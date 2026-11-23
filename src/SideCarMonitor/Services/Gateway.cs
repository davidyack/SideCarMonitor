using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace SideCarMonitor.Services;

/// <summary>
/// HTTPS API for the SideCar phone and watch apps, served by the kiosk on the home network.
/// - TLS uses a self-signed certificate kept in the user's certificate store; the phone pins its SHA-256 fingerprint.
/// - Every request must carry the pairing key (Authorization: Bearer ...), handed to the phone once via a QR code.
/// - The agent stays sandboxed: the phone talks to the kiosk, and the kiosk talks to the agent.
/// - Azure credentials stay on the kiosk: speech-to-text and text-to-speech happen here.
/// </summary>
public sealed class Gateway : IAsyncDisposable
{
    private const string CertSubject = "CN=SideCar Kiosk Gateway";

    public sealed record Dependencies(
        AgentHost? Agent,
        VoiceService? Voice,
        DispatcherQueue Ui,
        Func<object> Status,                        // runs on the UI thread
        Func<int, CancellationToken, Task<byte[]?>> Snapshot,
        SideCarTasks.TaskService? Tasks = null,
        Func<string, Task>? TakePass = null);

    private readonly int _port;
    private readonly Dependencies _deps;
    private WebApplication? _app;

    public string Key { get; private set; } = "";
    public string CertSha256 { get; private set; } = "";
    public string Url { get; private set; } = "";

    public static string KeyPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SideCarMonitor", "gateway-key.txt");

    public Gateway(int port, Dependencies deps)
    {
        _port = port;
        _deps = deps;
    }

    /// <summary>Tailscale address of this PC (100.64.0.0/10), if Tailscale is connected.</summary>
    public string? TailnetUrl { get; private set; }

    /// <summary>
    /// The pairing payload shown as a QR code: where to connect (home Wi-Fi first, then Tailscale for when
    /// you're away), the key, and the certificate to trust.
    /// </summary>
    public string PairingPayload => JsonSerializer.Serialize(new
    {
        v = 2,
        url = Url,
        urls = new[] { Url, TailnetUrl }.OfType<string>().ToArray(),
        key = Key,
        cert = CertSha256,
    });

    public async Task StartAsync()
    {
        Key = LoadOrCreateKey();
        var cert = LoadOrCreateCertificate();
        CertSha256 = Convert.ToHexString(SHA256.HashData(cert.RawData));
        Url = $"https://{LocalIPv4() ?? "localhost"}:{_port}";
        TailnetUrl = TailscaleIPv4() is { } ts ? $"https://{ts}:{_port}" : null;

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Any, _port, o => o.UseHttps(cert)));
        _app = builder.Build();
        _app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        _app.Use(RequireKey);
        Map(_app);
        await _app.StartAsync();
    }

    /// <summary>Makes a new pairing key; phones paired with the old one stop working.</summary>
    public void ResetPairing()
    {
        File.Delete(KeyPath);
        Key = LoadOrCreateKey();
    }

    // ---- Endpoints ------------------------------------------------------------------------

    private void Map(WebApplication app)
    {
        app.MapGet("/api/ping", () => Results.Ok(new { ok = true, name = "SideCar" }));

        app.MapGet("/api/status", async () => Results.Json(await OnUiAsync(_deps.Status)));

        app.MapGet("/api/cameras/{index:int}/snapshot", async (int index, CancellationToken ct) =>
            await _deps.Snapshot(index, ct) is { } jpeg ? Results.File(jpeg, "image/jpeg") : Results.NotFound());

        MapTasks(app);

        // Speech-to-text: body is 16 kHz, 16-bit, mono PCM WAV recorded on the phone or watch.
        app.MapPost("/api/listen", async (HttpRequest req, CancellationToken ct) =>
        {
            if (_deps.Voice is null) return Results.Problem("Voice isn't configured on the kiosk.");
            using var ms = new MemoryStream();
            await req.Body.CopyToAsync(ms, ct);
            var text = await _deps.Voice.TranscribeWavAsync(ms.ToArray(), ct);
            return Results.Ok(new { text });
        });

        // Text-to-speech in the kiosk's MAI voice; returns a WAV file.
        app.MapPost("/api/speak", async (HttpRequest req, CancellationToken ct) =>
        {
            if (_deps.Voice is null) return Results.Problem("Voice isn't configured on the kiosk.");
            var body = await JsonNode.ParseAsync(req.Body, cancellationToken: ct);
            var text = (string?)body?["text"] ?? "";
            var wav = await _deps.Voice.SynthesizeWavAsync(text, ct);
            return Results.File(wav, "audio/wav");
        });

        // Chat with the agent. Client sends {"type":"chat","text":"..."}; the server streams
        // {"type":"delta"|"message"|"tool"|"idle"|"error", "id", "text"/"name"/"message"} back.
        app.Map("/api/chat", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            await ChatSessionAsync(ws, ctx.RequestAborted);
        });
    }

    // ---- To-do list (the phone acts as you; the same rules as the kiosk apply) -----------------

    private void MapTasks(WebApplication app)
    {
        app.MapGet("/api/tasks", () =>
        {
            var t = _deps.Tasks;
            if (t is null) return Results.Problem("The to-do list isn't available.");
            var brief = t.Store.Read().Briefs.LastOrDefault();
            return Results.Json(new
            {
                today = t.Today.ToString("yyyy-MM-dd"),
                todayLabel = t.Today.ToString("dddd, MMMM d"),
                brief = brief is { Dismissed: false } && brief.Date == t.Today ? brief.Text : null,
                views = new
                {
                    today = t.View(SideCarTasks.TaskView.Today).Select(x => TaskRow(t, x)),
                    upcoming = t.View(SideCarTasks.TaskView.Upcoming).Select(x => TaskRow(t, x)),
                    needsMe = t.View(SideCarTasks.TaskView.NeedsMe).Select(x => TaskRow(t, x)),
                    done = t.View(SideCarTasks.TaskView.Done).Take(30).Select(x => TaskRow(t, x)),
                },
            });
        });

        // Weekly plan: Mon–Sun of the week containing ?start (default: this week), plus undated and earlier-overdue tasks.
        app.MapGet("/api/tasks/week", (string? start) =>
        {
            var t = _deps.Tasks;
            if (t is null) return Results.Problem("The to-do list isn't available.");
            var today = t.Today;
            var anchor = start is not null && DateOnly.TryParse(start, out var s) ? s : today;
            var monday = anchor.AddDays(-(((int)anchor.DayOfWeek + 6) % 7));
            var all = t.All();
            bool Show(SideCarTasks.TaskItem x, DateOnly day) =>
                x.Date == day && (x.Status == SideCarTasks.TodoStatus.Open || x.Completed is not null);
            return Results.Json(new
            {
                today = today.ToString("yyyy-MM-dd"),
                weekStart = monday.ToString("yyyy-MM-dd"),
                weekLabel = $"{monday:MMM d} – {monday.AddDays(6):MMM d}",
                days = Enumerable.Range(0, 7).Select(i => monday.AddDays(i)).Select(day => new
                {
                    date = day.ToString("yyyy-MM-dd"),
                    label = day.ToString("dddd"),
                    shortLabel = day.ToString("MMM d"),
                    isToday = day == today,
                    isPast = day < today,
                    tasks = all.Where(x => Show(x, day))
                               .OrderBy(x => x.Status).ThenBy(x => x.Priority).Select(x => TaskRow(t, x)),
                }),
                earlier = all.Where(x => x.Status == SideCarTasks.TodoStatus.Open && x.Date is { } d && d < monday)
                             .OrderBy(x => x.Date).Select(x => TaskRow(t, x)),
                noDay = all.Where(x => x.Status == SideCarTasks.TodoStatus.Open && x.Date is null)
                           .OrderBy(x => x.Priority).Select(x => TaskRow(t, x)),
            });
        });

        app.MapGet("/api/tasks/{id}", (string id) =>
        {
            var t = _deps.Tasks;
            var x = t?.Get(id);
            if (t is null || x is null) return Results.NotFound();
            return Results.Json(TaskDetail(t, x));
        });

        app.MapPost("/api/tasks", async (HttpRequest req) =>
        {
            var t = _deps.Tasks;
            if (t is null) return Results.Problem("The to-do list isn't available.");
            var body = await JsonNode.ParseAsync(req.Body) as JsonObject ?? [];
            try
            {
                var added = await OnUiAsync(() => t.Add(SideCarTasks.Actor.User, (string?)body["title"] ?? "",
                    pinToday: body["pinToday"]?.GetValue<bool>() == true));
                return Results.Json(TaskDetail(t, added));
            }
            catch (SideCarTasks.TaskRuleException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        // One endpoint for every action, so the phone and watch stay simple: {"action": "...", ...}
        app.MapPost("/api/tasks/{id}/action", async (string id, HttpRequest req) =>
        {
            var t = _deps.Tasks;
            if (t is null) return Results.Problem("The to-do list isn't available.");
            var b = await JsonNode.ParseAsync(req.Body) as JsonObject ?? [];
            string? S(string k) => b[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            bool? B(string k) => b[k] is JsonValue v && v.TryGetValue<bool>(out var x) ? x : null;
            int? I(string k) => b[k] is JsonValue v && v.TryGetValue<int>(out var x) ? x : null;
            const SideCarTasks.Actor you = SideCarTasks.Actor.User;
            try
            {
                var action = S("action") ?? "";
                if (action == "take_pass")
                {
                    if (_deps.TakePass is null) return Results.Problem("The assistant isn't available.");
                    _ = _deps.TakePass(id);
                    return Results.Json(new { queued = true });
                }
                // Changes go through the UI thread, like the kiosk's own edits, so its views stay in sync.
                var updated = await OnUiAsync(() => action switch
                {
                    "done" => t.MarkDone(you, id),
                    "reopen" => t.Reopen(you, id),
                    "pin" => t.PinToday(you, id, true),
                    "unpin" => t.PinToday(you, id, false),
                    "help_on" => t.SetAssistantHelp(you, id, true),
                    "help_off" => t.SetAssistantHelp(you, id, false),
                    "drop_date" => t.DropDate(you, id),
                    "resume" => t.ResumeRollover(you, id),
                    "accept" => t.AcceptRevision(you, id),
                    "dismiss" => t.DismissRevision(you, id),
                    "undo" => t.UndoRollover(you, id, S("entryId") ?? ""),
                    "note" => t.AddNote(you, id, S("text") ?? ""),
                    "deliverable" => t.SaveDeliverable(you, id, S("content") ?? ""),
                    "edit" => t.Edit(you, id, I("expectedVersion"), S("title"), S("details"),
                        S("priority") is { } p ? Enum.Parse<SideCarTasks.Priority>(p, true) : null,
                        S("date") is { } d ? DateOnly.Parse(d) : null, B("clearDate") == true,
                        B("hardDeadline") is { } h ? (h ? SideCarTasks.DateKind.Hard : SideCarTasks.DateKind.Flexible) : null),
                    _ => throw new SideCarTasks.TaskRuleException($"Unknown action '{action}'."),
                });
                return Results.Json(TaskDetail(t, updated));
            }
            catch (SideCarTasks.TaskRuleException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (FormatException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
    }

    private static object TaskRow(SideCarTasks.TaskService t, SideCarTasks.TaskItem x) => new
    {
        id = x.Id,
        title = x.Title,
        priority = x.Priority.ToString(),
        status = x.Status.ToString(),
        date = x.Date?.ToString("yyyy-MM-dd"),
        dateLabel = x.Date?.ToString("ddd MMM d"),
        hard = x.DateType == SideCarTasks.DateKind.Hard,
        pinned = x.PinnedToday,
        help = x.AssistantHelp,
        overdue = t.IsOverdue(x),
        needsMe = t.NeedsMe(x),
        draftVersion = x.Deliverable.Current?.Number,
        question = x.PendingQuestionId is not null,
        revision = x.Deliverable.PendingRevision is not null,
    };

    private static object TaskDetail(SideCarTasks.TaskService t, SideCarTasks.TaskItem x) => new
    {
        row = TaskRow(t, x),
        details = x.Details,
        version = x.Version,
        needsDecision = t.NeedsDecision(x),
        rolloverPaused = x.RolloverPaused,
        rolloverCount = x.RolloverCount,
        question = x.History.FirstOrDefault(e => e.Id == x.PendingQuestionId)?.Text,
        deliverable = x.Deliverable.Current is { } c
            ? new { number = c.Number, author = c.Author.ToString(), at = c.At.ToString("MMM d h:mm tt"), content = c.Content } : null,
        pendingRevision = x.Deliverable.PendingRevision?.Content,
        history = Enumerable.Reverse(x.History).Take(30).Select(e => new
        {
            id = e.Id,
            at = e.At.ToString("MMM d h:mm tt"),
            author = e.Author.ToString(),
            kind = e.Kind.ToString(),
            text = e.Text,
            canUndo = e.Rollover is { Undone: false } r && r.VersionAfter == x.Version,
        }),
    };

    private async Task ChatSessionAsync(WebSocket ws, CancellationToken ct)
    {
        var agent = _deps.Agent;
        var outbox = Channel.CreateUnbounded<string>();
        var myIds = new HashSet<string>();
        void Forward(string id, object msg) { lock (myIds) if (!myIds.Contains(id)) return; outbox.Writer.TryWrite(JsonSerializer.Serialize(msg)); }

        Action<string, string> onDelta = (id, text) => Forward(id, new { type = "delta", id, text });
        Action<string, string> onMessage = (id, text) => Forward(id, new { type = "message", id, text });
        Action<string, string> onTool = (id, name) => Forward(id, new { type = "tool", id, name });
        Action<string> onIdle = id => Forward(id, new { type = "idle", id });
        Action<string?, string> onError = (id, message) =>
        {
            if (id is null) outbox.Writer.TryWrite(JsonSerializer.Serialize(new { type = "error", message }));
            else Forward(id, new { type = "error", id, message });
        };

        if (agent is not null)
        {
            agent.Delta += onDelta; agent.Message += onMessage; agent.ToolStarted += onTool;
            agent.Idle += onIdle; agent.Error += onError;
        }

        var sender = Task.Run(async () =>
        {
            await foreach (var json in outbox.Reader.ReadAllAsync(ct))
                await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);
        }, ct);

        try
        {
            var buffer = new byte[16 * 1024];
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buffer, ct);
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                        return;
                    }
                    ms.Write(buffer, 0, r.Count);
                } while (!r.EndOfMessage);

                var msg = JsonNode.Parse(ms.ToArray());
                var type = (string?)msg?["type"];
                if (agent is null)
                {
                    outbox.Writer.TryWrite(JsonSerializer.Serialize(new { type = "error", message = "The assistant isn't configured on the kiosk." }));
                    continue;
                }
                if (type == "chat")
                {
                    var text = (string?)msg?["text"] ?? "";
                    try
                    {
                        // Register the id before sending so no early reply chunk is missed.
                        var id = "p" + Guid.NewGuid().ToString("N")[..7];
                        lock (myIds) myIds.Add(id);
                        outbox.Writer.TryWrite(JsonSerializer.Serialize(new { type = "accepted", id }));
                        await agent.SendChatAsync(text, ct, id);
                    }
                    catch (Exception ex)
                    {
                        outbox.Writer.TryWrite(JsonSerializer.Serialize(new { type = "error", message = ex.Message }));
                    }
                }
                else if (type == "end_conversation")
                {
                    await agent.EndConversationAsync();
                }
            }
        }
        catch (Exception) when (ct.IsCancellationRequested || ws.State != WebSocketState.Open) { }
        finally
        {
            if (agent is not null)
            {
                agent.Delta -= onDelta; agent.Message -= onMessage; agent.ToolStarted -= onTool;
                agent.Idle -= onIdle; agent.Error -= onError;
            }
            outbox.Writer.TryComplete();
            try { await sender; } catch (Exception) { }
        }
    }

    // ---- Auth -------------------------------------------------------------------------------

    private async Task RequireKey(HttpContext ctx, Func<Task> next)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        var presented = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..] : "";
        var ok = presented.Length > 0 && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(Key));
        if (!ok)
        {
            ctx.Response.StatusCode = 401;
            return;
        }
        await next();
    }

    private static string LoadOrCreateKey()
    {
        if (File.Exists(KeyPath) && File.ReadAllText(KeyPath).Trim() is { Length: >= 32 } existing) return existing;
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
        File.WriteAllText(KeyPath, key);
        return key;
    }

    // ---- TLS certificate ------------------------------------------------------------------

    private static X509Certificate2 LoadOrCreateCertificate()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        var existing = store.Certificates
            .Find(X509FindType.FindBySubjectDistinguishedName, CertSubject, validOnly: false)
            .OfType<X509Certificate2>()
            .Where(c => c.HasPrivateKey && c.NotAfter > DateTime.Now.AddDays(30))
            .OrderByDescending(c => c.NotAfter)
            .FirstOrDefault();
        if (existing is not null) return existing;

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest(CertSubject, key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(Environment.MachineName);
        san.AddDnsName("localhost");
        if (LocalIPv4() is { } ip) san.AddIpAddress(IPAddress.Parse(ip));
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var created = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(5));

        // Re-import so the private key is persisted with the certificate in the user's store.
        var persisted = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null,
            X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet);
        persisted.FriendlyName = "SideCar Kiosk Gateway";
        store.Add(persisted);
        return persisted;
    }

    /// <summary>The PC's home-network IPv4 address (the one with a default gateway).</summary>
    private static string? LocalIPv4() =>
        System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
            .Select(n => n.GetIPProperties())
            .Where(p => p.GatewayAddresses.Any(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
            .SelectMany(p => p.UnicastAddresses)
            .Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
            ?.ToString();

    /// <summary>This PC's Tailscale IPv4 (CGNAT range 100.64.0.0/10), or null if Tailscale isn't up.</summary>
    private static string? TailscaleIPv4() =>
        System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                                 && a.GetAddressBytes() is [100, var b, _, _] && b >= 64 && b <= 127)
            ?.ToString();

    private Task<T> OnUiAsync<T>(Func<T> fn)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_deps.Ui.TryEnqueue(() => { try { tcs.SetResult(fn()); } catch (Exception ex) { tcs.SetException(ex); } }))
            tcs.SetException(new InvalidOperationException("UI thread unavailable"));
        return tcs.Task;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
    }
}
