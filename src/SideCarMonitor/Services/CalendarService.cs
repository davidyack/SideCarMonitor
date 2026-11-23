using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace SideCarMonitor.Services;

/// <summary>One meeting or appointment from one of your calendars (times are local).</summary>
public sealed record CalEvent(string Calendar, string Subject, DateTime Start, DateTime End, bool AllDay, string ShowAs,
    string Location, string JoinUrl, string Organizer, string WebLink);

/// <summary>
/// Your M365 calendars (e.g. CTC and PMCollab) through Microsoft Graph, using the "SideCar Calendar" app
/// registration (multi-tenant public client, delegated Calendars.ReadWrite). Each account is signed in once with
/// "SideCarMonitor.exe --calendar-login CTC"; tokens are kept in a DPAPI-encrypted MSAL cache under
/// %LOCALAPPDATA%\SideCarMonitor. Only the kiosk talks to Graph; the agent gets tools.
/// Reading is unrestricted; adding is limited to one appointment per hour (see CalendarTools).
/// </summary>
public sealed class CalendarService
{
    public const string ClientId = "41eef356-6325-4650-96e5-d925e07ff905";
    private static readonly string[] Scopes = ["Calendars.ReadWrite", "User.Read"];
    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SideCarMonitor");
    private static readonly string AccountsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SideCarMonitor", "calendars.json");

    /// <summary>Label ("CTC") -> MSAL home account id.</summary>
    private Dictionary<string, string> _accounts = [];
    private IPublicClientApplication? _app;
    private readonly HttpClient _http = new() { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/"), Timeout = TimeSpan.FromSeconds(30) };
    private List<CalEvent> _events = [];
    private DateTime _loadedAt;

    public IReadOnlyList<string> Calendars => _accounts.Keys.ToList();
    public IReadOnlyList<CalEvent> Events => _events;
    /// <summary>Per-calendar problem (e.g. needs sign-in), shown on the kiosk.</summary>
    public Dictionary<string, string> Problems { get; } = [];
    public event Action? Changed;

    private async Task<IPublicClientApplication> AppAsync()
    {
        if (_app is not null) return _app;
        var app = PublicClientApplicationBuilder.Create(ClientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, AadAuthorityAudience.AzureAdMultipleOrgs)
            .WithRedirectUri("http://localhost")
            .Build();
        Directory.CreateDirectory(Folder);
        var cache = await MsalCacheHelper.CreateAsync(new StorageCreationPropertiesBuilder("calendar.msalcache", Folder).Build());
        cache.RegisterCache(app.UserTokenCache); // encrypted with DPAPI for this Windows user
        if (File.Exists(AccountsPath))
            _accounts = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(AccountsPath)) ?? [];
        return _app = app;
    }

    /// <summary>Opens the browser to sign in one account and remembers it under <paramref name="label"/>.</summary>
    public async Task<string> SignInAsync(string label)
    {
        var app = await AppAsync();
        var result = await app.AcquireTokenInteractive(Scopes).WithPrompt(Prompt.SelectAccount).ExecuteAsync();
        _accounts[label] = result.Account.HomeAccountId.Identifier;
        File.WriteAllText(AccountsPath, JsonSerializer.Serialize(_accounts, new JsonSerializerOptions { WriteIndented = true }));
        return result.Account.Username;
    }

    private async Task<string> TokenAsync(string label)
    {
        var app = await AppAsync();
        var account = (await app.GetAccountsAsync()).FirstOrDefault(a => a.HomeAccountId.Identifier == _accounts[label])
                      ?? throw new InvalidOperationException($"{label} needs signing in again");
        try { return (await app.AcquireTokenSilent(Scopes, account).ExecuteAsync()).AccessToken; }
        catch (MsalUiRequiredException) { throw new InvalidOperationException($"{label} needs signing in again"); }
    }

    private async Task<JsonNode?> GraphAsync(string label, HttpMethod method, string path, JsonObject? body = null)
    {
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(label));
        req.Headers.Add("Prefer", "outlook.timezone=\"UTC\"");
        if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var res = await _http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"{label}: Graph {(int)res.StatusCode} {JsonNode.Parse(text)?["error"]?["message"]}");
        return text.Length > 0 ? JsonNode.Parse(text) : null;
    }

    /// <summary>Reloads both calendars for today and the next 14 days (call every few minutes).</summary>
    public async Task RefreshAsync()
    {
        await AppAsync();
        var from = DateTime.Today;
        var to = from.AddDays(15);
        var all = new List<CalEvent>();
        foreach (var label in _accounts.Keys)
        {
            try
            {
                var path = $"me/calendarView?startDateTime={from.ToUniversalTime():o}&endDateTime={to.ToUniversalTime():o}" +
                           "&$select=subject,start,end,isAllDay,showAs,isCancelled,location,onlineMeeting,organizer,webLink&$orderby=start/dateTime&$top=250";
                var json = await GraphAsync(label, HttpMethod.Get, path);
                foreach (var e in json?["value"]?.AsArray() ?? [])
                {
                    if (e?["isCancelled"]?.GetValue<bool>() == true) continue;
                    all.Add(new CalEvent(label,
                        e!["subject"]?.GetValue<string>() ?? "(no title)",
                        Local(e["start"]), Local(e["end"]),
                        e["isAllDay"]?.GetValue<bool>() == true,
                        e["showAs"]?.GetValue<string>() ?? "busy",
                        e["location"]?["displayName"]?.GetValue<string>() ?? "",
                        e["onlineMeeting"]?["joinUrl"]?.GetValue<string>() ?? "",
                        e["organizer"]?["emailAddress"]?["name"]?.GetValue<string>() ?? "",
                        e["webLink"]?.GetValue<string>() ?? ""));
                }
                Problems.Remove(label);
            }
            catch (Exception ex)
            {
                Problems[label] = ex.Message;
            }
        }
        _events = all.OrderBy(e => e.Start).ToList();
        _loadedAt = DateTime.Now;
        Changed?.Invoke();
    }

    public bool IsStale => DateTime.Now - _loadedAt > TimeSpan.FromMinutes(5);

    private static DateTime Local(JsonNode? dt) =>
        DateTime.SpecifyKind(DateTime.Parse(dt?["dateTime"]?.GetValue<string>() ?? DateTime.UtcNow.ToString("o")), DateTimeKind.Utc).ToLocalTime();

    /// <summary>Busy blocks across all calendars (free and working-elsewhere don't count).</summary>
    public IEnumerable<CalEvent> Busy => _events.Where(e => !e.AllDay && e.ShowAs is not ("free" or "workingElsewhere"));

    /// <summary>Slots of at least <paramref name="minutes"/> free in every calendar, within working hours.</summary>
    public List<(DateTime Start, DateTime End)> FreeSlots(DateTime from, DateTime to, int minutes, int dayStartHour = 8, int dayEndHour = 17)
    {
        var slots = new List<(DateTime, DateTime)>();
        for (var day = from.Date; day <= to.Date; day = day.AddDays(1))
        {
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            var cursor = day.AddHours(dayStartHour);
            var end = day.AddHours(dayEndHour);
            if (cursor < from) cursor = RoundUp(from);
            foreach (var b in Busy.Where(b => b.End > cursor && b.Start < end).OrderBy(b => b.Start))
            {
                if (b.Start - cursor >= TimeSpan.FromMinutes(minutes)) slots.Add((cursor, b.Start));
                if (b.End > cursor) cursor = b.End;
            }
            if (end - cursor >= TimeSpan.FromMinutes(minutes)) slots.Add((cursor, end));
        }
        return slots;
    }

    private static DateTime RoundUp(DateTime t) => new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0).AddMinutes(t.Minute <= 30 ? 30 : 60);

    /// <summary>Creates an appointment on one calendar. Callers must enforce the limits (CalendarTools).</summary>
    public async Task<CalEvent> AddAsync(string label, string subject, DateTime start, DateTime end, string? location, string? notes)
    {
        if (!_accounts.ContainsKey(label)) throw new InvalidOperationException($"Unknown calendar '{label}'. Use one of: {string.Join(", ", _accounts.Keys)}");
        var body = new JsonObject
        {
            ["subject"] = subject,
            ["start"] = new JsonObject { ["dateTime"] = start.ToUniversalTime().ToString("s"), ["timeZone"] = "UTC" },
            ["end"] = new JsonObject { ["dateTime"] = end.ToUniversalTime().ToString("s"), ["timeZone"] = "UTC" },
            ["categories"] = new JsonArray("SideCar"),
        };
        if (!string.IsNullOrWhiteSpace(location)) body["location"] = new JsonObject { ["displayName"] = location };
        if (!string.IsNullOrWhiteSpace(notes)) body["body"] = new JsonObject { ["contentType"] = "text", ["content"] = notes };
        var created = await GraphAsync(label, HttpMethod.Post, "me/events", body);
        await RefreshAsync();
        return new CalEvent(label, subject, start, end, false, "busy", location ?? "", "", "", created?["webLink"]?.GetValue<string>() ?? "");
    }
}
