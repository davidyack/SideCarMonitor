using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Microsoft.Mxc.Sdk.V1;
using Microsoft.UI.Dispatching;
using SideCarMonitor.Models;

namespace SideCarMonitor.Services;

/// <summary>
/// Runs SideCarAgent.exe and talks to it with JSON lines over stdin/stdout.
/// By default the agent runs in an MXC ProcessContainer sandbox:
///   - files: read/execute its own program folder and the .NET runtime; read/write only its state folder
///   - network: direct egress denied; the only route out is <see cref="AllowlistProxy"/>, which tunnels
///     to the Foundry host and nothing else
///   - UI: clipboard and input injection blocked (basic window-system init is allowed; the runtime needs it)
/// The kiosk owns the Entra login and hands the agent short-lived tokens; the agent never signs in.
/// </summary>
public sealed class AgentHost : IAsyncDisposable
{
    private static readonly string[] Scopes = ["https://cognitiveservices.azure.com/.default"];

    private readonly AssistantSettings _settings;
    private readonly TokenCredential _credential;
    private readonly DispatcherQueue _ui;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private AgentProcess? _process;
    private AllowlistProxy? _proxy;
    private Timer? _tokenTimer;
    private TaskCompletionSource? _ready;

    public event Action<string, string>? Delta;       // id, text chunk
    public event Action<string, string>? Message;     // id, final text
    public event Action<string>? Idle;                // id
    public event Action<string?, string>? Error;      // id (may be null), message
    public event Action<string, string>? ToolStarted; // chat id, tool name

    /// <summary>A tool call from the agent: name, JSON arguments, and the chat turn it belongs to.</summary>
    public sealed record ToolCall(string Name, JsonNode? Args, string Turn);

    /// <summary>
    /// Answers tool calls whose data lives in the kiosk (local weather, the to-do list). Called on the UI thread;
    /// return any JSON-serializable object. The turn id tells the kiosk who's asking (you, or a background review).
    /// </summary>
    public Func<ToolCall, Task<object?>>? ToolHandler { get; set; }

    /// <summary>Raised on the UI thread when a turn finishes (any turn, including background reviews).</summary>
    public event Action<string>? TurnFinished;

    public bool IsRunning => _process is { Exited.IsCompleted: false };

    /// <summary>True when the running agent is inside the MXC sandbox.</summary>
    public bool IsSandboxed { get; private set; }

    /// <summary>The agent's only writable folder (state, memory, skills).</summary>
    public static string StateDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SideCarAgent");

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
        Directory.CreateDirectory(StateDirectory);

        _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _process = _settings.Sandbox ? await StartSandboxedAsync(exe, ct) : StartUnsandboxed(exe);
        IsSandboxed = _settings.Sandbox;

        _ = _process.Exited.ContinueWith(t =>
        {
            _ready?.TrySetException(new InvalidOperationException($"The agent exited unexpectedly (code {t.Result})."));
            Post(() => Error?.Invoke(null, "The assistant stopped. It will restart on the next request."));
        }, TaskScheduler.Default);
        var started = _process;
        _ = Task.Run(async () =>
        {
            try { await ReadOutputAsync(started.Stdout); }
            catch (Exception ex) { Debug.WriteLine($"[agent] stdout read failed: {ex.Message}"); }
            await started.OnStdoutEndedAsync();
        });
        _ = Task.Run(() => DrainErrorsAsync(_process.Stderr));

        await SendTokenAsync(ct);
        // Entra tokens last about an hour; refresh well before that.
        _tokenTimer?.Dispose();
        _tokenTimer = new Timer(_ => _ = SendTokenAsync(CancellationToken.None), null,
            TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));

        await _ready.Task.WaitAsync(TimeSpan.FromSeconds(60), ct);
    }

    // ---- Launching --------------------------------------------------------------------

    private Dictionary<string, string> AgentEnvironment() => new()
    {
        ["SIDECAR_FOUNDRY_BASEURL"] = _settings.FoundryBaseUrl!,
        ["SIDECAR_MODEL"] = _settings.Model,
        ["SIDECAR_STATE_DIR"] = StateDirectory,
        ["SIDECAR_PYTHON"] = FindPython() ?? "",
    };

    /// <summary>python.exe from PATH, so Python skills can run (the sandbox gets read-only access to its folder).</summary>
    private static string? FindPython() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d.Trim(), "python.exe"))
            .FirstOrDefault(p => File.Exists(p) && !p.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase));

    private async Task<AgentProcess> StartSandboxedAsync(string exe, CancellationToken ct)
    {
        var support = MxcPlatform.GetPlatformSupport();
        if (!support.IsSupported || !support.AvailableMethods.Contains(ContainmentBackend.ProcessContainer))
            throw new InvalidOperationException(
                $"The MXC sandbox isn't available on this PC ({support.Reason ?? "ProcessContainer unsupported"}). " +
                "The assistant won't run unsandboxed unless assistant.sandbox is set to false.");

        var foundryHost = new Uri(_settings.FoundryBaseUrl!).Host;
        _proxy?.Dispose();
        _proxy = new AllowlistProxy([foundryHost]);
        _proxy.Start();

        var env = AgentEnvironment();
        env["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        env["LOCALAPPDATA"] = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var request = new ContainerRequest($"\"{exe}\"")
        {
            WorkingDirectory = StateDirectory,
            InheritDefaultEnvironment = true,
            Environment = env,
            Filesystem = new FilesystemPolicy
            {
                ReadonlyPaths = [Path.GetDirectoryName(exe)!, DotnetRoot(), .. PythonFolder()],
                ReadwritePaths = [StateDirectory],
            },
            Network = new NetworkPolicy
            {
                Egress = new NetworkEgressPolicy { Default = NetworkAction.Deny },
                // Required by MXC for proxy mode; tested: the sandbox can reach the proxy port but not other loopback ports.
                Ingress = new NetworkIngressPolicy { Default = NetworkAction.Allow, HostLoopback = NetworkAction.Allow },
                RuntimeConfig = new NetworkRuntimeConfig { NetworkProxy = _proxy.Url },
            },
            Ui = new UiPolicy { Disable = false, Clipboard = ClipboardPolicy.None, AllowInputInjection = false },
            Containment = new Containment.ProcessContainer(),
        };

        var probe = MxcContainer.Probe(request);
        if (!string.IsNullOrEmpty(probe.Error))
            throw new InvalidOperationException($"The MXC sandbox rejected the agent's policy: {probe.Error}");

        var proc = await MxcContainer.SpawnAsync(request, new SpawnOptions(), ct);
        foreach (var w in proc.Warnings) Debug.WriteLine($"[mxc] {w}");
        // Note: MxcProcess.WaitAsync drains the output streams, so only call it after stdout has ended.
        return new AgentProcess(proc.StandardInput!, proc.StandardOutput!, proc.StandardError!,
            async () => (await proc.WaitAsync(CancellationToken.None)).ExitCode, proc.Kill, proc);
    }

    private AgentProcess StartUnsandboxed(string exe)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = StateDirectory,
        };
        foreach (var (k, v) in AgentEnvironment()) psi.Environment[k] = v;
        var p = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start SideCarAgent.");
        return new AgentProcess(p.StandardInput.BaseStream, p.StandardOutput.BaseStream, p.StandardError.BaseStream,
            async () => { await p.WaitForExitAsync(); return p.ExitCode; }, () => p.Kill(entireProcessTree: true), p);
    }

    private static string[] PythonFolder() =>
        FindPython() is { } py ? [Path.GetDirectoryName(py)!] : [];

    private static string DotnetRoot() =>
        Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root
            ? root
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");

    // ---- Protocol -----------------------------------------------------------------------

    /// <summary>
    /// Tells the agent the conversation is over (panel closed) so it can save episodic and semantic memory
    /// and clear its working memory. Does nothing if the agent isn't running.
    /// </summary>
    public Task EndConversationAsync() =>
        IsRunning ? WriteAsync(new { type = "end_conversation" }, CancellationToken.None) : Task.CompletedTask;

    /// <summary>Sends a chat message; replies arrive through the Delta/Message/Idle events with the returned id.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _turnText = new();

    /// <summary>What the user said to start this turn, or null for background turns the kiosk started itself.</summary>
    public string? TurnText(string turn) => _turnText.TryGetValue(turn, out var t) ? t : null;

    /// <param name="fromUser">False for turns the kiosk starts itself (e.g. to-do reviews).</param>
    public async Task<string> SendChatAsync(string text, CancellationToken ct = default, string? id = null, bool fromUser = true)
    {
        await EnsureStartedAsync(ct);
        id ??= Guid.NewGuid().ToString("N")[..8];
        if (fromUser && !id.StartsWith("review-", StringComparison.Ordinal))
        {
            if (_turnText.Count > 200) _turnText.Clear();
            _turnText[id] = text;
        }
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
        if (p is null || p.Exited.IsCompleted) return;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message) + "\n");
        await _writeLock.WaitAsync(ct);
        try
        {
            await p.Stdin.WriteAsync(bytes, ct);
            await p.Stdin.FlushAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    private async Task ReadOutputAsync(Stream stdout)
    {
        using var reader = new StreamReader(stdout, Encoding.UTF8);
        while (await reader.ReadLineAsync() is { } line)
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
                case "idle": Post(() => { Idle?.Invoke(id); TurnFinished?.Invoke(id); }); break;
                case "tool": Post(() => ToolStarted?.Invoke(id, (string?)msg?["name"] ?? "")); break;
                case "tool_request":
                    var call = new ToolCall((string?)msg?["name"] ?? "", msg?["args"]?.DeepClone(), (string?)msg?["turn"] ?? "");
                    _ui.TryEnqueue(async () =>
                    {
                        object? result;
                        try { result = ToolHandler is null ? new { error = "No tools." } : await ToolHandler(call) ?? new { error = $"Unknown tool '{call.Name}'." }; }
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

    private static async Task DrainErrorsAsync(Stream stderr)
    {
        using var reader = new StreamReader(stderr, Encoding.UTF8);
        while (await reader.ReadLineAsync() is { } line)
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
        if (p is not null)
        {
            try
            {
                if (!p.Exited.IsCompleted)
                {
                    var bytes = Encoding.UTF8.GetBytes("{\"type\":\"shutdown\"}\n");
                    await p.Stdin.WriteAsync(bytes);
                    await p.Stdin.FlushAsync();
                    p.Stdin.Close();
                    if (await Task.WhenAny(p.Exited, Task.Delay(3000)) != p.Exited) p.Kill();
                }
            }
            catch (Exception) { /* already gone */ }
            p.Owner.Dispose();
        }
        _proxy?.Dispose();
    }

    /// <summary>The agent process, whether sandboxed (MxcProcess) or plain (Process).</summary>
    private sealed record AgentProcess(Stream Stdin, Stream Stdout, Stream Stderr, Func<Task<int>> WaitForExit, Action Kill, IDisposable Owner)
    {
        private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes (with the exit code) once stdout has ended and the process has exited.</summary>
        public Task<int> Exited => _exited.Task;

        public async Task OnStdoutEndedAsync()
        {
            int code;
            try { code = await WaitForExit(); } catch (Exception) { code = -1; }
            _exited.TrySetResult(code);
        }
    }
}
