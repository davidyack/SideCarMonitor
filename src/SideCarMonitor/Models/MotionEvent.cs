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
    public MotionEvent(string cameraName, int cameraIndex, DetectionKind kinds, DateTime start)
    {
        CameraName = cameraName;
        CameraIndex = cameraIndex;
        _kinds = kinds;
        Start = start;
    }

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

    public string Label => PrimaryKind(Kinds) switch
    {
        DetectionKind.Person => "Person",
        DetectionKind.Vehicle => "Vehicle",
        DetectionKind.Animal => "Animal",
        _ => "Motion",
    };

    // Segoe Fluent Icons glyphs
    public string Glyph => PrimaryKind(Kinds) switch
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
