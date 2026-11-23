using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SideCarMonitor.Services;

/// <summary>One post from X with its author.</summary>
public sealed record XPost(string Id, string AuthorId, string Author, string Username, string AuthorBio, string Text,
    DateTime Created, string ConversationId, int Likes, int Reposts, int Replies, bool IsRepost, bool IsReply)
{
    public string Link => $"https://x.com/{Username}/status/{Id}";
    public string ProfileUrl => $"https://x.com/{Username}";
}

/// <summary>
/// The official X API v2 with your own account (OAuth 2.0 user context, PKCE). The app's Client ID (and Secret, for a
/// confidential app) are in %APPDATA%\SideCarMonitor\x-client-id.txt; the token is kept DPAPI-encrypted in
/// %LOCALAPPDATA%\SideCarMonitor\x-token.bin. Pay-per-use: reads cost per post, so callers keep volumes small.
/// </summary>
public sealed class XClient
{
    public const string Redirect = "http://127.0.0.1:53682/callback";
    private const string Scopes = "tweet.read users.read bookmark.read bookmark.write follows.read follows.write offline.access";
    private static readonly string IdPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SideCarMonitor", "x-client-id.txt");
    private static readonly string TokenPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SideCarMonitor", "x-token.bin");

    private readonly HttpClient _http = new() { BaseAddress = new Uri("https://api.x.com/2/"), Timeout = TimeSpan.FromSeconds(30) };
    private readonly string _clientId;
    private readonly string? _secret;
    private JsonObject? _token; // access_token, refresh_token, expires_at
    private string? _me;

    private XClient(string clientId, string? secret) { _clientId = clientId; _secret = secret; }

    public static XClient? Create()
    {
        if (!File.Exists(IdPath)) return null;
        var lines = File.ReadAllLines(IdPath).Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        return lines.Length == 0 ? null : new XClient(lines[0], lines.Length > 1 ? lines[1] : null);
    }

    public bool SignedIn => File.Exists(TokenPath);

    // ---- Sign-in (PKCE): opens the browser, catches the redirect on 127.0.0.1:53682 ----

    public async Task<string> SignInAsync()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        var url = "https://x.com/i/oauth2/authorize?response_type=code" +
                  $"&client_id={Uri.EscapeDataString(_clientId)}&redirect_uri={Uri.EscapeDataString(Redirect)}" +
                  $"&scope={Uri.EscapeDataString(Scopes)}&state={state}&code_challenge={challenge}&code_challenge_method=S256";

        using var listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:53682/callback/");
        listener.Start();
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        var ctx = await listener.GetContextAsync().WaitAsync(TimeSpan.FromMinutes(10));
        var q = System.Web.HttpUtility.ParseQueryString(ctx.Request.Url?.Query ?? "");
        var ok = q["state"] == state && q["code"] is not null;
        var page = Encoding.UTF8.GetBytes(ok ? "<html><body style='font-family:sans-serif'><h2>SideCar is connected to X.</h2>You can close this tab.</body></html>"
                                             : $"<html><body><h2>Sign-in didn't complete</h2>{WebUtility.HtmlEncode(q["error"] ?? "")}</body></html>");
        ctx.Response.ContentType = "text/html";
        await ctx.Response.OutputStream.WriteAsync(page);
        ctx.Response.Close();
        if (!ok) throw new InvalidOperationException($"X sign-in failed: {q["error"] ?? "state mismatch"}");

        await TokenRequestAsync(new()
        {
            ["grant_type"] = "authorization_code", ["code"] = q["code"]!, ["redirect_uri"] = Redirect,
            ["code_verifier"] = verifier, ["client_id"] = _clientId,
        });
        var me = await GetAsync("users/me");
        return "@" + me?["data"]?["username"]?.GetValue<string>();
    }

    private async Task TokenRequestAsync(Dictionary<string, string> form)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "oauth2/token") { Content = new FormUrlEncodedContent(form) };
        if (_secret is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_clientId}:{_secret}")));
        using var res = await _http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"X token request failed ({(int)res.StatusCode}): {Brief(text)}");
        var t = JsonNode.Parse(text)!.AsObject();
        t["expires_at"] = DateTimeOffset.UtcNow.AddSeconds(t["expires_in"]?.GetValue<int>() ?? 7200).ToUnixTimeSeconds();
        if (t["refresh_token"] is null && _token?["refresh_token"] is { } keep) t["refresh_token"] = keep.GetValue<string>();
        _token = t;
        Directory.CreateDirectory(Path.GetDirectoryName(TokenPath)!);
        File.WriteAllBytes(TokenPath, ProtectedData.Protect(Encoding.UTF8.GetBytes(t.ToJsonString()), null, DataProtectionScope.CurrentUser));
    }

    private async Task<string> AccessTokenAsync()
    {
        if (_token is null)
        {
            if (!File.Exists(TokenPath)) throw new InvalidOperationException("X isn't signed in (run SideCarMonitor.exe --x-login).");
            _token = JsonNode.Parse(Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(TokenPath), null, DataProtectionScope.CurrentUser)))!.AsObject();
        }
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > (_token["expires_at"]?.GetValue<long>() ?? 0) - 120)
            await TokenRequestAsync(new()
            {
                ["grant_type"] = "refresh_token", ["refresh_token"] = _token["refresh_token"]?.GetValue<string>() ?? "", ["client_id"] = _clientId,
            });
        return _token["access_token"]!.GetValue<string>();
    }

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonObject? body = null)
    {
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessTokenAsync());
        if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var res = await _http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        if ((int)res.StatusCode == 402) throw new InvalidOperationException("X API credits are used up (add credits in the X console).");
        if ((int)res.StatusCode == 429) throw new InvalidOperationException("X API rate limit reached; try again later.");
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"X API {(int)res.StatusCode}: {Brief(text)}");
        return text.Length > 0 ? JsonNode.Parse(text) : null;
    }

    private Task<JsonNode?> GetAsync(string path) => SendAsync(HttpMethod.Get, path);

    private async Task<string> MeAsync() =>
        _me ??= (await GetAsync("users/me"))?["data"]?["id"]?.GetValue<string>() ?? throw new InvalidOperationException("Couldn't read your X account");

    private const string PostFields = "tweet.fields=created_at,public_metrics,conversation_id,referenced_tweets,note_tweet,author_id" +
                                      "&expansions=author_id&user.fields=name,username";

    /// <summary>Your home timeline, newest first (reverse chronological). Each post costs a read.</summary>
    public async Task<List<XPost>> TimelineAsync(int max = 50, string? sinceId = null)
    {
        var since = sinceId is null ? "" : $"&since_id={sinceId}"; // only what's new: never pay for a post twice
        var json = await GetAsync($"users/{await MeAsync()}/timelines/reverse_chronological?max_results={Math.Clamp(max, 5, 100)}&exclude=replies{since}&{PostFields}");
        return Parse(json);
    }

    /// <summary>Replies to a post (search of the last 7 days by conversation). Each reply costs a read.</summary>
    public async Task<List<XPost>> RepliesAsync(string conversationId, int max = 50)
    {
        var q = Uri.EscapeDataString($"conversation_id:{conversationId} is:reply");
        var json = await GetAsync($"tweets/search/recent?query={q}&max_results={Math.Clamp(max, 10, 50)}&sort_order=relevancy&{PostFields}");
        return Parse(json);
    }

    /// <summary>How many accounts you follow (one profile read).</summary>
    public async Task<int> FollowingCountAsync() =>
        (await GetAsync("users/me?user.fields=public_metrics"))?["data"]?["public_metrics"]?["following_count"]?.GetValue<int>() ?? 0;

    /// <summary>Unfollows the author (your confirmed tap).</summary>
    public async Task UnfollowAsync(string userId) =>
        await SendAsync(HttpMethod.Delete, $"users/{await MeAsync()}/following/{userId}");

    /// <summary>Bookmarks the post on X (your Save).</summary>
    public async Task BookmarkAsync(string postId) =>
        await SendAsync(HttpMethod.Post, $"users/{await MeAsync()}/bookmarks", new JsonObject { ["tweet_id"] = postId });

    private static List<XPost> Parse(JsonNode? json)
    {
        var users = (json?["includes"]?["users"]?.AsArray() ?? []).Where(u => u is not null)
            .ToDictionary(u => u!["id"]!.GetValue<string>(), u => u!);
        var list = new List<XPost>();
        foreach (var t in json?["data"]?.AsArray() ?? [])
        {
            if (t is null) continue;
            var authorId = t["author_id"]?.GetValue<string>() ?? "";
            users.TryGetValue(authorId, out var u);
            var refs = (t["referenced_tweets"]?.AsArray() ?? []).Select(r => r?["type"]?.GetValue<string>()).ToList();
            var m = t["public_metrics"];
            list.Add(new XPost(
                t["id"]!.GetValue<string>(), authorId,
                u?["name"]?.GetValue<string>() ?? "", u?["username"]?.GetValue<string>() ?? "", u?["description"]?.GetValue<string>() ?? "",
                t["note_tweet"]?["text"]?.GetValue<string>() ?? t["text"]?.GetValue<string>() ?? "",
                DateTime.TryParse(t["created_at"]?.GetValue<string>(), out var c) ? c.ToLocalTime() : DateTime.Now,
                t["conversation_id"]?.GetValue<string>() ?? t["id"]!.GetValue<string>(),
                m?["like_count"]?.GetValue<int>() ?? 0, m?["retweet_count"]?.GetValue<int>() ?? 0, m?["reply_count"]?.GetValue<int>() ?? 0,
                refs.Contains("retweeted"), refs.Contains("replied_to")));
        }
        return list;
    }

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Brief(string s) { var m = JsonNode.Parse(s.Length > 0 && s[0] == '{' ? s : "{}"); return (m?["detail"] ?? m?["error_description"] ?? m?["title"])?.ToString() ?? s[..Math.Min(200, s.Length)]; }
}
