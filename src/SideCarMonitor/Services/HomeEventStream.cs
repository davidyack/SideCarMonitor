using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace SideCarMonitor.Services;

/// <summary>One thing that happened in the house, for the Activity feed.</summary>
/// <param name="Source">"door" (doors, windows, locks, garage, alarms) or "house" (automations, other motion).</param>
public sealed record HouseEvent(string Source, string Where, string What, string Glyph, DateTime At, string Who, bool Risky);

/// <summary>
/// Listens to Home Assistant's live event stream (WebSocket API, same token as the MCP bridge) and turns the
/// state changes worth knowing about into feed events: doors, windows, locks, garage and gates, alarms,
/// leaks and smoke, and automations that ran. Lights, media and readings
/// are skipped (too noisy); SideCar's own actions come from the bridge's log instead.
/// Read-only, kiosk-side: the agent never sees this connection.
/// </summary>
public sealed class HomeEventStream : IDisposable
{
    private readonly Uri _ws;
    private readonly string _token;
    private readonly IReadOnlyCollection<string> _cameraNames;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, string> _users = []; // HA user id -> name
    private bool _wasDown;

    /// <summary>Raised on a background thread.</summary>
    public event Action<HouseEvent>? Happened;

    private HomeEventStream(Uri baseUrl, string token, IReadOnlyCollection<string> cameraNames)
    {
        var b = new UriBuilder(new Uri(baseUrl, "api/websocket")) { Scheme = baseUrl.Scheme == "https" ? "wss" : "ws" };
        _ws = b.Uri;
        _token = token;
        _cameraNames = cameraNames;
    }

    public static HomeEventStream? Create(string? url, IReadOnlyCollection<string> cameraNames)
    {
        if (!File.Exists(HomeAssistantBridge.TokenPath)) return null;
        var token = File.ReadAllText(HomeAssistantBridge.TokenPath).Trim();
        if (token.Length == 0) return null;
        return new HomeEventStream(new Uri(string.IsNullOrWhiteSpace(url) ? "http://homeassistant.local:8123/" : url), token, cameraNames);
    }

    public void Start() => _ = Task.Run(() => RunAsync(_cts.Token));

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await ListenAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ha-events] {ex.Message}; reconnecting");
                if (!_wasDown) SystemEvents.Report("Lost connection to Home Assistant", $"{ex.Message}; retrying", alert: true, dedupe: TimeSpan.FromMinutes(30));
                _wasDown = true;
            }
            try { await Task.Delay(TimeSpan.FromSeconds(15), ct); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(_ws, ct);
        await ReceiveAsync(ws, ct); // auth_required
        await SendAsync(ws, new JsonObject { ["type"] = "auth", ["access_token"] = _token }, ct);
        var auth = await ReceiveAsync(ws, ct);
        if (auth?["type"]?.GetValue<string>() != "auth_ok") throw new InvalidOperationException("Home Assistant rejected the token");
        if (_wasDown) SystemEvents.Report("Home Assistant connection restored", "live events are flowing again");
        _wasDown = false;

        await SendAsync(ws, new JsonObject { ["id"] = 1, ["type"] = "config/auth/list" }, ct); // to name who did it (admin only)
        await SendAsync(ws, new JsonObject { ["id"] = 2, ["type"] = "subscribe_events", ["event_type"] = "state_changed" }, ct);
        await SendAsync(ws, new JsonObject { ["id"] = 3, ["type"] = "subscribe_events", ["event_type"] = "automation_triggered" }, ct);

        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var msg = await ReceiveAsync(ws, ct);
            if (msg is null) break;
            if (msg["id"]?.GetValue<int>() == 1 && msg["result"] is JsonArray users)
            {
                foreach (var u in users)
                    if (u?["id"]?.GetValue<string>() is { } id) _users[id] = u["name"]?.GetValue<string>() ?? "someone";
                continue;
            }
            if (msg["type"]?.GetValue<string>() != "event") continue;
            var ev = msg["event"];
            var e = ev?["event_type"]?.GetValue<string>() switch
            {
                "state_changed" => FromStateChange(ev!),
                "automation_triggered" => new HouseEvent("house", ev!["data"]?["name"]?.GetValue<string>() ?? "Automation",
                    "Automation ran", "", DateTime.Now, "automation", false),
                _ => null,
            };
            if (e is not null) Happened?.Invoke(e);
        }
    }

    private HouseEvent? FromStateChange(JsonNode ev)
    {
        var data = ev["data"];
        var entity = data?["entity_id"]?.GetValue<string>() ?? "";
        var oldState = data?["old_state"]?["state"]?.GetValue<string>();
        var newNode = data?["new_state"];
        var state = newNode?["state"]?.GetValue<string>();
        if (state is null || oldState is null || state == oldState) return null;          // attribute-only change
        if (oldState is "unavailable" or "unknown" || state is "unavailable" or "unknown") return null; // device dropping out
        var name = newNode?["attributes"]?["friendly_name"]?.GetValue<string>() ?? entity;
        var cls = newNode?["attributes"]?["device_class"]?.GetValue<string>() ?? "";
        var domain = entity.Split('.')[0];
        var who = Who(ev["context"]);
        var hour = DateTime.Now.Hour;
        var night = hour < 6 || hour >= 22;

        switch (domain)
        {
            case "lock":
                var what = state switch { "locked" => "Locked", "unlocked" => "Unlocked", "jammed" => "Lock jammed", "open" => "Opened", _ => null };
                return what is null ? null : new HouseEvent("door", name, what, state == "locked" ? "" : "", DateTime.Now, who,
                    state is "unlocked" or "jammed" or "open" && night);
            case "cover" when cls is "garage" or "gate" or "door":
                if (state is not ("open" or "closed")) return null; // skip opening/closing in between
                return new HouseEvent("door", name, state == "open" ? $"{Title(cls)} opened" : $"{Title(cls)} closed", "", DateTime.Now, who,
                    state == "open" && night);
            case "alarm_control_panel":
                return new HouseEvent("door", name, $"Alarm {state.Replace('_', ' ')}", "", DateTime.Now, who, state == "triggered");
            case "binary_sensor":
                switch (cls)
                {
                    case "door" or "window" or "garage_door" or "opening":
                        if (state != "on" && state != "off") return null;
                        var thing = cls == "garage_door" ? "Garage door" : cls == "window" ? "Window" : "Door";
                        return new HouseEvent("door", name, state == "on" ? $"{thing} opened" : $"{thing} closed", "", DateTime.Now, who,
                            state == "on" && night);
                    case "smoke" or "gas" or "carbon_monoxide" or "moisture" or "safety" or "tamper" or "problem" when state == "on":
                        var alarm = cls switch { "moisture" => "Leak detected", "smoke" => "Smoke detected", "gas" => "Gas detected", "carbon_monoxide" => "CO detected", "tamper" => "Tampering detected", _ => "Problem" };
                        return new HouseEvent("door", name, alarm, "", DateTime.Now, who, true);
                    // Motion, occupancy and presence are left out: camera motion has its own events with pictures,
                    // and presence / in-bed sensors change all day.
                }
                return null;
            default:
                return null; // lights, media, sensors: too noisy for the feed
        }
    }

    private static string Title(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>"Dave", "automation" or "device" from the event's context.</summary>
    private string Who(JsonNode? context)
    {
        if (context?["user_id"]?.GetValue<string>() is { } uid)
            return _users.TryGetValue(uid, out var n) ? n : "someone in the app";
        return context?["parent_id"] is JsonValue ? "automation" : "device";
    }

    private static async Task SendAsync(ClientWebSocket ws, JsonObject msg, CancellationToken ct) =>
        await ws.SendAsync(Encoding.UTF8.GetBytes(msg.ToJsonString()), WebSocketMessageType.Text, true, ct);

    private static async Task<JsonNode?> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult r;
        do
        {
            r = await ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, r.Count);
        } while (!r.EndOfMessage);
        return JsonNode.Parse(ms.ToArray());
    }

    public void Dispose() { _cts.Cancel(); _cts.Dispose(); }
}
