using System.Security;
using Azure.Core;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using SideCarMonitor.Models;

namespace SideCarMonitor.Services;

/// <summary>
/// Azure Speech on the Foundry resource, signed in with Entra:
/// listening with MAI-Transcribe-2-Streaming and speaking with a MAI-Voice voice.
/// Uses the Windows default microphone and speaker.
/// </summary>
public sealed class VoiceService : IDisposable
{
    private static readonly TimeSpan NoSpeechTimeout = TimeSpan.FromSeconds(8);

    private readonly AssistantSettings _settings;
    private readonly TokenCredential _credential;
    private SpeechSynthesizer? _synth;

    /// <summary>Name of the microphone used by the last ListenOnceAsync call.</summary>
    public string MicrophoneName { get; private set; } = "";

    public VoiceService(AssistantSettings settings, TokenCredential credential)
    {
        _settings = settings;
        _credential = credential;
    }

    private SpeechConfig CreateConfig() =>
        SpeechConfig.FromEndpoint(new Uri(_settings.SpeechEndpoint!), _credential);

    /// <summary>
    /// Listens on the default microphone until the first finished phrase (you pause), or until
    /// nothing is heard for a few seconds. Reports partial text while you talk. Returns "" if nothing was said.
    /// </summary>
    public async Task<string> ListenOnceAsync(Action<string> onPartial, CancellationToken ct)
    {
        var config = CreateConfig();
        config.Model = _settings.TranscribeModel;
        var (micId, micName) = await FindMicrophoneAsync(_settings.Microphone);
        MicrophoneName = micName;
        using var audio = micId is null ? AudioConfig.FromDefaultMicrophoneInput() : AudioConfig.FromMicrophoneInput(micId);
        using var recognizer = new SpeechRecognizer(config, audio);

        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var heardSomething = false;

        recognizer.Recognizing += (_, e) =>
        {
            heardSomething = true;
            onPartial(e.Result.Text);
        };
        recognizer.Recognized += (_, e) =>
        {
            if (e.Result.Reason == ResultReason.RecognizedSpeech && !string.IsNullOrWhiteSpace(e.Result.Text))
                done.TrySetResult(e.Result.Text.Trim());
        };
        recognizer.Canceled += (_, e) =>
        {
            if (e.Reason == CancellationReason.Error)
                done.TrySetException(new InvalidOperationException($"Speech recognition failed: {e.ErrorDetails}"));
            else
                done.TrySetResult("");
        };
        recognizer.SessionStopped += (_, _) => done.TrySetResult("");

        await recognizer.StartContinuousRecognitionAsync();
        try
        {
            using var reg = ct.Register(() => done.TrySetResult(""));
            var silence = Task.Delay(NoSpeechTimeout, ct).ContinueWith(_ =>
            {
                if (!heardSomething) done.TrySetResult("");
            }, TaskScheduler.Default);
            return await done.Task;
        }
        finally
        {
            await recognizer.StopContinuousRecognitionAsync();
        }
    }

    /// <summary>Speaks text on the default speaker with the configured MAI voice. Returns when done or stopped.</summary>
    public async Task SpeakAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _synth ??= new SpeechSynthesizer(CreateConfig(), AudioConfig.FromDefaultSpeakerOutput());

        var lang = _settings.Voice.Length >= 5 ? _settings.Voice[..5] : "en-US";
        var ssml = $"""
            <speak version="1.0" xmlns="http://www.w3.org/2001/10/synthesis" xml:lang="{lang}">
              <voice name="{SecurityElement.Escape(_settings.Voice)}">{SecurityElement.Escape(ForSpeech(text))}</voice>
            </speak>
            """;

        using var reg = ct.Register(() => _ = _synth?.StopSpeakingAsync());
        var result = await _synth.SpeakSsmlAsync(ssml);
        if (result.Reason == ResultReason.Canceled)
        {
            var details = SpeechSynthesisCancellationDetails.FromResult(result);
            if (details.Reason == CancellationReason.Error)
                throw new InvalidOperationException($"Speech synthesis failed: {details.ErrorDetails}");
        }
    }

    /// <summary>
    /// Finds the audio endpoint ID ("{0.0.1.00000000}.{guid}") of the first capture device whose name
    /// contains <paramref name="nameContains"/>. Returns null (use the Windows default) if not set or not found.
    /// </summary>
    private static async Task<(string? Id, string Name)> FindMicrophoneAsync(string? nameContains)
    {
        if (string.IsNullOrWhiteSpace(nameContains)) return (null, "default mic");
        var devices = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(
            Windows.Media.Devices.MediaDevice.GetAudioCaptureSelector());
        var match = devices.FirstOrDefault(d => d.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase));
        if (match is null) return (null, $"default mic ({nameContains} not found)");

        // WinRT id looks like \\?\SWD#MMDEVAPI#{0.0.1.00000000}.{guid}#{interface}; the Speech SDK wants the middle part.
        var parts = match.Id.Split('#');
        var i = Array.FindIndex(parts, p => p.Equals("MMDEVAPI", StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < parts.Length ? (parts[i + 1], match.Name) : (null, "default mic");
    }

    public Task StopSpeakingAsync() => _synth?.StopSpeakingAsync() ?? Task.CompletedTask;

    /// <summary>Strips simple Markdown so list markers and asterisks aren't read aloud.</summary>
    private static string ForSpeech(string text)
    {
        var lines = text.Split('\n').Select(l => l.Trim().TrimStart('-', '*', '#', ' ').Replace("**", "").Replace("`", ""));
        return string.Join(". ", lines.Where(l => l.Length > 0)).Replace("..", ".");
    }

    public void Dispose() => _synth?.Dispose();
}
