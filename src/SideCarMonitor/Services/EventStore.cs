using System.Text.Json;
using System.Text.RegularExpressions;
using SideCarMonitor.Models;

namespace SideCarMonitor.Services;

/// <summary>
/// Keeps camera activity across restarts, with the pictures taken when each event happened:
/// %LOCALAPPDATA%\SideCarMonitor\events\events.json plus {eventId}-{n}.jpg.
/// Pictures older than the retention period are deleted.
/// </summary>
public static partial class EventStore
{
    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SideCarMonitor", "events");

    private static string IndexPath => Path.Combine(Directory, "events.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    [GeneratedRegex(@"^\d{8}-\d{6}-\d{1,3}$")]
    private static partial Regex IdPattern();

    public static bool IsValidId(string id) => IdPattern().IsMatch(id);

    /// <summary>Path of picture n (1 = when it started, 2 = when it became a person or vehicle), or null for a bad id.</summary>
    public static string? SnapshotPath(string id, int n) =>
        IsValidId(id) && n is >= 1 and <= 9 ? Path.Combine(Directory, $"{id}-{n}.jpg") : null;

    public static async Task SaveSnapshotAsync(string id, int n, byte[] jpeg)
    {
        if (SnapshotPath(id, n) is not { } path) return;
        System.IO.Directory.CreateDirectory(Directory);
        var tmp = path + ".tmp";
        await File.WriteAllBytesAsync(tmp, jpeg);
        File.Move(tmp, path, overwrite: true);
    }

    private sealed record Saved(string Id, string Camera, int CameraIndex, DetectionKind Kinds, DateTime Start, DateTime? End, EventVerdict? Verdict = null);

    /// <summary>Loads saved events (newest first) whose start is within the retention period.</summary>
    public static List<MotionEvent> Load(TimeSpan retention)
    {
        var result = new List<MotionEvent>();
        try
        {
            if (!File.Exists(IndexPath)) return result;
            var saved = JsonSerializer.Deserialize<List<Saved>>(File.ReadAllText(IndexPath), Json) ?? [];
            var cutoff = DateTime.Now - retention;
            foreach (var s in saved.Where(s => s.Start >= cutoff && IsValidId(s.Id)))
            {
                var ev = new MotionEvent(s.Camera, s.CameraIndex, s.Kinds, s.Start, s.Id);
                // An event still active when the kiosk closed: we don't know when it ended.
                ev.End = s.End ?? s.Start;
                ev.Verdict = s.Verdict;
                for (var n = 1; n <= 9; n++)
                    if (File.Exists(SnapshotPath(s.Id, n))) ev.AddSnapshot(n);
                result.Add(ev);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[events] couldn't load: {ex.Message}");
        }
        return result;
    }

    private static readonly object SaveLock = new();

    public static void Save(IEnumerable<MotionEvent> events)
    {
        lock (SaveLock)
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var list = events.Select(e => new Saved(e.Id, e.CameraName, e.CameraIndex, e.Kinds, e.Start, e.End, e.Verdict)).ToList();
            var tmp = IndexPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(list, Json));
            File.Move(tmp, IndexPath, overwrite: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[events] couldn't save: {ex.Message}");
        }
    }

    /// <summary>Deletes pictures older than the retention period.</summary>
    public static void Prune(TimeSpan retention)
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory)) return;
            var cutoff = DateTime.Now - retention;
            foreach (var f in System.IO.Directory.EnumerateFiles(Directory, "*.jpg"))
                if (File.GetLastWriteTime(f) < cutoff) File.Delete(f);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[events] prune failed: {ex.Message}");
        }
    }
}
