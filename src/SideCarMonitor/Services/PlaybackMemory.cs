using System.Text.Json;
using Windows.Media.Playback;

namespace SideCarMonitor.Services;

/// <summary>
/// Remembers where you were in a briefing (LinkedIn, X, morning/evening brief) so a kiosk restart or Stop
/// picks up there instead of the start. Saved every few seconds to %LOCALAPPDATA%\SideCarMonitor\playback.json;
/// forgotten when the audio finishes. Keys name one episode, e.g. "linkedin:{episode time}".
/// </summary>
public static class PlaybackMemory
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SideCarMonitor", "playback.json");
    private static readonly object Gate = new();
    private static Dictionary<string, Entry>? _entries;

    private sealed record Entry(double Seconds, DateTimeOffset At);

    /// <summary>Resumes <paramref name="player"/> at the saved spot (a few seconds back, for context) and keeps saving it.</summary>
    public static void Track(MediaPlayer player, string key)
    {
        var lastSave = DateTime.MinValue;
        player.MediaOpened += (p, _) =>
        {
            if (Get(key) is not { } at) return;
            var length = p.PlaybackSession.NaturalDuration;
            if (at > TimeSpan.FromSeconds(5) && (length == TimeSpan.Zero || at < length - TimeSpan.FromSeconds(5)))
                p.PlaybackSession.Position = at - TimeSpan.FromSeconds(3);
        };
        player.PlaybackSession.PositionChanged += (s, _) =>
        {
            if (DateTime.UtcNow - lastSave < TimeSpan.FromSeconds(3) || s.Position < TimeSpan.FromSeconds(5)) return;
            lastSave = DateTime.UtcNow;
            Save(key, s.Position);
        };
        player.MediaEnded += (_, _) => Forget(key);
    }

    public static TimeSpan? Get(string key)
    {
        lock (Gate) return Load().TryGetValue(key, out var e) ? TimeSpan.FromSeconds(e.Seconds) : null;
    }

    public static void Save(string key, TimeSpan position)
    {
        lock (Gate) { Load()[key] = new Entry(position.TotalSeconds, DateTimeOffset.Now); Write(); }
    }

    public static void Forget(string key)
    {
        lock (Gate) { if (Load().Remove(key)) Write(); }
    }

    private static Dictionary<string, Entry> Load()
    {
        if (_entries is not null) return _entries;
        try { _entries = File.Exists(FilePath) ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath)) : null; }
        catch { _entries = null; }
        return _entries ??= [];
    }

    private static void Write()
    {
        try
        {
            // Keep it small: only the last couple of weeks.
            foreach (var old in _entries!.Where(e => e.Value.At < DateTimeOffset.Now.AddDays(-14)).Select(e => e.Key).ToList()) _entries.Remove(old);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_entries));
        }
        catch { /* best effort */ }
    }
}
