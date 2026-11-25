using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace SideCarMonitor.Services;

/// <summary>
/// PMCollab's MCP server (default https://dev1.pmcollab.ai/mcp/pmc, assistant.pmcollabMcp), signed in as you with OAuth:
/// SideCar registers itself once (dynamic client registration, public client, PKCE), you sign in with
/// "SideCarMonitor.exe --pmcollab-login", and the token is DPAPI-encrypted in %LOCALAPPDATA%\SideCarMonitor\pmcollab-token.bin.
/// Calls are plain MCP over streamable HTTP (JSON or SSE replies). Used to push an idea into PMCollab.
/// </summary>
public sealed class PmCollabMcp
{
    private const string Redirect = "http://127.0.0.1:53683/callback";
    private const string Scopes = "pmc:workspace:read pmc:workspace:write pmc:ideachat:write";
    private static readonly string AppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SideCarMonitor");
    private static readonly string ClientPath = Path.Combine(AppData, "pmcollab-client.json");
    private static readonly string TokenPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SideCarMonitor", "pmcollab-token.bin");

    private readonly Uri _mcp;
    private readonly Uri _origin;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private JsonObject? _token;
    private string? _session;
    private int _nextId = 1;

    public PmCollabMcp(string mcpUrl)
    {
        _mcp = new Uri(mcpUrl);
        _origin = new Uri(_mcp.GetLeftPart(UriPartial.Authority));
    }

    public bool SignedIn => File.Exists(TokenPath);
    public string Host => _mcp.Host;

    // ---- Registration and sign-in ----

    private async Task<string> ClientIdAsync()
    {
        if (File.Exists(ClientPath) && JsonNode.Parse(File.ReadAllText(ClientPath))?["client_id"]?.GetValue<string>() is { Length: > 0 } id
            && JsonNode.Parse(File.ReadAllText(ClientPath))?["issuer"]?.GetValue<string>() == _origin.ToString())
            return id;
        var body = new JsonObject
        {
            ["client_name"] = "SideCar kiosk", ["redirect_uris"] = new JsonArray(Redirect),
            ["grant_types"] = new JsonArray("authorization_code", "refresh_token"), ["response_types"] = new JsonArray("code"),
            ["token_endpoint_auth_method"] = "none", ["scope"] = Scopes,
        };
        using var res = await _http.PostAsync(new Uri(_origin, "register"), new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"PMCollab client registration failed ({(int)res.StatusCode}): {Brief(text)}");
        var clientId = JsonNode.Parse(text)?["client_id"]?.GetValue<string>() ?? throw new InvalidOperationException("No client_id from PMCollab");
        Directory.CreateDirectory(AppData);
        File.WriteAllText(ClientPath, new JsonObject { ["issuer"] = _origin.ToString(), ["client_id"] = clientId }.ToJsonString());
        return clientId;
    }

    public async Task<string> SignInAsync()
    {
        var clientId = await ClientIdAsync();
        var verifier = B64(RandomNumberGenerator.GetBytes(32));
        var challenge = B64(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = B64(RandomNumberGenerator.GetBytes(16));
        var url = new Uri(_origin, "authorize") + "?response_type=code" +
                  $"&client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(Redirect)}" +
                  $"&scope={Uri.EscapeDataString(Scopes)}&state={state}&code_challenge={challenge}&code_challenge_method=S256" +
                  $"&resource={Uri.EscapeDataString(_mcp.ToString())}";
        using var listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:53683/callback/");
        listener.Start();
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        var ctx = await listener.GetContextAsync().WaitAsync(TimeSpan.FromMinutes(10));
        var q = System.Web.HttpUtility.ParseQueryString(ctx.Request.Url?.Query ?? "");
        var ok = q["state"] == state && q["code"] is not null;
        var page = Encoding.UTF8.GetBytes(ok ? "<html><body style='font-family:sans-serif'><h2>SideCar is connected to PMCollab.</h2>You can close this tab.</body></html>"
                                             : $"<html><body><h2>Sign-in didn't complete</h2>{WebUtility.HtmlEncode(q["error_description"] ?? q["error"] ?? "")}</body></html>");
        ctx.Response.ContentType = "text/html";
        await ctx.Response.OutputStream.WriteAsync(page);
        ctx.Response.Close();
        if (!ok) throw new InvalidOperationException($"PMCollab sign-in failed: {q["error"] ?? "state mismatch"}");
        await TokenRequestAsync(new()
        {
            ["grant_type"] = "authorization_code", ["code"] = q["code"]!, ["redirect_uri"] = Redirect,
            ["code_verifier"] = verifier, ["client_id"] = clientId, ["resource"] = _mcp.ToString(),
        });
        return "signed in";
    }

    private async Task TokenRequestAsync(Dictionary<string, string> form)
    {
        using var res = await _http.PostAsync(new Uri(_origin, "token"), new FormUrlEncodedContent(form));
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"PMCollab token request failed ({(int)res.StatusCode}): {Brief(text)}");
        var t = JsonNode.Parse(text)!.AsObject();
        t["expires_at"] = DateTimeOffset.UtcNow.AddSeconds(t["expires_in"]?.GetValue<int>() ?? 3600).ToUnixTimeSeconds();
        if (t["refresh_token"] is null && _token?["refresh_token"] is { } keep) t["refresh_token"] = keep.GetValue<string>();
        _token = t;
        Directory.CreateDirectory(Path.GetDirectoryName(TokenPath)!);
        File.WriteAllBytes(TokenPath, ProtectedData.Protect(Encoding.UTF8.GetBytes(t.ToJsonString()), null, DataProtectionScope.CurrentUser));
    }

    private async Task<string> AccessTokenAsync()
    {
        if (_token is null)
        {
            if (!File.Exists(TokenPath)) throw new InvalidOperationException("PMCollab isn't signed in (run SideCarMonitor.exe --pmcollab-login).");
            _token = JsonNode.Parse(Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(TokenPath), null, DataProtectionScope.CurrentUser)))!.AsObject();
        }
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > (_token["expires_at"]?.GetValue<long>() ?? 0) - 120)
            await TokenRequestAsync(new()
            {
                ["grant_type"] = "refresh_token", ["refresh_token"] = _token["refresh_token"]?.GetValue<string>() ?? "",
                ["client_id"] = await ClientIdAsync(), ["resource"] = _mcp.ToString(),
            });
        return _token["access_token"]!.GetValue<string>();
    }

    // ---- MCP ----

    private async Task<JsonNode?> RpcAsync(string method, JsonObject? @params, bool notification = false)
    {
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (@params is not null) msg["params"] = @params;
        if (!notification) msg["id"] = _nextId++;
        using var req = new HttpRequestMessage(HttpMethod.Post, _mcp) { Content = new StringContent(msg.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessTokenAsync());
        req.Headers.Accept.ParseAdd("application/json");
        req.Headers.Accept.ParseAdd("text/event-stream");
        if (_session is not null) req.Headers.Add("Mcp-Session-Id", _session);
        if (method != "initialize") req.Headers.Add("MCP-Protocol-Version", "2025-06-18");
        using var res = await _http.SendAsync(req);
        if (res.Headers.TryGetValues("Mcp-Session-Id", out var sid)) _session = sid.FirstOrDefault();
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"PMCollab {method} failed ({(int)res.StatusCode}): {Brief(text)}");
        if (notification || text.Length == 0) return null;
        // SSE: take the data line(s) of the response event.
        if (res.Content.Headers.ContentType?.MediaType == "text/event-stream")
            text = string.Join("", text.Split('\n').Where(l => l.StartsWith("data:")).Select(l => l[5..].Trim()).Where(l => l.StartsWith("{")).Take(1));
        var node = JsonNode.Parse(text);
        if (node?["error"] is { } err) throw new InvalidOperationException($"PMCollab {method}: {err["message"]}");
        return node?["result"];
    }

    private async Task EnsureSessionAsync()
    {
        if (_session is not null) return;
        await RpcAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "sidecar", ["version"] = "1.0" },
        });
        await RpcAsync("notifications/initialized", null, notification: true);
    }

    public async Task<JsonArray> ListToolsAsync()
    {
        await EnsureSessionAsync();
        var all = new JsonArray();
        string? cursor = null;
        do
        {
            var r = await RpcAsync("tools/list", cursor is null ? null : new JsonObject { ["cursor"] = cursor });
            foreach (var t in r?["tools"]?.AsArray() ?? []) all.Add(t?.DeepClone());
            cursor = r?["nextCursor"]?.GetValue<string>();
        } while (cursor is not null);
        return all;
    }

    public async Task<JsonNode?> CallToolAsync(string name, JsonObject args)
    {
        await EnsureSessionAsync();
        try { return await RpcAsync("tools/call", new JsonObject { ["name"] = name, ["arguments"] = args }); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("(404)") || ex.Message.Contains("(400)") && ex.Message.Contains("session", StringComparison.OrdinalIgnoreCase))
        {
            _session = null; // session expired: start a new one and retry once
            await EnsureSessionAsync();
            return await RpcAsync("tools/call", new JsonObject { ["name"] = name, ["arguments"] = args });
        }
    }

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Brief(string s) => s.Length > 300 ? s[..300] + "…" : s;
}
