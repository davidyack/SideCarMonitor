using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using SideCarMonitor.Services;

namespace SideCarMonitor.Controls;

/// <summary>
/// Day view of all your calendars (CTC, PMCollab) merged: page back and forward a day at a time, tap a meeting to
/// see it (Join / Open in Outlook), tap empty time to add an appointment or block time. Adds made here are yours:
/// logged, not limited (the one-per-hour limit is for the agent).
/// </summary>
public sealed partial class CalendarPage : UserControl
{
    private CalendarService? _calendar;
    private CalendarTools? _tools;
    private DateTime _day = DateTime.Today;
    private List<CalEvent> _events = [];
    private int _loadVersion;

    // Add panel state
    private DateTime _addStart;
    private int _addMinutes = 30;
    private string? _addCalendar;
    private string _addTitle = "Focus time";

    public event Action? HomeRequested;

    private const int FirstHour = 0, LastHour = 24;
    private double HourHeight => Res<double>("TouchTarget") * 1.1;
    private double LabelWidth => Res<double>("TouchTarget") * 1.6;

    private static readonly Dictionary<string, string> CalendarBrush = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CTC"] = "TileCobalt", ["PMCollab"] = "TileMauve",
    };

    public CalendarPage() => InitializeComponent();

    public void Initialize(CalendarService? calendar, CalendarTools? tools)
    {
        _calendar = calendar;
        _tools = tools;
    }

    public void Show(DateTime? day = null)
    {
        _day = (day ?? DateTime.Today).Date;
        _ = LoadAsync(scrollToNow: true);
    }

    private static T Res<T>(string key) => (T)Application.Current.Resources[key];
    private static Brush BrushFor(string calendar) => Res<Brush>(CalendarBrush.GetValueOrDefault(calendar, "TileSteel"));

    private void Home_Click(object sender, RoutedEventArgs e) => HomeRequested?.Invoke();
    private void Prev_Click(object sender, RoutedEventArgs e) { _day = _day.AddDays(-1); _ = LoadAsync(false); }
    private void Next_Click(object sender, RoutedEventArgs e) { _day = _day.AddDays(1); _ = LoadAsync(false); }
    private void Today_Click(object sender, RoutedEventArgs e) { _day = DateTime.Today; _ = LoadAsync(true); }
    private void Timeline_SizeChanged(object sender, SizeChangedEventArgs e) => Draw();

    private async Task LoadAsync(bool scrollToNow, bool keepPanel = false)
    {
        var version = ++_loadVersion;
        DayTitle.Text = _day == DateTime.Today ? $"Today, {_day:dddd MMMM d}" : _day.ToString("dddd, MMMM d");
        DaySubtitle.Text = "Loading…";
        _events = [];
        Draw();
        if (!keepPanel) ShowHint();
        if (_calendar is null) { DaySubtitle.Text = "Calendars aren't set up."; return; }
        try
        {
            var events = await _calendar.DayAsync(_day);
            if (version != _loadVersion) return; // paged again meanwhile
            _events = events;
            var meetings = events.Count(e => !e.AllDay);
            DaySubtitle.Text = (meetings == 0 ? "Nothing scheduled" : $"{meetings} meeting{(meetings == 1 ? "" : "s")}") +
                               " · " + string.Join("  ", _calendar.Calendars.Select(c => $"■ {c}")) +
                               (_calendar.Problems.Count > 0 ? $" · {string.Join(", ", _calendar.Problems.Keys)} needs sign-in" : "");
        }
        catch (Exception ex)
        {
            DaySubtitle.Text = $"Couldn't load: {ex.Message}";
        }
        Draw();
        var focus = scrollToNow && _day == DateTime.Today ? DateTime.Now.Hour - 1 : (_events.FirstOrDefault(e => !e.AllDay)?.Start.Hour ?? 8) - 1;
        TimelineScroll.ChangeView(null, Math.Max(0, focus) * HourHeight, null, disableAnimation: true);
    }

    private void Draw()
    {
        Timeline.Children.Clear();
        var width = Math.Max(200, TimelineScroll.ActualWidth);
        Timeline.Width = width;
        Timeline.Height = (LastHour - FirstHour) * HourHeight;
        var caption = Res<double>("FontCaption");

        for (var h = FirstHour; h < LastHour; h++)
        {
            var y = (h - FirstHour) * HourHeight;
            var working = h is >= 8 and < 17 && _day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
            if (working)
                Add(new Rectangle { Width = width - LabelWidth, Height = HourHeight, Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(18, 255, 255, 255)) }, LabelWidth, y);
            Add(new Rectangle { Width = width - LabelWidth, Height = 1, Fill = Res<Brush>("PanelBorder") }, LabelWidth, y);
            Add(new TextBlock { Text = DateTime.Today.AddHours(h).ToString("h tt"), FontSize = caption, Foreground = Res<Brush>("MutedText") }, 8, y + 2);
        }

        // All-day items as a strip at the top of the label column's right.
        var allDay = _events.Where(e => e.AllDay).ToList();
        for (var i = 0; i < allDay.Count; i++)
            Add(Block(allDay[i], width - LabelWidth - 8, Res<double>("ActivityThumbHeight") * 0.6, compact: true), LabelWidth + 4, 2 + i * Res<double>("ActivityThumbHeight") * 0.65);

        // Timed meetings, side by side when they overlap.
        var timed = _events.Where(e => !e.AllDay).OrderBy(e => e.Start).ThenByDescending(e => e.End).ToList();
        var columns = new List<List<CalEvent>>();
        var groups = new List<List<CalEvent>>();
        DateTime groupEnd = DateTime.MinValue;
        foreach (var e in timed)
        {
            if (e.Start >= groupEnd) { groups.Add([]); groupEnd = e.End; }
            groups[^1].Add(e);
            if (e.End > groupEnd) groupEnd = e.End;
        }
        foreach (var group in groups)
        {
            var lanes = new List<DateTime>();
            var laneOf = new Dictionary<CalEvent, int>();
            foreach (var e in group)
            {
                var lane = lanes.FindIndex(end => end <= e.Start);
                if (lane < 0) { lanes.Add(e.End); lane = lanes.Count - 1; } else lanes[lane] = e.End;
                laneOf[e] = lane;
            }
            var laneWidth = (width - LabelWidth - 8) / lanes.Count;
            foreach (var e in group)
            {
                var top = Y(e.Start < _day ? _day : e.Start);
                var bottom = Y(e.End > _day.AddDays(1) ? _day.AddDays(1) : e.End);
                Add(Block(e, laneWidth - 4, Math.Max(HourHeight / 3, bottom - top - 2), compact: bottom - top < HourHeight * 0.6),
                    LabelWidth + 4 + laneOf[e] * laneWidth, top + 1);
            }
        }

        if (_day == DateTime.Today)
            Add(new Rectangle { Width = width - LabelWidth, Height = 3, Fill = Res<Brush>("AlertBrush") }, LabelWidth, Y(DateTime.Now) - 1);
    }

    private double Y(DateTime t) => (t - _day.AddHours(FirstHour)).TotalHours * HourHeight;

    private void Add(FrameworkElement e, double x, double y)
    {
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e, y);
        Timeline.Children.Add(e);
    }

    private Border Block(CalEvent e, double width, double height, bool compact)
    {
        var text = new StackPanel();
        text.Children.Add(new TextBlock
        {
            Text = compact ? $"{e.Start:h:mm} {e.Subject}" : e.Subject, FontSize = Res<double>("FontCaption"),
            FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = new SolidColorBrush(Colors.White),
        });
        if (!compact)
            text.Children.Add(new TextBlock
            {
                Text = $"{e.Start:h:mm}–{e.End:h:mm tt} · {e.Calendar}" + (e.Location.Length > 0 ? $" · {e.Location}" : ""),
                FontSize = Res<double>("FontCaption"), Opacity = 0.85, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = new SolidColorBrush(Colors.White),
            });
        var block = new Border
        {
            Width = Math.Max(40, width), Height = height, Child = text, Padding = new Thickness(8, 4, 8, 4),
            CornerRadius = Res<CornerRadius>("SmallCorner"), Background = BrushFor(e.Calendar),
            Opacity = e.ShowAs is "free" or "tentative" ? 0.7 : 1,
        };
        block.Tapped += (_, args) => { args.Handled = true; ShowMeeting(e); };
        return block;
    }

    // ---- Right panel ---------------------------------------------------------------------------

    private TextBlock Text(string s, string font = "FontBody", string? brush = null, bool bold = false) => new()
    {
        Text = s, FontSize = Res<double>(font), TextWrapping = TextWrapping.Wrap,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = brush is null ? new SolidColorBrush(Colors.White) : Res<Brush>(brush),
    };

    private Button Chip(string text, bool selected, Action onClick, string selectedBrush = "TileCobalt")
    {
        var b = new Button
        {
            Content = text, FontSize = Res<double>("FontCaption"), MinHeight = Res<double>("ActivityThumbHeight"),
            Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 6, 6), CornerRadius = Res<CornerRadius>("SmallCorner"),
            Background = Res<Brush>(selected ? selectedBrush : "PanelBorder"), Foreground = new SolidColorBrush(Colors.White),
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    private void ShowHint()
    {
        SidePanel.Children.Clear();
        SidePanel.Children.Add(Text("Tap an empty time to add an appointment or block time. Tap a meeting to see it.", "FontBody", "MutedText"));
    }

    private void ShowMeeting(CalEvent e)
    {
        SidePanel.Children.Clear();
        SidePanel.Children.Add(Text(e.Subject, "FontTitle", bold: true));
        SidePanel.Children.Add(Text(e.AllDay ? $"All day · {e.Calendar}" : $"{e.Start:dddd h:mm tt} – {e.End:h:mm tt} · {e.Calendar}", "FontBody", "MutedText"));
        if (e.Location.Length > 0) SidePanel.Children.Add(Text(e.Location));
        if (e.Organizer.Length > 0) SidePanel.Children.Add(Text($"Organizer: {e.Organizer}", "FontCaption", "MutedText"));
        if (e.ShowAs is not ("busy" or "oof")) SidePanel.Children.Add(Text($"Shows as {e.ShowAs}", "FontCaption", "MutedText"));
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        if (e.JoinUrl.Length > 0) actions.Children.Add(Chip("Join", true, () => Open(e.JoinUrl), "TileEmerald"));
        if (e.WebLink.Length > 0) actions.Children.Add(Chip("Open in Outlook", false, () => Open(e.WebLink)));
        SidePanel.Children.Add(actions);
    }

    private static void Open(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private void Timeline_Tapped(object sender, TappedRoutedEventArgs e)
    {
        var y = e.GetPosition(Timeline).Y;
        var minutes = (int)(y / HourHeight * 60) / 30 * 30; // snap to the half hour
        _addStart = _day.AddHours(FirstHour).AddMinutes(minutes);
        _addCalendar ??= _calendar?.Calendars.FirstOrDefault();
        ShowAdd();
    }

    private void ShowAdd(string? status = null)
    {
        SidePanel.Children.Clear();
        SidePanel.Children.Add(Text("Add to your calendar", "FontTitle", bold: true));

        var time = new StackPanel { Orientation = Orientation.Horizontal };
        time.Children.Add(Chip("− 30 min", false, () => { _addStart = _addStart.AddMinutes(-30); ShowAdd(); }));
        time.Children.Add(Text($"{_addStart:ddd h:mm tt} – {_addStart.AddMinutes(_addMinutes):h:mm tt}", "FontBody", bold: true));
        time.Children.Add(Chip("+ 30 min", false, () => { _addStart = _addStart.AddMinutes(30); ShowAdd(); }));
        ((TextBlock)time.Children[1]).Margin = new Thickness(8, 8, 8, 0);
        SidePanel.Children.Add(time);

        SidePanel.Children.Add(Text("Length", "FontCaption", "MutedText"));
        var lengths = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal };
        foreach (var m in new[] { 15, 30, 60, 90, 120 })
            lengths.Children.Add(Chip(m < 60 ? $"{m} min" : $"{m / 60.0:0.#} h", m == _addMinutes, () => { _addMinutes = m; ShowAdd(); }));
        SidePanel.Children.Add(lengths);

        SidePanel.Children.Add(Text("Calendar", "FontCaption", "MutedText"));
        var cals = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal };
        foreach (var c in _calendar?.Calendars ?? [])
            cals.Children.Add(Chip(c, c == _addCalendar, () => { _addCalendar = c; ShowAdd(); }, CalendarBrush.GetValueOrDefault(c, "TileSteel")));
        SidePanel.Children.Add(cals);

        SidePanel.Children.Add(Text("Title", "FontCaption", "MutedText"));
        var titles = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal };
        foreach (var t in new[] { "Focus time", "Busy", "Lunch", "Travel", "Personal" })
            titles.Children.Add(Chip(t, t == _addTitle, () => { _addTitle = t; ShowAdd(); }));
        SidePanel.Children.Add(titles);
        var box = new TextBox { PlaceholderText = "Or type a title", Text = new[] { "Focus time", "Busy", "Lunch", "Travel", "Personal" }.Contains(_addTitle) ? "" : _addTitle, FontSize = Res<double>("FontBody"), MinHeight = Res<double>("TouchTarget") };
        box.TextChanged += (_, _) => { if (box.Text.Trim().Length > 0) _addTitle = box.Text.Trim(); };
        SidePanel.Children.Add(box);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        actions.Children.Add(Chip("Add", true, () => _ = SaveAsync(_addTitle), "TileEmerald"));
        actions.Children.Add(Chip("Block time", true, () => _ = SaveAsync("Blocked"), "AlertBrush"));
        actions.Children.Add(Chip("Cancel", false, ShowHint));
        SidePanel.Children.Add(actions);
        if (status is not null) SidePanel.Children.Add(Text(status, "FontCaption", "AlertBrush"));
    }

    private async Task SaveAsync(string title)
    {
        if (_tools is null || _addCalendar is null) { ShowAdd("No calendar is signed in."); return; }
        SidePanel.Children.Add(Text("Adding…", "FontCaption", "MutedText"));
        var error = await _tools.UserAddAsync(_addCalendar, title, _addStart, _addStart.AddMinutes(_addMinutes));
        if (error is not null) { ShowAdd($"Couldn't add: {error}"); return; }
        SidePanel.Children.Clear();
        SidePanel.Children.Add(Text($"Added \"{title}\" to {_addCalendar}", "FontBody", "AccentText"));
        SidePanel.Children.Add(Text($"{_addStart:dddd h:mm tt} – {_addStart.AddMinutes(_addMinutes):h:mm tt}", "FontCaption", "MutedText"));
        await LoadAsync(false, keepPanel: true);
    }
}
