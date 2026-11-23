// SideCarAgent: a chat agent built on the GitHub Copilot SDK (agent harness), with inference routed
// to a Microsoft Foundry model-router deployment (BYOK, Responses API, Entra bearer token).
//
// It talks to its host (the SideCar kiosk) over stdin/stdout using one JSON object per line:
//   host -> agent : {"type":"token","value":"<entra access token>"}
//                   {"type":"chat","id":"c1","text":"hello"}
//                   {"type":"shutdown"}
//   agent -> host : {"type":"ready"} {"type":"delta","id","text"} {"type":"message","id","text","model"}
//                   {"type":"idle","id"} {"type":"error","id","message"}
// stdout carries only protocol messages; diagnostics go to stderr.
// The agent never signs in itself: the host owns the Entra login and sends short-lived tokens.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Copilot;
using PermissionDecision = Microsoft.Copilot.Rpc.PermissionDecision;

var baseUrl = Environment.GetEnvironmentVariable("SIDECAR_FOUNDRY_BASEURL")
              ?? "https://sidecardemo-fcwqx.openai.azure.com/openai/v1/";
var model = Environment.GetEnvironmentVariable("SIDECAR_MODEL") ?? "model-router";

var output = new ProtocolWriter(Console.OpenStandardOutput());
string? token = null;
var tokenReady = new TaskCompletionSource();
var chats = Channel.CreateUnbounded<(string Id, string Text)>();

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
});
await client.StartAsync();

#pragma warning disable MCPSDK001 // Named BYOK providers are experimental in the SDK.
await using var session = await client.CreateSessionAsync(new CreateSessionOptions
{
    Model = model,
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
            Keep answers short and easy to read at a glance: a few sentences or a short list.
            """,
    },
    // Step 1: chat only. No built-in tools (shell, files) until we decide on them together.
    Tools = new SessionToolOptions { AvailableTools = [] },
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
