using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SideCarMonitor.Services;

/// <summary>
/// Gives the agent Home Assistant through its MCP server, with the token and the safety checks kept in the kiosk
/// (outside the agent's sandbox):
/// - every call is logged to %APPDATA%\SideCarMonitor\home-actions.log (JSON lines)
/// - reading state is always allowed; background (review) turns can never control the home
/// - every control action is checked by the decision model for odd activity (not what you asked for, odd time,
///   unrelated to the conversation, a burst); an odd action is blocked and home control is paused
/// - a burst of control actions pauses it too; only you can resume (kiosk button)
/// </summary>
public sealed class HomeAssistantBridge : IDisposable
{
    public static string TokenPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SideCarMonitor", "ha-token.txt");
    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SideCarMonitor", "home-actions.log");

    /// <summary>Tools that only read; everything else counts as controlling the home.</summary>
    private static readonly HashSet<string> ReadOnlyTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "homeassistant__GetLiveContext", "llm__GetDateTime", "todo__get_items",
    };

    private const double OddThreshold = 0.6;
    private const int BurstLimit = 8;                       // control actions…
    private static readonly TimeSpan BurstWindow = TimeSpan.FromMinutes(2); // …within this window

    private readonly Uri _mcp;
    private readonly string _token;
    private readonly EventJudge? _decision;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly Queue<(DateTime At, string What)> _recent = new();
    private readonly object _lock = new();
    private JsonArray? _tools;
    private int _rpcId;

    /// <summary>Why home control is paused, or null when it isn't.</summary>
    public string? PausedReason { get; private set; }
    /// <summary>Raised (on a background thread) when home control is paused or resumed.</summary>
    public event Action<string?>? PausedChanged;

    private HomeAssistantBridge(Uri baseUrl, string token, EventJudge? decision)
    {
        _mcp = new Uri(baseUrl, "api/mcp");
        _token = token;
        _decision = decision;
    }

    /// <summary>Null when there's no token file (Home Assistant stays off).</summary>
    public static HomeAssistantBridge? Create(string? url, EventJudge? decision)
    {
        if (!File.Exists(TokenPath)) return null;
        var token = File.ReadAllText(TokenPath).Trim();
        if (token.Length == 0) return null;
        return new HomeAssistantBridge(new Uri(string.IsNullOrWhiteSpace(url) ? "http://homeassistant.local:8123/" : url), token, decision);
    }

    public void Resume()
    {
        if (PausedReason is null) return;
        PausedReason = null;
        lock (_lock) _recent.Clear();
        Log(new JsonObject { ["event"] = "resumed", ["by"] = "user" });
        PausedChanged?.Invoke(null);
    }

    private void Pause(string reason)
    {
        PausedReason = reason;
        Log(new JsonObject { ["event"] = "paused", ["reason"] = reason });
        PausedChanged?.Invoke(reason);
    }

    // ---- Agent tools --------------------------------------------------------------------------

    /// <param name="call">The agent's tool call (home_tools, home_state, home_call).</param>
    /// <param name="userText">What you said in this turn (null for background turns).</param>
    public async Task<object?> HandleAsync(AgentHost.ToolCall call, string? userText)
    {
        try
        {
            switch (call.Name)
            {
                case "home_tools":
                    return new { tools = await ToolsAsync(), paused = PausedReason };
                case "home_state":
                    return await CallReadAsync("homeassistant__GetLiveContext", new JsonObject(), call.Turn);
                case "home_call":
                    var tool = call.Args?["tool"]?.GetValue<string>() ?? "";
                    var args = ParseArgs(call.Args?["arguments"]);
                    return ReadOnlyTools.Contains(tool)
                        ? await CallReadAsync(tool, args, call.Turn)
                        : await CallControlAsync(tool, args, call.Turn, userText);
                default:
                    return new { error = $"Unknown home tool '{call.Name}'." };
            }
        }
        catch (Exception ex)
        {
            return new { error = $"Home Assistant: {ex.Message}" };
        }
    }

    private static JsonObject ParseArgs(JsonNode? node) => node switch
    {
        JsonObject o => o.DeepClone().AsObject(),
        JsonValue v when v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) =>
            JsonNode.Parse(s) as JsonObject ?? new JsonObject(),
        _ => new JsonObject(),
    };

    private async Task<object?> CallReadAsync(string tool, JsonObject args, string turn)
    {
        var result = await ToolCallAsync(tool, args);
        Log(new JsonObject { ["event"] = "read", ["tool"] = tool, ["args"] = args.DeepClone(), ["turn"] = turn });
        return result;
    }

    private async Task<object?> CallControlAsync(string tool, JsonObject args, string turn, string? userText)
    {
        var entry = new JsonObject
        {
            ["event"] = "control", ["tool"] = tool, ["args"] = args.DeepClone(), ["turn"] = turn, ["asked"] = userText,
        };

        if (PausedReason is { } paused)
            return Blocked(entry, $"Home control is paused ({paused}). Only the user can resume it, on the kiosk.");

        if (turn.StartsWith("review-", StringComparison.Ordinal) || userText is null)
            return Blocked(entry, "Background tasks can't control the home; only a direct request from the user can.");

        var what = $"{tool} {args.ToJsonString()}";
        List<string> recent;
        lock (_lock)
        {
            while (_recent.Count > 0 && DateTime.Now - _recent.Peek().At > BurstWindow) _recent.Dequeue();
            recent = _recent.Select(r => $"{r.At:h:mm:ss tt} {r.What}").ToList();
        }
        if (recent.Count >= BurstLimit)
        {
            Pause($"{recent.Count + 1} control actions in {BurstWindow.TotalMinutes:0} minutes");
            return Blocked(entry, $"Paused home control: too many actions in a short time. The user must resume it on the kiosk.");
        }

        // Ask the decision model whether this looks odd.
        if (_decision is not null)
        {
            double odd;
            try
            {
                odd = await OddScoreAsync(what, userText, recent);
            }
            catch (Exception ex)
            {
                // If the check can't run, don't act blind on the home.
                return Blocked(entry, $"Couldn't run the safety check ({ex.Message}); nothing was changed. Try again.");
            }
            entry["odd"] = Math.Round(odd, 3);
            if (odd >= OddThreshold)
            {
                Pause($"odd action blocked: {tool}");
                return Blocked(entry, "Blocked and paused home control: this action looked unusual for what the user asked " +
                                      $"(odd score {odd:0.00}). Tell the user; they can resume on the kiosk.");
            }
        }

        var result = await ToolCallAsync(tool, args);
        lock (_lock) _recent.Enqueue((DateTime.Now, what));
        entry["result"] = "done";
        Log(entry);
        return result;
    }

    private object Blocked(JsonObject entry, string message)
    {
        entry["result"] = "blocked";
        entry["why"] = message;
        Log(entry);
        return new { error = message, blocked = true };
    }

    private Task<double> OddScoreAsync(string action, string userText, List<string> recent) =>
        _decision!.OddActionAsync(new JsonObject
        {
            ["now"] = DateTime.Now.ToString("dddd h:mm tt"),
            ["user_said"] = userText,
            ["assistant_action"] = action,
            ["recent_home_actions"] = new JsonArray(recent.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
        });

    // ---- MCP (Home Assistant's server answers plain JSON-RPC over HTTP) ------------------------

    private async Task<JsonArray> ToolsAsync()
    {
        if (_tools is not null) return _tools;
        var result = await RpcAsync("tools/list", new JsonObject());
        var list = new JsonArray();
        foreach (var t in result?["tools"]?.AsArray() ?? [])
            list.Add(new JsonObject
            {
                ["name"] = t?["name"]?.DeepClone(),
                ["description"] = t?["description"]?.DeepClone(),
                ["arguments"] = t?["inputSchema"]?["properties"]?.DeepClone(),
                ["controls_home"] = !ReadOnlyTools.Contains(t?["name"]?.GetValue<string>() ?? ""),
            });
        return _tools = list;
    }

    private async Task<object?> ToolCallAsync(string tool, JsonObject args)
    {
        var result = await RpcAsync("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = args });
        var text = string.Join("\n", (result?["content"]?.AsArray() ?? []).Select(c => c?["text"]?.GetValue<string>()).Where(t => t is not null));
        var isError = result?["isError"]?.GetValue<bool>() == true;
        return isError ? new { error = text } : new { result = text };
    }

    private async Task<JsonNode?> RpcAsync(string method, JsonObject @params)
    {
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = Interlocked.Increment(ref _rpcId), ["method"] = method, ["params"] = @params,
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, _mcp)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        req.Headers.Accept.ParseAdd("application/json");
        req.Headers.Accept.ParseAdd("text/event-stream");
        using var res = await _http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"HTTP {(int)res.StatusCode}");
        // Streamable HTTP may answer as an SSE event; take its data line.
        if (res.Content.Headers.ContentType?.MediaType == "text/event-stream")
            text = string.Join("", text.Split('\n').Where(l => l.StartsWith("data:")).Select(l => l[5..].Trim()));
        var json = JsonNode.Parse(text);
        if (json?["error"] is { } err) throw new InvalidOperationException(err["message"]?.GetValue<string>() ?? "MCP error");
        return json?["result"];
    }

    private static readonly object LogLock = new();

    private static void Log(JsonObject entry)
    {
        entry["at"] = DateTime.Now.ToString("o");
        try { lock (LogLock) File.AppendAllText(LogPath, entry.ToJsonString() + Environment.NewLine); }
        catch { /* logging must never break the call */ }
    }

    public void Dispose() => _http.Dispose();
}
