using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Microsoft.UI.Dispatching;
using SideCarMonitor.Models;

namespace SideCarMonitor.Services;

/// <summary>
/// Runs SideCarAgent.exe as a child process and talks to it with JSON lines over stdin/stdout.
/// The kiosk owns the Entra login and hands the agent short-lived tokens; the agent never signs in.
/// (Step 1: plain child process. MXC sandboxing comes next.)
/// </summary>
public sealed class AgentHost : IAsyncDisposable
{
    private static readonly string[] Scopes = ["https://cognitiveservices.azure.com/.default"];

    private readonly AssistantSettings _settings;
    private readonly TokenCredential _credential;
    private readonly DispatcherQueue _ui;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Process? _process;
    private Timer? _tokenTimer;
    private TaskCompletionSource? _ready;

    public event Action<string, string>? Delta;      // id, text chunk
    public event Action<string, string>? Message;    // id, final text
    public event Action<string>? Idle;               // id
    public event Action<string?, string>? Error;     // id (may be null), message
    public event Action<string, string>? ToolStarted; // chat id, tool name

    /// <summary>
    /// Answers tool calls whose data lives in the kiosk (e.g. local weather). Called on the UI thread with the
    /// tool name; return any JSON-serializable object.
    /// </summary>
    public Func<string, object?>? ToolHandler { get; set; }

    public bool IsRunning => _process is { HasExited: false };

    public AgentHost(AssistantSettings settings, TokenCredential credential, DispatcherQueue ui)
    {
        _settings = settings;
        _credential = credential;
        _ui = ui;
    }

    /// <summary>Starts the agent if needed and waits until it reports ready.</summary>
    public async Task EnsureStartedAsync(CancellationToken ct = default)
    {
        if (IsRunning && _ready is not null) { await _ready.Task.WaitAsync(ct); return; }

        var exe = ResolveAgentPath() ?? throw new FileNotFoundException(
            "SideCarAgent.exe not found. Build src/SideCarAgent or set assistant.agentPath in settings.");

        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        psi.Environment["SIDECAR_FOUNDRY_BASEURL"] = _settings.FoundryBaseUrl;
        psi.Environment["SIDECAR_MODEL"] = _settings.Model;

        _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _process = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start SideCarAgent.");
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) =>
        {
            _ready?.TrySetException(new InvalidOperationException("The agent exited unexpectedly."));
            Post(() => Error?.Invoke(null, "The assistant stopped. It will restart on the next request."));
        };
        _ = Task.Run(() => ReadOutputAsync(_process));
        _ = Task.Run(() => DrainErrorsAsync(_process));

        await SendTokenAsync(ct);
        // Entra tokens last about an hour; refresh well before that.
        _tokenTimer = new Timer(_ => _ = SendTokenAsync(CancellationToken.None), null,
            TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));

        await _ready.Task.WaitAsync(TimeSpan.FromSeconds(60), ct);
    }

    /// <summary>Sends a chat message; replies arrive through the Delta/Message/Idle events with the returned id.</summary>
    public async Task<string> SendChatAsync(string text, CancellationToken ct = default)
    {
        await EnsureStartedAsync(ct);
        var id = Guid.NewGuid().ToString("N")[..8];
        await WriteAsync(new { type = "chat", id, text }, ct);
        return id;
    }

    private async Task SendTokenAsync(CancellationToken ct)
    {
        try
        {
            var token = await _credential.GetTokenAsync(new TokenRequestContext(Scopes), ct);
            await WriteAsync(new { type = "token", value = token.Token }, ct);
        }
        catch (Exception ex)
        {
            Post(() => Error?.Invoke(null, $"Couldn't get an Azure sign-in token: {ex.Message}"));
        }
    }

    private async Task WriteAsync(object message, CancellationToken ct)
    {
        var p = _process;
        if (p is null || p.HasExited) return;
        var line = JsonSerializer.Serialize(message);
        await _writeLock.WaitAsync(ct);
        try
        {
            await p.StandardInput.WriteLineAsync(line.AsMemory(), ct);
            await p.StandardInput.FlushAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    private async Task ReadOutputAsync(Process p)
    {
        while (await p.StandardOutput.ReadLineAsync() is { } line)
        {
            JsonNode? msg;
            try { msg = JsonNode.Parse(line); } catch (JsonException) { continue; }
            var id = (string?)msg?["id"] ?? "";
            var text = (string?)msg?["text"] ?? "";
            switch ((string?)msg?["type"])
            {
                case "ready": _ready?.TrySetResult(); break;
                case "delta": Post(() => Delta?.Invoke(id, text)); break;
                case "message": Post(() => Message?.Invoke(id, text)); break;
                case "idle": Post(() => Idle?.Invoke(id)); break;
                case "tool": Post(() => ToolStarted?.Invoke(id, (string?)msg?["name"] ?? "")); break;
                case "tool_request":
                    var name = (string?)msg?["name"] ?? "";
                    _ui.TryEnqueue(async () =>
                    {
                        object? result;
                        try { result = ToolHandler?.Invoke(name) ?? new { error = $"Unknown tool '{name}'." }; }
                        catch (Exception ex) { result = new { error = ex.Message }; }
                        await WriteAsync(new { type = "tool_result", id, result }, CancellationToken.None);
                    });
                    break;
                case "error":
                    var err = (string?)msg?["message"] ?? "Unknown error";
                    _ready?.TrySetException(new InvalidOperationException(err));
                    Post(() => Error?.Invoke(id.Length > 0 ? id : null, err));
                    break;
            }
        }
    }

    private static async Task DrainErrorsAsync(Process p)
    {
        while (await p.StandardError.ReadLineAsync() is { } line)
            Debug.WriteLine(line);
    }

    private string? ResolveAgentPath()
    {
        if (!string.IsNullOrWhiteSpace(_settings.AgentPath))
            return File.Exists(_settings.AgentPath) ? _settings.AgentPath : null;

        // Dev layout: walk up from the kiosk's bin folder to src/, then into the agent's build output.
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "SideCarAgent", "bin", "Debug", "net10.0", "SideCarAgent.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private void Post(Action action) => _ui.TryEnqueue(() => action());

    public async ValueTask DisposeAsync()
    {
        _tokenTimer?.Dispose();
        var p = _process;
        _process = null;
        if (p is null) return;
        try
        {
            if (!p.HasExited)
            {
                await p.StandardInput.WriteLineAsync("{\"type\":\"shutdown\"}");
                await p.StandardInput.FlushAsync();
                if (!p.WaitForExit(3000)) p.Kill(entireProcessTree: true);
            }
        }
        catch (Exception) { /* already gone */ }
        p.Dispose();
    }
}
