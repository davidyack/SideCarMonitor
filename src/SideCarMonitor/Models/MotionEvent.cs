using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SideCarMonitor.Models;

[Flags]
public enum DetectionKind
{
    None = 0,
    Motion = 1,
    Animal = 2,
    Vehicle = 4,
    Person = 8,
}

/// <summary>
/// One continuous period of activity on a camera. Starts when any detection turns on,
/// ends when all detections turn off. Kinds accumulate (e.g. Motion that becomes a Person).
/// </summary>
public sealed class MotionEvent : INotifyPropertyChanged
{
    public MotionEvent(string cameraName, int cameraIndex, DetectionKind kinds, DateTime start, string? id = null)
    {
        CameraName = cameraName;
        CameraIndex = cameraIndex;
        _kinds = kinds;
        Start = start;
        Id = id ?? $"{start:yyyyMMdd-HHmmss}-{cameraIndex}";
    }

    /// <summary>Stable id, also the file name prefix of the event's pictures (see EventStore).</summary>
    public string Id { get; }

    /// <summary>
    /// A house event (door, lock, garage, alarm, automation, SideCar's own action): instant, no pictures.
    /// <paramref name="where"/> is the device or room; <paramref name="what"/> the label, e.g. "Door opened".
    /// </summary>
    public static MotionEvent House(string source, string where, string what, string glyph, DateTime at, string? detail = null, bool alert = false)
    {
        var ev = new MotionEvent(where, -1, DetectionKind.None, at, $"{at:yyyyMMdd-HHmmss}-h{Math.Abs((where + what).GetHashCode()) % 1000}")
        {
            Source = source, _customLabel = what, _customGlyph = glyph, _detail = detail ?? "", _alert = alert,
        };
        ev.End = at;
        return ev;
    }

    /// <summary>"camera", "door" (doors, locks, garage, alarms), "house" (automations, other devices) or "sidecar".</summary>
    public string Source { get; init; } = "camera";
    public bool IsCamera => Source == "camera";

    private string? _customLabel;
    private string? _customGlyph;
    private string _detail = "";
    private bool _alert;

    /// <summary>Set for house events: who did it, or why it matters.</summary>
    public string Detail { get => _detail; set { _detail = value; OnChanged(); OnChanged(nameof(VerdictText)); } }

    /// <summary>A house event worth a look (e.g. the decision model flagged a door opening at 3 AM).</summary>
    public bool Alert { get => _alert || (_verdict?.Alert ?? false); set { _alert = value; OnChanged(); OnChanged(nameof(VerdictText)); } }

    /// <summary>Restores a saved house event's label and icon.</summary>
    public void RestoreHouse(string? label, string? glyph, string? detail, bool alert)
    {
        _customLabel = label; _customGlyph = glyph; _detail = detail ?? ""; _alert = alert;
    }

    public string? CustomLabel => _customLabel;
    public string? CustomGlyph => _customGlyph;

    private readonly List<int> _snapshots = [];
    /// <summary>Pictures saved for this event: 1 = when it started, 2 = when it became a person or vehicle.</summary>
    public IReadOnlyList<int> Snapshots => _snapshots;

    private Microsoft.UI.Xaml.Media.ImageSource? _thumbnail;
    /// <summary>The latest picture, for the Activity list. Create on the UI thread.</summary>
    public Microsoft.UI.Xaml.Media.ImageSource? Thumbnail
    {
        get => _thumbnail;
        private set { _thumbnail = value; OnChanged(); OnChanged(nameof(HasThumbnail)); }
    }

    public bool HasThumbnail => _thumbnail is not null;

    /// <summary>Records that picture n was saved. Call on the UI thread.</summary>
    public void AddSnapshot(int n)
    {
        if (_snapshots.Contains(n) || Services.EventStore.SnapshotPath(Id, n) is not { } path) return;
        _snapshots.Add(n);
        _snapshots.Sort();
        if (n == _snapshots[^1])
            Thumbnail = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(path)) { DecodePixelWidth = 320 };
        OnChanged(nameof(Snapshots));
    }

    private Services.EventVerdict? _verdict;
    /// <summary>What the decision model made of the picture, or null.</summary>
    public Services.EventVerdict? Verdict
    {
        get => _verdict;
        set { _verdict = value; OnChanged(); OnChanged(nameof(VerdictText)); }
    }

    /// <summary>e.g. "Delivery · Look now", or empty before the picture is judged.</summary>
    public string VerdictText => _verdict?.Summary ?? (_alert && _detail.Length > 0 ? $"Look now \u00B7 {_detail}" : _detail);

    public string CameraName { get; }
    public int CameraIndex { get; }
    public DateTime Start { get; }

    private DetectionKind _kinds;
    public DetectionKind Kinds
    {
        get => _kinds;
        set { if (_kinds != value) { _kinds = value; OnChanged(); OnChanged(nameof(Label)); OnChanged(nameof(Glyph)); } }
    }

    private DateTime? _end;
    public DateTime? End
    {
        get => _end;
        set { _end = value; OnChanged(); OnChanged(nameof(IsActive)); UpdateDuration(); }
    }

    public bool IsActive => _end is null;

    public string TimeText => Start.ToString("h:mm:ss tt");

    private string _durationText = "active";
    public string DurationText
    {
        get => _durationText;
        private set { if (_durationText != value) { _durationText = value; OnChanged(); } }
    }

    public string Label => _customLabel ?? PrimaryKind(Kinds) switch
    {
        DetectionKind.Person => "Person",
        DetectionKind.Vehicle => "Vehicle",
        DetectionKind.Animal => "Animal",
        _ => "Motion",
    };

    // Segoe Fluent Icons glyphs
    public string Glyph => _customGlyph ?? PrimaryKind(Kinds) switch
    {
        DetectionKind.Person => "",
        DetectionKind.Vehicle => "",
        DetectionKind.Animal => "",
        _ => "",
    };

    public static DetectionKind PrimaryKind(DetectionKind k) =>
        k.HasFlag(DetectionKind.Person) ? DetectionKind.Person :
        k.HasFlag(DetectionKind.Vehicle) ? DetectionKind.Vehicle :
        k.HasFlag(DetectionKind.Animal) ? DetectionKind.Animal :
        k.HasFlag(DetectionKind.Motion) ? DetectionKind.Motion : DetectionKind.None;

    /// <summary>Called periodically so active events show a live duration.</summary>
    public void UpdateDuration()
    {
        if (!IsCamera) { DurationText = Source switch { "door" => "Doors & locks", "sidecar" => "SideCar", "system" => "System", _ => "House" }; return; } // instant
        var span = (_end ?? DateTime.Now) - Start;
        var text = span.TotalSeconds < 60 ? $"{(int)span.TotalSeconds}s"
                 : span.TotalMinutes < 60 ? $"{(int)span.TotalMinutes}m {span.Seconds}s"
                 : $"{(int)span.TotalHours}h {span.Minutes}m";
        DurationText = IsActive ? $"{text} · active" : text;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
