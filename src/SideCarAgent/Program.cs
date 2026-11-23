// SideCarAgent: a chat agent built on the GitHub Copilot SDK (agent harness), with inference routed
// to a Microsoft Foundry model-router deployment (BYOK, Responses API, Entra bearer token).
//
// It talks to its host (the SideCar kiosk) over stdin/stdout using one JSON object per line:
//   host -> agent : {"type":"token","value":"<entra access token>"}
//                   {"type":"chat","id":"c1","text":"hello"}
//                   {"type":"tool_result","id":"t1","result":{...}}
//                   {"type":"shutdown"}
//   agent -> host : {"type":"ready"} {"type":"delta","id","text"} {"type":"message","id","text","model"}
//                   {"type":"idle","id"} {"type":"error","id","message"}
//                   {"type":"tool","id","name"}               (a tool started; for status display)
//                   {"type":"tool_request","id","name","args"} (the host answers with tool_result)
// Tools whose data lives on the host (like local weather) are answered by the host over this pipe,
// so the agent needs no network or device access of its own.
// stdout carries only protocol messages; diagnostics go to stderr.
// The agent never signs in itself: the host owns the Entra login and sends short-lived tokens.

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Copilot;
using Microsoft.Extensions.AI;
using PermissionDecision = Microsoft.Copilot.Rpc.PermissionDecision;

var baseUrl = Environment.GetEnvironmentVariable("SIDECAR_FOUNDRY_BASEURL")
              ?? "https://sidecardemo-fcwqx.openai.azure.com/openai/v1/";
var model = Environment.GetEnvironmentVariable("SIDECAR_MODEL") ?? "model-router";

var output = new ProtocolWriter(Console.OpenStandardOutput());
string? token = null;
var tokenReady = new TaskCompletionSource();
var chats = Channel.CreateUnbounded<(string Id, string Text)>();
var pendingTools = new ConcurrentDictionary<string, TaskCompletionSource<string>>();

// ---- Read host messages -------------------------------------------------------------
var reader = Task.Run(async () =>
{
    using var stdin = new StreamReader(Console.OpenStandardInput());
    while (await stdin.ReadLineAsync() is { } line)
    {
        if (string.IsNullOrWhiteSpace(line)) continue;
        try
        {
            var msg = JsonNode.Parse(line);
            switch ((string?)msg?["type"])
            {
                case "token":
                    token = (string?)msg!["value"];
                    tokenReady.TrySetResult();
                    break;
                case "chat":
                    await chats.Writer.WriteAsync(((string?)msg!["id"] ?? Guid.NewGuid().ToString("N"), (string?)msg["text"] ?? ""));
                    break;
                case "tool_result":
                    if (pendingTools.TryRemove((string?)msg!["id"] ?? "", out var waiter))
                        waiter.TrySetResult(msg["result"]?.ToJsonString() ?? "null");
                    break;
                case "shutdown":
                    chats.Writer.TryComplete();
                    return;
            }
        }
        catch (JsonException ex)
        {
            Log($"Ignoring malformed host message: {ex.Message}");
        }
    }
    // stdin closed (host gone, or a piped test finished): finish queued chats, then exit.
    chats.Writer.TryComplete();
});

// ---- Copilot SDK session --------------------------------------------------------------
Log($"Waiting for token; model={model} baseUrl={baseUrl}");
if (await Task.WhenAny(tokenReady.Task, Task.Delay(TimeSpan.FromSeconds(30))) != tokenReady.Task)
{
    output.Write(new { type = "error", message = "No token received from host within 30 seconds." });
    return 1;
}

// Session state (transcripts, compaction) lives here. In the MXC sandbox this will be the writable work folder.
var stateDir = Environment.GetEnvironmentVariable("SIDECAR_STATE_DIR")
               ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SideCarAgent");
Directory.CreateDirectory(stateDir);

await using var client = new CopilotClient(new CopilotClientOptions
{
    Mode = CopilotClientMode.Empty,
    Runtime = RuntimeConnection.ForInProcess(stateDir),
    // Never pick up the user's GitHub sign-in (gh CLI hosts.yml etc.). Inference is BYOK to Foundry only.
    GitHub = new ClientGitHubOptions { UseLoggedInUser = false },
});
await client.StartAsync();

// ---- Tools answered by the host ----------------------------------------------------------
async Task<string> AskHostAsync(string name, object? args = null)
{
    var id = "t" + Guid.NewGuid().ToString("N")[..8];
    var waiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    pendingTools[id] = waiter;
    output.Write(new { type = "tool_request", id, name, args });
    if (await Task.WhenAny(waiter.Task, Task.Delay(TimeSpan.FromSeconds(10))) == waiter.Task)
        return await waiter.Task;
    pendingTools.TryRemove(id, out _);
    return JsonSerializer.Serialize(new { error = "The kiosk didn't answer in time." });
}

var weatherTool = CopilotTool.DefineTool(
    [Description("Current conditions from the user's own Tempest weather station at home: temperature, feels-like, wind, humidity, rain today, pressure trend, lightning.")]
    async () => await AskHostAsync("get_local_weather"),
    new CopilotToolOptions { SkipPermission = true, Idempotent = true },
    new AIFunctionFactoryOptions { Name = "get_local_weather" });

#pragma warning disable MCPSDK001 // Named BYOK providers are experimental in the SDK.
await using var session = await client.CreateSessionAsync(new CreateSessionOptions
{
    Model = model,
    // Stay inside our own folder: no git repo discovery, no config/instruction files from elsewhere.
    WorkingDirectory = stateDir,
    EnableConfigDiscovery = false,
    Git = new SessionGitOptions { EnableHostGitOperations = false },
    Instructions = new CreateSessionInstructionOptions { SkipCustomInstructions = true },
    // Don't use GitHub Copilot inference; everything goes to our Foundry deployment.
    GitHub = new SessionGitHubOptions { Capi = new CapiSessionOptions { Enabled = false } },
    Inference = new SessionInferenceOptions
    {
        DefaultProvider = "foundry",
        Providers =
        [
            new NamedProviderConfig
            {
                Name = "foundry",
                Type = "openai",
                BaseUrl = baseUrl,
                WireApi = "responses",
                // The host refreshes the token over stdin; always hand the runtime the latest one.
                BearerTokenProvider = _ => Task.FromResult(token ?? throw new InvalidOperationException("No token")),
            },
        ],
    },
    SystemMessage = new SystemMessageConfig
    {
        Mode = SystemMessageMode.Replace,
        Content = """
            You are SideCar, a helpful assistant shown on a small 7-inch kiosk touchscreen beside the user's desk.
            Your replies are also read aloud, so keep them short and natural to hear: a few sentences, no tables.
            For questions about the weather at home (outside, temperature, wind, rain), call get_local_weather
            and answer from it. It's the user's own weather station, so prefer it over general knowledge.
            """,
    },
    // Only our own host-answered tools. No built-in tools (shell, files) until we decide on them together.
    Tools = new SessionToolOptions { AvailableTools = ["get_local_weather"], Definitions = [weatherTool] },
    Permissions = new SessionPermissionOptions
    {
        OnRequest = (_, _) => Task.FromResult(PermissionDecision.Reject("Tools are not enabled for this agent yet.")),
    },
    Events = new SessionEventOptions { Streaming = true },
});
#pragma warning restore MCPSDK001

string? currentId = null;
TaskCompletionSource? turnDone = null;

session.On<SessionEvent>(evt =>
{
    switch (evt)
    {
        case AssistantMessageDeltaEvent delta:
            output.Write(new { type = "delta", id = currentId, text = delta.Data.DeltaContent });
            break;
        case AssistantMessageEvent message when !string.IsNullOrEmpty(message.Data.Content):
            output.Write(new { type = "message", id = currentId, text = message.Data.Content, model = message.Data.Model });
            break;
        case ToolExecutionStartEvent tool:
            output.Write(new { type = "tool", id = currentId, name = tool.Data.ToolName });
            break;
        case SessionErrorEvent error:
            output.Write(new { type = "error", id = currentId, message = error.Data.Message });
            break;
        case SessionIdleEvent:
            turnDone?.TrySetResult();
            break;
    }
});

output.Write(new { type = "ready", model });

// ---- Chat loop: one turn at a time -------------------------------------------------------
await foreach (var (id, text) in chats.Reader.ReadAllAsync())
{
    currentId = id;
    turnDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    try
    {
        await session.SendAsync(new MessageOptions { Prompt = text });
        if (await Task.WhenAny(turnDone.Task, Task.Delay(TimeSpan.FromMinutes(3))) != turnDone.Task)
        {
            await session.AbortAsync();
            output.Write(new { type = "error", id, message = "Timed out after 3 minutes." });
        }
    }
    catch (Exception ex)
    {
        output.Write(new { type = "error", id, message = ex.Message });
    }
    output.Write(new { type = "idle", id });
}

await reader;
return 0;

static void Log(string message) => Console.Error.WriteLine($"[agent] {message}");

/// <summary>Writes one JSON object per line to stdout; safe to call from SDK event threads.</summary>
sealed class ProtocolWriter(Stream stream)
{
    private readonly StreamWriter _writer = new(stream) { AutoFlush = true, NewLine = "\n" };
    private readonly Lock _gate = new();

    public void Write(object message)
    {
        var json = JsonSerializer.Serialize(message);
        lock (_gate) _writer.WriteLine(json);
    }
}
