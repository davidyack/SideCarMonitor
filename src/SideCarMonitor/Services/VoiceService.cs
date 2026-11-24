using System.Security;
using System.Text;
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

    private static readonly TimeSpan MaxListen = TimeSpan.FromSeconds(90);

    /// <summary>How long the mic stays open for a follow-up in a voice conversation (assistant.followUpSeconds).</summary>
    public TimeSpan FollowUpTimeout => TimeSpan.FromSeconds(Math.Clamp(_settings.FollowUpSeconds, 3, 30));

    /// <summary>
    /// Listens on the default microphone and keeps going through short pauses: it ends after
    /// assistant.endSilenceSeconds (default 2.5 s) of silence once you've spoken, when nothing is heard within
    /// <paramref name="noSpeechTimeout"/> (default 8 s), after 90 s, or when <paramref name="ct"/> is cancelled
    /// (e.g. Done tapped), returning everything heard so far. Reports the running text while you talk.
    /// Returns "" if nothing was said.
    /// </summary>
    public async Task<string> ListenOnceAsync(Action<string> onPartial, CancellationToken ct, TimeSpan? noSpeechTimeout = null)
    {
        var config = CreateConfig();
        config.Model = _settings.TranscribeModel;
        var (micId, micName) = await FindMicrophoneAsync(_settings.Microphone);
        MicrophoneName = micName;
        using var audio = micId is null ? AudioConfig.FromDefaultMicrophoneInput() : AudioConfig.FromMicrophoneInput(micId);
        using var recognizer = new SpeechRecognizer(config, audio);

        var endSilence = TimeSpan.FromSeconds(Math.Clamp(_settings.EndSilenceSeconds, 0.8, 10));
        var noSpeech = noSpeechTimeout ?? NoSpeechTimeout;
        var gate = new object();
        var parts = new List<string>();
        var partial = "";
        var heard = false;
        var lastHeard = DateTime.UtcNow;
        Exception? failure = null;
        var stopped = false;
        string Text() => string.Join(" ", partial.Length > 0 ? parts.Append(partial) : parts).Trim();

        recognizer.Recognizing += (_, e) =>
        {
            string now;
            lock (gate) { heard = true; lastHeard = DateTime.UtcNow; partial = e.Result.Text.Trim(); now = Text(); }
            onPartial(now);
        };
        recognizer.Recognized += (_, e) =>
        {
            if (e.Result.Reason != ResultReason.RecognizedSpeech || string.IsNullOrWhiteSpace(e.Result.Text)) return;
            string now;
            lock (gate) { heard = true; lastHeard = DateTime.UtcNow; parts.Add(e.Result.Text.Trim()); partial = ""; now = Text(); }
            onPartial(now);
        };
        recognizer.Canceled += (_, e) =>
        {
            if (e.Reason == CancellationReason.Error) failure = new InvalidOperationException($"Speech recognition failed: {e.ErrorDetails}");
            stopped = true;
        };
        recognizer.SessionStopped += (_, _) => stopped = true;

        var started = DateTime.UtcNow;
        await recognizer.StartContinuousRecognitionAsync();
        try
        {
            while (!stopped && !ct.IsCancellationRequested)
            {
                try { await Task.Delay(150, ct); } catch (OperationCanceledException) { break; }
                var now = DateTime.UtcNow;
                bool h; DateTime last; bool midPhrase;
                lock (gate) { h = heard; last = lastHeard; midPhrase = partial.Length > 0; }
                if (!h && now - started > noSpeech) break;
                // Pause long enough: done. A phrase still "in progress" gets a little extra for its final result.
                if (h && now - last > endSilence + (midPhrase ? TimeSpan.FromSeconds(1.5) : TimeSpan.Zero)) break;
                if (now - started > MaxListen) break;
            }
        }
        finally
        {
            await recognizer.StopContinuousRecognitionAsync(); // flushes the last phrase
        }
        if (failure is not null && parts.Count == 0) throw failure;
        lock (gate) return Text();
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

    private static readonly HashSet<string> NewscastStyles = ["narrator", "excited", "determined", "hopeful", "joyful", "happy", "neutral", "audiobook", "educational"];

    /// <summary>
    /// Long-form, expressive narration (the LinkedIn podcast): MAI-Voice-2.1 (not Flash), 24 kHz.
    /// Each paragraph may start with a delivery tag like [excited] or [narrator]; tags become speaking styles with a
    /// short pause between segments, so it sounds like a newscaster rather than a flat read.
    /// </summary>
    public sealed record NarrationSegment(double StartSeconds, string Text, List<string> Refs);

    public async Task<byte[]> SynthesizeNarrationWavAsync(string taggedScript, CancellationToken ct) =>
        (await SynthesizeNarrationAsync(taggedScript, ct)).Wav;

    /// <summary>Like SynthesizeNarrationWavAsync, plus when each paragraph starts and which items (P1, P2...) it covers.</summary>
    public async Task<(byte[] Wav, List<NarrationSegment> Segments)> SynthesizeNarrationAsync(string taggedScript, CancellationToken ct)
    {
        var segments = new List<NarrationSegment>();
        var starts = new Dictionary<int, double>();
        var config = CreateConfig();
        config.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Riff24Khz16BitMonoPcm);
        using var synth = new SpeechSynthesizer(config, null as AudioConfig);
        synth.BookmarkReached += (_, e) =>
        {
            if (e.Text.StartsWith("s") && int.TryParse(e.Text[1..], out var i)) lock (starts) starts[i] = e.AudioOffset / 10_000_000.0;
        };
        var voice = _settings.Voice.Replace(":MAI-Voice-2.1-Flash", ":MAI-Voice-2.1");
        var lang = voice.Length >= 5 ? voice[..5] : "en-US";
        var body = new StringBuilder();
        foreach (var raw in taggedScript.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var style = "narrator";
            var text = raw;
            var refs = new List<string>();
            var m = System.Text.RegularExpressions.Regex.Match(raw, @"^\[([^\]]+)\]\s*");
            if (m.Success)
            {
                text = raw[m.Length..];
                foreach (var part in m.Groups[1].Value.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries))
                {
                    if (NewscastStyles.Contains(part.ToLowerInvariant())) style = part.ToLowerInvariant();
                    else if (System.Text.RegularExpressions.Regex.IsMatch(part, @"^P\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) refs.Add(part.ToUpperInvariant());
                }
            }
            text = ForSpeech(System.Text.RegularExpressions.Regex.Replace(text, @"\[[^\]]*\]", "").Trim());
            if (text.Length == 0) continue;
            var index = segments.Count;
            segments.Add(new NarrationSegment(0, text, refs));
            body.Append($"<bookmark mark=\"s{index}\"/><mstts:express-as style=\"{style}\">{SecurityElement.Escape(text)}</mstts:express-as><break time=\"650ms\"/>");
        }
        var ssml = $"""
            <speak version="1.0" xmlns="http://www.w3.org/2001/10/synthesis" xmlns:mstts="http://www.w3.org/2001/mstts" xml:lang="{lang}">
              <voice name="{SecurityElement.Escape(voice)}">{body}</voice>
            </speak>
            """;
        using var result = await synth.SpeakSsmlAsync(ssml).WaitAsync(ct);
        if (result.Reason == ResultReason.Canceled)
            throw new InvalidOperationException(SpeechSynthesisCancellationDetails.FromResult(result).ErrorDetails);
        // Segment start times from the bookmarks; if a bookmark didn't come back, estimate by text length.
        var total = Math.Max(0, result.AudioData.Length - 44) / 48000.0;
        var chars = Math.Max(1, segments.Sum(s => s.Text.Length));
        double running = 0;
        for (var i = 0; i < segments.Count; i++)
        {
            double start;
            lock (starts) start = starts.TryGetValue(i, out var t) ? t : running;
            segments[i] = segments[i] with { StartSeconds = start };
            running = start + total * segments[i].Text.Length / chars;
        }
        return (result.AudioData, segments);
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
    /// <summary>
    /// Text as it should be heard: no markdown, and never a URL read out loud. Markdown links keep their words
    /// ("[United search](https://...)" -> "United search"); bare links are dropped ("...at https://x.com/a" -> "...");
    /// the links stay in the on-screen text.
    /// </summary>
    internal static string ForSpeech(string text)
    {
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\[([^\]]+)\]\((?:https?://|www\.)[^)\s]+\)", "$1");   // [words](url) -> words
        const string Link = "\u0001";                                                                                   // placeholder for a removed link
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<?(?:https?://|www\.)[^\s<>()]+?>?(?=[.,;:!?)]?(?:\s|$))", Link); // bare URLs
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\b[\w-]+(?:\.[\w-]+)*\.(?:com|org|net|io|ai|dev|co)/\S+?(?=[.,;:!?)]?(?:\s|$))", Link); // domain/path
        // "Link: <url>" -> "link on screen"; any other link -> "the link on screen"; two in a row read once.
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?i)\b(?:link|url|book(?:ing)? link)\s*:\s*\u0001", "link on screen");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\u0001(?:\s*(?:and|or|,)\s*\u0001)+", Link);
        text = text.Replace(Link, "the link on screen");
        var lines = text.Split('\n').Select(l => l.Trim().TrimStart('-', '*', '#', ' ').Replace("**", "").Replace("`", ""));
        return System.Text.RegularExpressions.Regex.Replace(string.Join(". ", lines.Where(l => l.Length > 0)).Replace("..", "."), @"\s{2,}", " ");
    }

    /// <summary>Markdown as plain on-screen text: links keep their words, bullets and bold marks go, lines stay.</summary>
    public static string PlainText(string text)
    {
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\[([^\]]+)\]\((?:https?://|www\.)[^)\s]+\)", "$1");
        return string.Join("\n", text.Split('\n').Select(l => l.Trim().TrimStart('-', '*', '#', ' ').Replace("**", "").Replace("`", "")).Where(l => l.Length > 0));
    }

    public void Dispose() => _synth?.Dispose();
}
