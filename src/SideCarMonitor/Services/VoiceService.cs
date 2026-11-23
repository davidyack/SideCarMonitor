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

    /// <summary>Transcribes a 16 kHz / 16-bit / mono PCM WAV (from the phone or watch) with MAI-Transcribe.</summary>
    public async Task<string> TranscribeWavAsync(byte[] wav, CancellationToken ct)
    {
        var config = CreateConfig();
        config.Model = _settings.TranscribeModel;
        using var push = AudioInputStream.CreatePushStream(AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1));
        using var audio = AudioConfig.FromStreamInput(push);
        using var recognizer = new SpeechRecognizer(config, audio);

        var text = new System.Text.StringBuilder();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        recognizer.Recognized += (_, e) =>
        {
            if (e.Result.Reason == ResultReason.RecognizedSpeech && e.Result.Text.Length > 0)
                lock (text) text.Append(text.Length > 0 ? " " : "").Append(e.Result.Text.Trim());
        };
        recognizer.Canceled += (_, e) =>
        {
            if (e.Reason == CancellationReason.Error) done.TrySetException(new InvalidOperationException(e.ErrorDetails));
            else done.TrySetResult();
        };
        recognizer.SessionStopped += (_, _) => done.TrySetResult();

        await recognizer.StartContinuousRecognitionAsync();
        var pcm = PcmData(wav);
        push.Write(pcm.ToArray());
        push.Close(); // end of audio: the recognizer finishes and stops the session
        await done.Task.WaitAsync(TimeSpan.FromSeconds(60), ct);
        await recognizer.StopContinuousRecognitionAsync();
        lock (text) return text.ToString();
    }

    /// <summary>Speaks text with the MAI voice into a WAV file (16 kHz mono) for the phone or watch to play.</summary>
    public async Task<byte[]> SynthesizeWavAsync(string text, CancellationToken ct)
    {
        var config = CreateConfig();
        config.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Riff16Khz16BitMonoPcm);
        using var synth = new SpeechSynthesizer(config, null as AudioConfig);
        var lang = _settings.Voice.Length >= 5 ? _settings.Voice[..5] : "en-US";
        var ssml = $"""
            <speak version="1.0" xmlns="http://www.w3.org/2001/10/synthesis" xml:lang="{lang}">
              <voice name="{SecurityElement.Escape(_settings.Voice)}">{SecurityElement.Escape(ForSpeech(text))}</voice>
            </speak>
            """;
        using var result = await synth.SpeakSsmlAsync(ssml).WaitAsync(ct);
        if (result.Reason == ResultReason.Canceled)
            throw new InvalidOperationException(SpeechSynthesisCancellationDetails.FromResult(result).ErrorDetails);
        return result.AudioData;
    }

    /// <summary>Returns the PCM samples of a WAV file (the "data" chunk), or the bytes as-is if there's no RIFF header.</summary>
    private static ReadOnlyMemory<byte> PcmData(byte[] wav)
    {
        if (wav.Length < 12 || wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F') return wav;
        var pos = 12;
        while (pos + 8 <= wav.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(wav, pos, 4);
            var size = BitConverter.ToInt32(wav, pos + 4);
            if (id == "data") return wav.AsMemory(pos + 8, Math.Min(size, wav.Length - pos - 8));
            pos += 8 + size + (size & 1);
        }
        return wav;
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
