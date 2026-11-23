using System.Runtime.InteropServices.WindowsRuntime;
using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SideCarMonitor.Controls;
using SideCarMonitor.Models;
using SideCarMonitor.Services;

namespace SideCarMonitor;

public sealed partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly List<CameraTile> _tiles = [];
    private readonly ObservableCollection<MotionEvent> _events = [];
    private readonly DispatcherQueueTimer _clock;
    private readonly WeatherFormat _wx;
    private MotionMonitor? _monitor;
    private TempestListener? _tempest;
    private AgentHost? _agent;
    private Gateway? _gateway;
    private SideCarTasks.TaskService? _taskService;
    private TaskAssistant? _taskTools;
    private List<CameraConfig> _cameraConfigs = [];
    private VoiceService? _voice;
    private EventJudge? _judge;
    private HomeAssistantBridge? _home;

    private void HouseTile_Click(object sender, RoutedEventArgs e)
    {
        CalendarView.Visibility = Visibility.Collapsed;
        LinkedInView.Visibility = Visibility.Collapsed;
        ActivityView.Visibility = Visibility.Collapsed;
        WeatherPage.Visibility = Visibility.Collapsed;
        HomePage.Visibility = Visibility.Collapsed;
        TasksPageView.Visibility = Visibility.Collapsed;
        HouseView.Visibility = Visibility.Visible;
        HouseView.Show();
    }

    private DateTime _lastHouseRefresh;

    /// <summary>Keeps the House tile's "lights on" count fresh while the home screen shows.</summary>
    private void RefreshHouseTile()
    {
        if (_home is null || DateTime.Now - _lastHouseRefresh < TimeSpan.FromSeconds(60)) return;
        _lastHouseRefresh = DateTime.Now;
        _ = HouseView.RefreshAsync();
    }

    private void UpdateHouseTile(int lightsOn, int lights)
    {
        HouseTileValue.Text = lightsOn.ToString();
        HouseTileDetail.Text = _home?.PausedReason is not null ? "SideCar control paused"
                             : $"light{(lightsOn == 1 ? "" : "s")} on of {lights}";
        HouseTile.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
            _home?.PausedReason is not null ? "TileCrimson" : "TileTeal"];
    }

    private void ShowHomePaused(string? reason)
    {
        if (_lastHouseRefresh != default) _lastHouseRefresh = default; // tile picks up the paused state on next tick
        HomePausedBanner.Visibility = reason is null ? Visibility.Collapsed : Visibility.Visible;
        HomePausedText.Text = reason is null ? "" : $"Home control paused: {reason}";
    }

    private void ResumeHome_Click(object sender, RoutedEventArgs e) => _home?.Resume();
    private CameraTile? _focused;

    public MainWindow(AppSettings settings, bool createdNewSettings)
    {
        _settings = settings;
        _wx = new WeatherFormat(settings.Weather.Imperial);
        InitializeComponent();
        StartTempest();
        StartTasks();
        StartAssistant();

        PlaceOnKioskMonitor();

        SystemEvents.Reported += OnSystemEvent;
        TrackTouches();
        LoadSavedEvents();
        ActivityView.Initialize(_events);
        ActivityView.HomeRequested += ShowHome;
        ActivityView.WatchLiveRequested += i => { ActivityView.Visibility = Visibility.Collapsed; ShowCameras(); if (i < _tiles.Count) { _focused = _tiles[i]; LayoutTiles(); } };
        EventList.ItemsSource = _shownEvents;
        _events.CollectionChanged += (_, _) => { RebuildShownEvents(); UpdateActivitySummary(); };
        RebuildShownEvents();
        StartHouseEvents();
        UpdateActivitySummary();

        _clock = DispatcherQueue.CreateTimer();
        _clock.Interval = TimeSpan.FromSeconds(1);
        _clock.Tick += (_, _) => Tick();
        ShowHome();
        _clock.Start();
        Tick();

        BuildCameras(createdNewSettings);
        Closed += OnClosed;
    }

    // ---- Window placement -------------------------------------------------

    private void PlaceOnKioskMonitor()
    {
        var bounds = PickDisplay().OuterBounds;
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(bounds.X, bounds.Y, bounds.Width, bounds.Height));
        SetFullScreen(_settings.Display.FullScreen);
    }

    private DisplayArea PickDisplay()
    {
        // DisplayArea.FindAll() must be indexed, not enumerated (projection quirk).
        var all = DisplayArea.FindAll();
        var displays = new List<DisplayArea>();
        for (var i = 0; i < all.Count; i++) displays.Add(all[i]);

        if (_settings.Display.MonitorIndex is int idx && idx >= 0 && idx < displays.Count)
            return displays[idx];

        var primaryId = DisplayArea.Primary.DisplayId.Value;
        return displays.FirstOrDefault(d => d.DisplayId.Value != primaryId &&
                                            d.OuterBounds.Width == 1920 && d.OuterBounds.Height == 1080)
            ?? DisplayArea.Primary;
    }

    private void SetFullScreen(bool on)
    {
        AppWindow.SetPresenter(on ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
        FullScreenButtonText.Text = on ? "Exit full screen" : "Full screen";
    }

    private bool IsFullScreen => AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;

    // ---- Cameras ------------------------------------------------------------

    private void BuildCameras(bool createdNewSettings)
    {
        var cameras = _cameraConfigs = _settings.Cameras.Where(c => c.IsConfigured).ToList();
        if (cameras.Count == 0)
        {
            EmptyState.Visibility = Visibility.Visible;
            EmptyStateDetail.Text = (createdNewSettings ? "A settings file was created for you. " : "") +
                                    $"Add your Reolink cameras to:\n{AppSettings.SettingsPath}\nthen restart the app.";
            StatusText.Text = "Waiting for configuration";
            return;
        }

        foreach (var camera in cameras)
        {
            var tile = new CameraTile(camera);
            tile.Tapped += (s, _) => ToggleFocus((CameraTile)s);
            tile.AspectChanged += (_, _) => LayoutTiles();
            CameraGrid.Children.Add(tile);
            _tiles.Add(tile);
        }
        LayoutTiles();

        _monitor = new MotionMonitor(cameras, TimeSpan.FromSeconds(_settings.MotionPollSeconds), DispatcherQueue);
        _monitor.StateChanged += (i, kinds) => _tiles[i].SetDetection(kinds);
        _monitor.ApiStatusChanged += (i, err) =>
        {
            _tiles[i].SetApiError(err);
            if (err is not null) SystemEvents.Report($"Camera offline: {cameras[i].Name}", err, dedupe: TimeSpan.FromHours(2));
            else if (_cameraWasDown.Remove(i)) SystemEvents.Report($"Camera back online: {cameras[i].Name}");
            if (err is not null) _cameraWasDown.Add(i);
        };
        _monitor.EventStarted += OnEventStarted;
        _monitor.Start();
    }

    private void CameraGrid_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutTiles();

    /// <summary>
    /// Arranges the visible tiles in whichever rows x columns makes each 16:9 video largest
    /// for the space available. In focus mode only the focused tile is visible.
    /// </summary>
    private void LayoutTiles()
    {
        if (_tiles.Count == 0) return;
        List<CameraTile> visible = _focused is null ? _tiles : [_focused];
        if (CameraGrid.ActualWidth <= 0 || CameraGrid.ActualHeight <= 0) return;

        // Each tile is sized to its camera's real aspect ratio. LibVLC's swap chain top-aligns letterboxed
        // video, so a tile with the wrong shape leaves dead space; an exact-shape tile has none.
        var gap = (double)Application.Current.Resources["Gap"];
        var rects = JustifiedLayout.Compute(visible.Select(t => t.VideoAspect).ToList(),
                                            CameraGrid.ActualWidth, CameraGrid.ActualHeight, gap);

        foreach (var t in _tiles)
            t.Visibility = visible.Contains(t) ? Visibility.Visible : Visibility.Collapsed;
        for (var i = 0; i < visible.Count; i++)
        {
            var r = rects[i];
            var tile = visible[i];
            tile.HorizontalAlignment = HorizontalAlignment.Left;
            tile.VerticalAlignment = VerticalAlignment.Top;
            tile.Margin = new Thickness(r.X, r.Y, 0, 0);
            tile.Width = Math.Max(1, r.Width);
            tile.Height = Math.Max(1, r.Height);
        }

        BackButton.Visibility = _focused is null ? Visibility.Collapsed : Visibility.Visible;
    }


    /// <summary>Tap a tile to fill the camera area with it; tap it again or press "All cameras" to go back.</summary>
    private void ToggleFocus(CameraTile tile)
    {
        _focused = _focused == tile ? null : tile;
        LayoutTiles();
    }

    private void Unfocus()
    {
        if (_focused is null) return;
        _focused = null;
        LayoutTiles();
    }

    // ---- Events ---------------------------------------------------------------

    private static readonly TimeSpan EventRetention = TimeSpan.FromDays(7);

    /// <summary>Restores the last week of activity (with its pictures) after a restart.</summary>
    private void LoadSavedEvents()
    {
        EventStore.Prune(EventRetention);
        foreach (var ev in EventStore.Load(EventRetention).Take(Math.Max(10, _settings.MaxEvents)))
        {
            WatchEvent(ev);
            _events.Add(ev);
        }
        _ = JudgeUnlabeledAsync();
    }

    private string? CameraWatches(MotionEvent ev) =>
        ev.CameraIndex < _cameraConfigs.Count ? _cameraConfigs[ev.CameraIndex].Watches : null;

    /// <summary>Labels saved event pictures that haven't been judged yet (e.g. from before the decision model was set up).</summary>
    private async Task JudgeUnlabeledAsync()
    {
        if (_judge is null) return;
        foreach (var ev in _events.Where(e => (e.Verdict?.Version ?? 0) < EventVerdict.CurrentVersion && e.Snapshots.Count > 0).Take(20).ToList())
        {
            try
            {
                var jpeg = await File.ReadAllBytesAsync(EventStore.SnapshotPath(ev.Id, ev.Snapshots[^1])!);
                ev.Verdict = await _judge.JudgeAsync(jpeg, ev.CameraName, CameraWatches(ev), CancellationToken.None);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[events] judge failed for {ev.Id}: {ex.Message}");
                await File.AppendAllTextAsync(Path.Combine(EventStore.Directory, "judge.log"), $"{DateTime.Now:s} {ev.Id}: {ex.Message}\n");
            }
        }
        SaveEvents();
    }

    private void WatchEvent(MotionEvent ev) => ev.PropertyChanged += (_, e) =>
    {
        if (e.PropertyName == nameof(MotionEvent.IsActive)) { UpdateActivitySummary(); SaveEvents(); }
        // Take a second picture when motion turns out to be a person or vehicle.
        if (e.PropertyName == nameof(MotionEvent.Label) && ev.Label is "Person" or "Vehicle" && ev.IsActive)
            CaptureEventPicture(ev, 2);
    };

    private void OnEventStarted(MotionEvent ev)
    {
        WatchEvent(ev);
        _events.Insert(0, ev);
        while (_events.Count > Math.Max(10, _settings.MaxEvents))
            _events.RemoveAt(_events.Count - 1);
        CaptureEventPicture(ev, 1);
        SaveEvents();
    }

    private readonly HashSet<string> _capturing = [];

    /// <summary>Grabs a still from the event's camera and saves it with the event (once per picture number).</summary>
    private async void CaptureEventPicture(MotionEvent ev, int n)
    {
        if (!_capturing.Add($"{ev.Id}-{n}") || ev.Snapshots.Contains(n)) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            if (await SnapshotAsync(ev.CameraIndex, cts.Token) is not { Length: > 0 } jpeg) return;
            await EventStore.SaveSnapshotAsync(ev.Id, n, jpeg);
            ev.AddSnapshot(n); // back on the UI thread after the awaits
            SaveEvents();
            ShowEventPopup(ev); // shows the picture now; the analysis fills in below when it arrives
            if (_judge is not null)
            {
                // Ask the decision model what's in the picture (the later picture wins).
                using var judgeCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                ev.Verdict = await _judge.JudgeAsync(jpeg, ev.CameraName, CameraWatches(ev), judgeCts.Token) ?? ev.Verdict;
                SaveEvents();
                if (_popupEvent == ev) FillEventPopup(ev);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[events] picture failed for {ev.Id}: {ex.Message}");
            try { File.AppendAllText(Path.Combine(EventStore.Directory, "judge.log"), $"{DateTime.Now:s} {ev.Id}: {ex.Message}\n"); } catch { }
        }
    }

    private DateTime _lastPrune = DateTime.Now;

    private void SaveEvents()
    {
        var snapshot = _events.ToList();
        var prune = DateTime.Now - _lastPrune > TimeSpan.FromHours(6);
        if (prune) _lastPrune = DateTime.Now;
        _ = Task.Run(() =>
        {
            EventStore.Save(snapshot);
            if (prune) EventStore.Prune(EventRetention);
        });
    }

    // ---- Combined feed: house events from Home Assistant and SideCar's own actions ----

    private readonly ObservableCollection<MotionEvent> _shownEvents = [];
    private readonly HashSet<int> _cameraWasDown = [];

    // ---- Camera event popup --------------------------------------------------------------------
    // When a camera event's picture arrives and the kiosk is idle (nobody touched it for a minute and the
    // assistant isn't in use), show the picture and the AI's analysis for 30 s, with Watch live.

    private static readonly TimeSpan IdleBeforePopup = TimeSpan.FromSeconds(60);
    private const int PopupSeconds = 30;
    private DateTime _lastTouch = DateTime.MinValue;
    private MotionEvent? _popupEvent;
    private DispatcherQueueTimer? _popupTimer;
    private int _popupLeft;

    private void TrackTouches()
    {
        if (Content is UIElement root)
            root.AddHandler(UIElement.PointerPressedEvent,
                new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => _lastTouch = DateTime.Now), handledEventsToo: true);
    }

    private bool KioskIsBusy =>
        DateTime.Now - _lastTouch < IdleBeforePopup || Assistant.IsBusy || PairingPage.Visibility == Visibility.Visible;

    private void ShowEventPopup(MotionEvent ev)
    {
        if (!ev.IsCamera || ev.Snapshots.Count == 0) return;
        // Already showing one? Switch to the newer event. Otherwise only when nobody is using the kiosk.
        if (EventPopup.Visibility != Visibility.Visible && KioskIsBusy) return;
        _popupEvent = ev;
        _popupJoinUrl = null;
        EventPopupImage.Visibility = Visibility.Visible;
        EventPopupAction.Visibility = Visibility.Visible;
        EventPopupActionText.Text = "Watch live";
        EventPopupActionIcon.Glyph = "\uE714";
        FillEventPopup(ev);
        EventPopup.Visibility = Visibility.Visible;
        StartPopupCountdown(PopupSeconds);
    }

    private void StartPopupCountdown(int seconds)
    {
        _popupLeft = seconds;
        EventPopupCountdown.Maximum = seconds;
        EventPopupCountdown.Value = _popupLeft;
        if (_popupTimer is null)
        {
            _popupTimer = DispatcherQueue.CreateTimer();
            _popupTimer.Interval = TimeSpan.FromSeconds(1);
            _popupTimer.Tick += (_, _) =>
            {
                EventPopupCountdown.Value = --_popupLeft;
                if (_popupLeft <= 0) CloseEventPopup();
            };
        }
        _popupTimer.Start();
    }

    private void FillEventPopup(MotionEvent ev)
    {
        EventPopupTitle.Text = $"{ev.Label} \u00B7 {ev.CameraName}";
        EventPopupWhen.Text = ev.Start.ToString("dddd h:mm:ss tt");
        EventPopupAi.Text = ev.Verdict is { } v ? $"AI: {v.Summary}" : "Analyzing\u2026";
        if (ev.Snapshots.Count > 0 && EventStore.SnapshotPath(ev.Id, ev.Snapshots[^1]) is { } path)
            EventPopupImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(path)) { DecodePixelWidth = 1400 };
    }

    private void CloseEventPopup()
    {
        _popupTimer?.Stop();
        EventPopup.Visibility = Visibility.Collapsed;
        EventPopupImage.Source = null;
        _popupEvent = null;
    }

    private void EventPopupDismiss_Click(object sender, RoutedEventArgs e) => CloseEventPopup();
    private void EventPopupScrim_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) => CloseEventPopup();
    private void EventPopupCard_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) => e.Handled = true; // taps on the card don't dismiss

    private void EventPopupLive_Click(object sender, RoutedEventArgs e)
    {
        if (_popupJoinUrl is { } join)
        {
            CloseEventPopup();
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(join) { UseShellExecute = true }); } catch { }
            return;
        }
        var index = _popupEvent?.CameraIndex ?? -1;
        CloseEventPopup();
        if (index < 0) return;
        ShowCameras();
        if (index < _tiles.Count) { _focused = _tiles[index]; LayoutTiles(); }
    }

    private void OnSystemEvent(string what, string detail, bool alert) => DispatcherQueue.TryEnqueue(() =>
        AddFeedEvent(MotionEvent.House("system", "SideCar", what, alert ? "\uE7BA" : "\uE9D9", DateTime.Now, detail, alert)));

    private void ShowActivity()
    {
        CalendarView.Visibility = Visibility.Collapsed;
        LinkedInView.Visibility = Visibility.Collapsed;
        HouseView.Visibility = Visibility.Collapsed;
        TasksPageView.Visibility = Visibility.Collapsed;
        WeatherPage.Visibility = Visibility.Collapsed;
        HomePage.Visibility = Visibility.Collapsed;
        ActivityView.Visibility = Visibility.Visible;
        ActivityView.Show();
    }

    private void ActivityTile_Click(object sender, RoutedEventArgs e) => ShowActivity();

    // ---- Calendars: next meeting on the clock tile, amber 15 min before, popup 5 min before ----
    private CalendarService? _calendar;
    private CalendarTools? _calendarTools;
    private readonly HashSet<string> _meetingPopups = [];
    private string? _popupJoinUrl;

    private void ClockTile_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        HideOverlayPages();
        HomePage.Visibility = Visibility.Collapsed;
        CalendarView.Visibility = Visibility.Visible;
        CalendarView.Show();
    }

    private CalEvent? NextMeeting() => _calendar?.Events
        .Where(e => !e.AllDay && e.ShowAs != "free" && e.End > DateTime.Now && e.Start.Date == DateTime.Today)
        .OrderBy(e => e.Start).FirstOrDefault();

    private void UpdateMeetingTile()
    {
        var next = NextMeeting();
        if (next is null)
        {
            NextMeetingWhen.Text = _calendar?.Problems.Count > 0 ? string.Join(", ", _calendar.Problems.Keys) + " needs sign-in" : "";
            NextMeetingText.Text = _calendar?.Calendars.Count > 0 ? "No more meetings today" : "";
            ClockTile.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["PanelBackground"];
            return;
        }
        var until = next.Start - DateTime.Now;
        NextMeetingWhen.Text = until <= TimeSpan.Zero ? $"Now \u00B7 until {next.End:h:mm} \u00B7 {next.Calendar}"
            : until <= TimeSpan.FromMinutes(60) ? $"In {Math.Ceiling(until.TotalMinutes)} min \u00B7 {next.Calendar}"
            : $"{next.Start:h:mm tt} \u00B7 {next.Calendar}";
        NextMeetingText.Text = next.Subject;
        var soon = until <= TimeSpan.FromMinutes(15) && until > TimeSpan.FromMinutes(-5);
        ClockTile.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[soon ? "TileAmber" : "PanelBackground"];
    }

    private DateTime _lastMeetingCheck;
    private void CheckMeetings()
    {
        if (_calendar is null || DateTime.Now - _lastMeetingCheck < TimeSpan.FromSeconds(15)) return;
        _lastMeetingCheck = DateTime.Now;
        if (_calendar.IsStale) _ = _calendar.RefreshAsync();
        UpdateMeetingTile();
        foreach (var e in _calendar.Events.Where(e => !e.AllDay && e.ShowAs != "free"))
        {
            var until = e.Start - DateTime.Now;
            if (until > TimeSpan.Zero && until <= TimeSpan.FromMinutes(5) && _meetingPopups.Add($"{e.Calendar}|{e.Subject}|{e.Start:o}"))
            {
                ShowMeetingPopup(e);
                break;
            }
        }
    }

    /// <summary>The 5-minute reminder: same popup as camera events, with Join for online meetings.</summary>
    private void ShowMeetingPopup(CalEvent e)
    {
        if (PairingPage.Visibility == Visibility.Visible) return;
        _popupEvent = null;
        _popupJoinUrl = e.JoinUrl.Length > 0 ? e.JoinUrl : null;
        EventPopupTitle.Text = e.Subject;
        EventPopupWhen.Text = $"{e.Start:h:mm tt}\u2013{e.End:h:mm tt} \u00B7 in {Math.Max(1, Math.Ceiling((e.Start - DateTime.Now).TotalMinutes))} min \u00B7 {e.Calendar}";
        EventPopupAi.Text = string.Join(" \u00B7 ", new[] { e.Location, e.Organizer.Length > 0 ? $"Organizer: {e.Organizer}" : "", e.JoinUrl.Length > 0 ? "Online meeting" : "" }.Where(s => s.Length > 0));
        EventPopupImage.Source = null;
        EventPopupImage.Visibility = Visibility.Collapsed;
        EventPopupAction.Visibility = _popupJoinUrl is null ? Visibility.Collapsed : Visibility.Visible;
        EventPopupActionText.Text = "Join";
        EventPopupActionIcon.Glyph = "\uE8AA";
        EventPopup.Visibility = Visibility.Visible;
        StartPopupCountdown(300); // stays up until it starts (or you dismiss it)
    }

    // ---- Morning (8 AM) and evening (8 PM) briefs ----
    private BriefService? _briefs;
    private Brief? _shownBrief;
    private DateTime _lastBriefAttempt;

    private void CheckBriefs()
    {
        if (_briefs?.Due(DateTime.Now) is not { } kind || DateTime.Now - _lastBriefAttempt < TimeSpan.FromMinutes(15)) return;
        _lastBriefAttempt = DateTime.Now;
        _ = Task.Run(() => _briefs.MakeAsync(kind));
    }

    private void ShowBrief(Brief? b)
    {
        _shownBrief = b;
        BriefTitle.Text = b?.Title ?? "No brief yet";
        BriefText.Text = b?.Text ?? "Briefs are made at 8 AM and 8 PM. Tap Make it now for one now.";
        BriefRemake.Content = "Make it now";
        BriefRemake.IsEnabled = true;
        BriefPopup.Visibility = Visibility.Visible;
    }

    private void BriefDismiss_Click(object sender, RoutedEventArgs e)
    {
        if (_shownBrief is { } b) _briefs?.Dismiss(b);
        BriefPopup.Visibility = Visibility.Collapsed;
    }

    private async void BriefRemake_Click(object sender, RoutedEventArgs e)
    {
        if (_briefs is null) return;
        BriefRemake.IsEnabled = false;
        BriefRemake.Content = "Making it\u2026";
        var kind = DateTime.Now.Hour is >= 4 and < 15 ? "morning" : "evening";
        var b = await Task.Run(() => _briefs.MakeAsync(kind));
        if (b is null) { BriefRemake.Content = "Couldn't make it"; BriefRemake.IsEnabled = true; }
    }

    /// <summary>Called from the brief's background thread: gathers the facts on the UI thread, where they live.</summary>
    private System.Text.Json.Nodes.JsonObject GatherBriefFactsOnUi(string kind)
    {
        var done = new TaskCompletionSource<System.Text.Json.Nodes.JsonObject>();
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                try { done.SetResult(GatherBriefFacts(kind)); } catch (Exception ex) { done.SetException(ex); }
            }))
            throw new InvalidOperationException("The kiosk is closing");
        return done.Task.WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
    }

    /// <summary>The facts a brief is written from (UI thread).</summary>
    private System.Text.Json.Nodes.JsonObject GatherBriefFacts(string kind)
    {
        var J = (object? o) => System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(o));
        var now = DateTime.Now;
        var morning = kind == "morning";
        var since = morning ? now.Date.AddDays(-1).AddHours(20) : now.Date.AddHours(6);
        var calDay = morning ? now.Date : now.Date.AddDays(1);
        object? weather = null;
        try { weather = _tempest is null ? null : LocalWeatherForAgent(); } catch { }

        var events = (_calendar?.Events ?? []).Where(e => e.Start.Date == calDay || (e.AllDay && e.Start.Date <= calDay && e.End.Date > calDay))
            .Select(e => new { e.Calendar, e.Subject, start = e.AllDay ? "all day" : e.Start.ToString("h:mm tt"), end = e.End.ToString("h:mm tt"), online = e.JoinUrl.Length > 0, e.Location });

        object? tasks = null;
        try
        {
            if (_taskService is { } ts)
            {
                var today = DateOnly.FromDateTime(now);
                tasks = new
                {
                    today = ts.View(SideCarTasks.TaskView.Today).Select(t => new { t.Title, date = t.Date?.ToString("ddd MMM d") }),
                    needsMe = ts.View(SideCarTasks.TaskView.NeedsMe).Select(t => t.Title),
                    upcoming = ts.View(SideCarTasks.TaskView.Upcoming).Where(t => t.Date is { } d && d <= today.AddDays(3)).Select(t => new { t.Title, date = t.Date?.ToString("ddd MMM d") }),
                    doneToday = morning ? null : ts.View(SideCarTasks.TaskView.Done).Take(15).Select(t => t.Title),
                };
            }
        }
        catch { }

        var feed = _events.ToList().Where(e => e.Start >= since).ToList();
        var activity = new
        {
            cameraEvents = feed.Count(e => e.IsCamera),
            worthALook = feed.Where(e => e.Alert).Take(10).Select(e => $"{e.Start:h:mm tt} {e.Label} at {e.CameraName}: {e.VerdictText}"),
            doorsAndLocks = feed.Where(e => e.Source == "door").Take(10).Select(e => $"{e.Start:h:mm tt} {e.Label}: {e.CameraName} {e.Detail}"),
            sidecarSafeguards = feed.Where(e => e.Source is "system" or "sidecar" && (e.Alert || e.Label.StartsWith("Stopped") || e.Label.Contains("blocked"))).Take(8).Select(e => $"{e.Start:h:mm tt} {e.Label} {e.Detail}"),
        };

        var li = _linkedIn?.State;
        return new System.Text.Json.Nodes.JsonObject
        {
            ["now"] = now.ToString("dddd MMMM d, h:mm tt"),
            ["kind"] = kind,
            ["weather_now"] = J(weather),
            [morning ? "meetings_today" : "meetings_tomorrow"] = J(events),
            ["todos"] = J(tasks),
            [morning ? "activity_overnight" : "activity_today"] = J(activity),
            ["house"] = J(new { lightsOn = HouseTileValue.Text, homeControlPaused = _home?.PausedReason }),
            ["linkedin_digest"] = morning && li?.DigestAt?.Date == now.Date ? li.Digest : null,
        };
    }

    // ---- LinkedIn tile ----
    private LinkedInFeed? _linkedIn;

    private void LinkedInTile_Click(object sender, RoutedEventArgs e)
    {
        HideOverlayPages();
        HomePage.Visibility = Visibility.Collapsed;
        LinkedInView.Visibility = Visibility.Visible;
        LinkedInView.Show();
    }

    private void UpdateLinkedInTile()
    {
        if (_linkedIn is null) return;
        var s = _linkedIn.State;
        var today = _linkedIn.Signal.Where(p => p.FirstSeen.Date == DateTime.Today).ToList();
        LinkedInTileValue.Text = s.LastRun is null ? "--" : today.Count.ToString();
        LinkedInTileDetail.Text = s.NeedsAttention ? "Needs sign-in" : s.LastRun is null ? "Not read yet"
            : today.Count > 0 ? string.Join(", ", today.Select(p => p.Author).Distinct().Take(3)) : "Nothing worth it today";
    }

    /// <summary>Daily read (about 7 AM), checked once a minute from the clock.</summary>
    private DateTime _lastLinkedInCheck;
    private void CheckLinkedInSchedule()
    {
        if (_linkedIn is null || DateTime.Now - _lastLinkedInCheck < TimeSpan.FromMinutes(1)) return;
        _lastLinkedInCheck = DateTime.Now;
        if (!_linkedIn.IsRunning && _linkedIn.IsDue(DateTime.Now)) _ = Task.Run(() => _linkedIn.RunAsync(manual: false));
    }

    /// <summary>Hides the full-screen pages that sit over Home and Cameras.</summary>
    private void HideOverlayPages()
    {
        ActivityView.Visibility = Visibility.Collapsed;
        HouseView.Visibility = Visibility.Collapsed;
        TasksPageView.Visibility = Visibility.Collapsed;
        WeatherPage.Visibility = Visibility.Collapsed;
        LinkedInView.Visibility = Visibility.Collapsed;
        CalendarView.Visibility = Visibility.Collapsed;
    }
    private string _activityFilter = "all";
    private HomeEventStream? _houseEvents;

    private void ActivityFilter_Click(object sender, RoutedEventArgs e)
    {
        _activityFilter = (string)((Button)sender).Tag;
        RebuildShownEvents();
    }

    private bool ShowInFilter(MotionEvent ev) => _activityFilter switch
    {
        "camera" => ev.IsCamera,
        "door" => ev.Source == "door",
        "house" => ev.Source is "house" or "sidecar",
        "system" => ev.Source is "system" or "sidecar",
        _ => true,
    };

    private void RebuildShownEvents()
    {
        _shownEvents.Clear();
        foreach (var ev in _events) if (ShowInFilter(ev)) _shownEvents.Add(ev);
        foreach (var b in ActivityFilters.Children.OfType<Button>())
            b.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                (string)b.Tag == _activityFilter ? "TileCobalt" : "PanelBackground"];
        NoEventsText.Visibility = _shownEvents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void StartHouseEvents()
    {
        _houseEvents = HomeEventStream.Create(_settings.Assistant.HomeAssistantUrl, _settings.Cameras.Select(c => c.Name).ToList());
        if (_houseEvents is not null)
        {
            _houseEvents.Happened += e => DispatcherQueue.TryEnqueue(() => AddHouseEvent(e));
            _houseEvents.Start();
        }
        if (_home is not null)
            _home.AgentActed += (where, what, blocked) => DispatcherQueue.TryEnqueue(() =>
                AddFeedEvent(MotionEvent.House("sidecar", where, blocked ? $"SideCar blocked: {what.ToLowerInvariant()}" : $"SideCar: {what.ToLowerInvariant()}",
                    "\uE99A", DateTime.Now, blocked ? "Paused home control" : null, alert: blocked)));
    }

    private void AddFeedEvent(MotionEvent ev)
    {
        _events.Insert(0, ev);
        while (_events.Count > Math.Max(10, _settings.MaxEvents)) _events.RemoveAt(_events.Count - 1);
        SaveEvents();
    }

    private async void AddHouseEvent(HouseEvent e)
    {
        var who = e.Who switch { "device" => "", "automation" => "by an automation", _ => $"by {e.Who}" };
        var ev = MotionEvent.House(e.Source, e.Where, e.What, e.Glyph, e.At, who, alert: e.Risky && _judge is null);
        AddFeedEvent(ev);
        if (e.Source != "door" || _judge is null) return;
        try
        {
            // The decision model decides whether a door, lock, garage or alarm event deserves a "Look now".
            var recent = new System.Text.Json.Nodes.JsonArray(_events.Where(x => x.Source == "door" && x != ev).Take(8)
                .Select(x => (System.Text.Json.Nodes.JsonNode)System.Text.Json.Nodes.JsonValue.Create($"{x.Start:ddd h:mm tt} {x.Label}: {x.CameraName}")!).ToArray());
            var odd = await _judge.OddHouseEventAsync(new System.Text.Json.Nodes.JsonObject
            {
                ["now"] = DateTime.Now.ToString("dddd h:mm tt"),
                ["event"] = $"{e.What}: {e.Where}",
                ["who"] = e.Who,
                ["recent_events"] = recent,
            });
            if (odd >= 0.6) { ev.Alert = true; SaveEvents(); }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[events] house judge failed: {ex.Message}");
            if (e.Risky) { ev.Alert = true; SaveEvents(); }
        }
    }

    private void EventList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is MotionEvent ev && ev.CameraIndex >= 0 && ev.CameraIndex < _tiles.Count)
        {
            _focused = _tiles[ev.CameraIndex];
            LayoutTiles();
        }
    }

    private void UpdateActivitySummary()
    {
        NoEventsText.Visibility = _shownEvents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var active = _events.Count(e => e.IsActive);
        ActiveCountText.Text = active > 0 ? $"{active} active" : "";
    }

    private void Tick()
    {
        var now = DateTime.Now;
        HomeClockText.Text = now.ToString("h:mm");
        HomeDateText.Text = now.ToString("dddd, MMMM d");
        CamerasClockText.Text = now.ToString("h:mm");
        WeatherClockText.Text = now.ToString("h:mm");
        foreach (var ev in _events)
            if (ev.IsActive) ev.UpdateDuration();

        var live = _tiles.Count(t => t.IsPlaying);
        if (_tiles.Count > 0)
            StatusText.Text = $"{live} of {_tiles.Count} cameras live";
        UpdateLiveTiles(live);
        CheckBriefs();
        CheckMeetings();
        CheckLinkedInSchedule();
        if (HomePage.Visibility == Visibility.Visible) RefreshHouseTile();
        UpdateWeatherStatus();
    }

    // ---- Navigation -----------------------------------------------------------------
    // The Cameras page is always underneath; Weather and Home are opaque pages layered on top.

    // ---- To-do --------------------------------------------------------------------------------

    private void StartTasks()
    {
        try
        {
            var store = new SideCarTasks.TaskStore(App.TasksPath);
            _taskService = new SideCarTasks.TaskService(store);
            TasksPageView.Initialize(_taskService, null);
            TasksPageView.HomeRequested += ShowHome;
            store.Changed += _ => DispatcherQueue.TryEnqueue(UpdateTasksTile);
            UpdateTasksTile();
            // Durable daily rollover + brief, even when the kiosk isn't running.
            var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (exe is not null)
                _ = Task.Run(() => System.Diagnostics.Debug.WriteLine("[tasks] scheduled task: " + TaskAssistant.RegisterScheduledTask(exe)));
        }
        catch (Exception ex)
        {
            TasksTileDetail.Text = "To-do unavailable";
            System.Diagnostics.Debug.WriteLine($"[tasks] failed to start: {ex.Message}");
        }
    }

    private void UpdateTasksTile()
    {
        if (_taskService is null) return;
        var today = _taskService.View(SideCarTasks.TaskView.Today);
        var needs = _taskService.View(SideCarTasks.TaskView.NeedsMe);
        TasksTileValue.Text = today.Count.ToString();
        TasksTileDetail.Text = needs.Count > 0 ? $"{needs.Count} need{(needs.Count == 1 ? "s" : "")} you"
                             : today.FirstOrDefault() is { } first ? first.Title : "Nothing for today";
        TasksTile.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[needs.Count > 0 ? "TileCrimson" : "TileEmerald"];
    }

    private void TasksTile_Click(object sender, RoutedEventArgs e) => ShowTasks();

    private void ShowTasks()
    {
        CalendarView.Visibility = Visibility.Collapsed;
        LinkedInView.Visibility = Visibility.Collapsed;
        ActivityView.Visibility = Visibility.Collapsed;
        HouseView.Visibility = Visibility.Collapsed;
        WeatherPage.Visibility = Visibility.Collapsed;
        HomePage.Visibility = Visibility.Collapsed;
        TasksPageView.Visibility = Visibility.Visible;
        TasksPageView.Show();
    }

    private void ShowHome()
    {
        CalendarView.Visibility = Visibility.Collapsed;
        LinkedInView.Visibility = Visibility.Collapsed;
        ActivityView.Visibility = Visibility.Collapsed;
        HouseView.Visibility = Visibility.Collapsed;
        TasksPageView.Visibility = Visibility.Collapsed;
        WeatherPage.Visibility = Visibility.Collapsed;
        HomePage.Visibility = Visibility.Visible;
    }

    private void ShowCameras()
    {
        CalendarView.Visibility = Visibility.Collapsed;
        LinkedInView.Visibility = Visibility.Collapsed;
        ActivityView.Visibility = Visibility.Collapsed;
        HouseView.Visibility = Visibility.Collapsed;
        TasksPageView.Visibility = Visibility.Collapsed;
        WeatherPage.Visibility = Visibility.Collapsed;
        HomePage.Visibility = Visibility.Collapsed;
        LayoutTiles();
    }

    private void ShowWeather()
    {
        CalendarView.Visibility = Visibility.Collapsed;
        LinkedInView.Visibility = Visibility.Collapsed;
        ActivityView.Visibility = Visibility.Collapsed;
        HouseView.Visibility = Visibility.Collapsed;
        TasksPageView.Visibility = Visibility.Collapsed;
        WeatherPage.Visibility = Visibility.Visible;
        HomePage.Visibility = Visibility.Collapsed;
    }

    private void WeatherTile_Click(object sender, RoutedEventArgs e) => ShowWeather();

    // ---- Voice assistant ----------------------------------------------------------------

    private void StartAssistant()
    {
        var a = _settings.Assistant;
        if (!a.IsConfigured)
        {
            Assistant.Initialize(null, null, "Set assistant.foundryBaseUrl and assistant.speechEndpoint in settings to turn on the assistant.");
            return;
        }
        // The kiosk owns the Entra sign-in: the Azure CLI login first, then an interactive browser sign-in as a fallback.
        var credential = new Azure.Identity.ChainedTokenCredential(
            new Azure.Identity.AzureCliCredential(),
            new Azure.Identity.InteractiveBrowserCredential());
        _agent = new AgentHost(a, credential, DispatcherQueue);
        _voice = new VoiceService(a, credential);
        _judge = EventJudge.Create(a, credential);
        _linkedIn = new LinkedInFeed(_judge, _agent);
        LinkedInView.Initialize(_linkedIn);
        LinkedInView.HomeRequested += ShowHome;
        _linkedIn.Changed += () => DispatcherQueue.TryEnqueue(UpdateLinkedInTile);
        UpdateLinkedInTile();
        _calendar = new CalendarService();
        _calendarTools = new CalendarTools(_calendar, _judge);
        _calendar.Changed += () => DispatcherQueue.TryEnqueue(UpdateMeetingTile);
        CalendarView.Initialize(_calendar, _calendarTools);
        CalendarView.HomeRequested += ShowHome;
        CalendarView.BriefRequested += () => ShowBrief(_briefs?.Latest);
        _briefs = new BriefService(_agent, GatherBriefFactsOnUi);
        _briefs.Ready += b => DispatcherQueue.TryEnqueue(() => ShowBrief(b));
        if (_briefs.Latest is { Dismissed: false } pending && pending.At > DateTime.Now.AddHours(-14)) ShowBrief(pending);
        _ = _calendar.RefreshAsync();
        _home = HomeAssistantBridge.Create(a.HomeAssistantUrl, _judge);
        HouseView.Initialize(_home);
        HouseView.HomeRequested += ShowHome;
        HouseView.CountsChanged += UpdateHouseTile;
        if (_home is null) HouseTileDetail.Text = "Not set up";
        if (_home is not null)
            _home.PausedChanged += reason => DispatcherQueue.TryEnqueue(() => ShowHomePaused(reason));
        _agent.ToolHandler = call => call.Name switch
        {
            "get_local_weather" => Task.FromResult<object?>(LocalWeatherForAgent()),
            _ when call.Name.StartsWith("calendar_") => _calendarTools is null
                ? Task.FromResult<object?>(new { error = "Calendars aren't set up." })
                : _calendarTools.HandleAsync(call, _agent?.TurnText(call.Turn)),
            _ when call.Name.StartsWith("home_") => _home is null
                ? Task.FromResult<object?>(new { error = "Home Assistant isn't set up (no token file)." })
                : _home.HandleAsync(call, _agent?.TurnText(call.Turn)),
            _ when call.Name.StartsWith("task") => _taskTools?.HandleAsync(call) ?? Task.FromResult<object?>(new { error = "The to-do list isn't available." }),
            _ => Task.FromResult<object?>(new { error = $"Unknown tool '{call.Name}'." }),
        };
        Assistant.Initialize(_agent, _voice, null);
        // "it" in chat/voice means the task you're looking at.
        Assistant.ContextProvider = () =>
            TasksPageView.Visibility == Visibility.Visible && TasksPageView.SelectedTask is { } t
                ? $"[Context: the user is looking at task {t.Id} \"{t.Title}\"]" : null;
        if (_taskService is not null)
        {
            _taskTools = new TaskAssistant(_taskService, _agent, DispatcherQueue);
            TasksPageView.Initialize(_taskService, _taskTools);
            _ = _taskTools.RunDueJobsAsync();
        }
        _ = StartGatewayAsync();
    }

    /// <summary>Snapshot of the Tempest readings for the agent's get_local_weather tool.</summary>
    private object LocalWeatherForAgent()
    {
        if (_tempest?.State is not { LastObservation: DateTime last } s)
            return new { error = "No reading from the Tempest weather station yet." };
        return new
        {
            station = "Tempest weather station at the user's home",
            observedAt = last.ToString("h:mm tt"),
            minutesAgo = (int)(DateTime.Now - last).TotalMinutes,
            condition = WeatherFormat.Condition(s),
            temperature = _wx.Temp(s.TemperatureC),
            feelsLike = _wx.Temp(s.FeelsLikeC),
            humidity = s.HumidityPct is double rh ? $"{rh:0}%" : null,
            dewPoint = _wx.Temp(s.DewPointC),
            wind = $"{_wx.Speed(s.RapidWindMs ?? s.WindAvgMs)} from {WeatherFormat.Direction(s.RapidWindDirDeg ?? s.WindDirDeg)}",
            windGust = _wx.Speed(s.WindGustMs),
            rainToday = _wx.Rain(s.RainTodayMm) + " (counted since the kiosk started)",
            precipitationNow = WeatherFormat.PrecipText(s.PrecipType) is { Length: > 0 } p ? p : "none",
            pressure = $"{_wx.Pressure(s.StationPressureMb)} station pressure, {_wx.PressureTrend(s.PressureTrendMb)}",
            uvIndex = s.Uv,
            lightningStrikesToday = s.StrikesToday,
            lastLightning = s.LastStrike is DateTime ls ? $"{ls:h:mm tt}, {_wx.Distance(s.LastStrikeKm)} away" : null,
        };
    }

    // ---- Weather (Tempest) ------------------------------------------------------------

    private void StartTempest()
    {
        if (!_settings.Weather.TempestEnabled) return;
        try
        {
            _tempest = new TempestListener(DispatcherQueue);
            _tempest.Updated += UpdateWeather;
            _tempest.Start();
            if (_tempest.State.LastObservation is not null) UpdateWeather(); // cached reading from last run
        }
        catch (Exception ex)
        {
            _tempest = null;
            WeatherTileDetail.Text = "Tempest unavailable";
            WeatherStatusText.Text = $"Can't listen on UDP {TempestListener.Port}: {ex.Message}";
        }
    }

    private void UpdateWeather()
    {
        if (_tempest is null) return;
        var s = _tempest.State;
        var condition = WeatherFormat.Condition(s);

        // Home tile
        WeatherTileValue.Text = _wx.Temp(s.TemperatureC);
        WeatherTileDetail.Text = string.Join(" · ", new[]
        {
            condition,
            s.WindAvgMs is not null ? $"{_wx.Speed(s.RapidWindMs ?? s.WindAvgMs)} {WeatherFormat.Direction(s.RapidWindDirDeg ?? s.WindDirDeg)}" : "",
        }.Where(x => x.Length > 0));

        // Weather page
        WxTemp.Text = _wx.Temp(s.TemperatureC);
        WxCondition.Text = condition;
        WxFeels.Text = $"Feels like {_wx.Temp(s.FeelsLikeC)}" + (s.Uv is double uv ? $"  ·  UV {uv:0.#}" : "");

        // rapid_wind (every 3 s) is the live number; obs_st gives the 1-minute average, gust and lull.
        var windMs = s.RapidWindMs ?? s.WindAvgMs;
        var windDir = s.RapidWindDirDeg ?? s.WindDirDeg;
        WxWind.Text = _wx.SpeedNumber(windMs);
        WxWindUnit.Text = _wx.SpeedUnit;
        WxWindDir.Text = windMs > 0 ? $"from {WeatherFormat.Direction(windDir)}" : "calm";
        WxWindArrow.Visibility = windMs > 0 && windDir is not null ? Visibility.Visible : Visibility.Collapsed;
        WxWindRotate.Angle = (windDir ?? 0) + 180; // arrow points the way the wind is blowing
        WxGust.Text = $"Gust {_wx.Speed(s.WindGustMs)}  ·  Avg {_wx.Speed(s.WindAvgMs)}";

        WxHumidity.Text = s.HumidityPct is double rh ? $"{rh:0}%" : "--";
        WxDew.Text = $"Dew point {_wx.Temp(s.DewPointC)}";

        WxRain.Text = _wx.Rain(s.RainTodayMm);
        var precip = WeatherFormat.PrecipText(s.PrecipType);
        WxRainDetail.Text = precip.Length > 0 ? precip
                          : s.LastRainStart is DateTime rs ? $"Started {rs:h:mm tt}"
                          : "Counted since app start";

        WxPressure.Text = _wx.Pressure(s.StationPressureMb);
        WxPressureTrend.Text = $"Station · {_wx.PressureTrend(s.PressureTrendMb)}";

        WxStrikes.Text = s.StrikesToday.ToString();
        WxStrikeDetail.Text = s.LastStrike is DateTime ls
            ? $"Last {ls:h:mm tt}, {_wx.Distance(s.LastStrikeKm)} away"
            : "None detected";

        UpdateWeatherStatus();
    }

    private void UpdateWeatherStatus()
    {
        if (_tempest?.State.LastObservation is not DateTime last) return;
        var age = DateTime.Now - last;
        var stale = age > TimeSpan.FromMinutes(5);
        WeatherStatusText.Text = stale
            ? $"No data from Tempest since {last:h:mm tt}"
            : $"Tempest {_tempest.State.StationSerial} · updated {(age.TotalSeconds < 60 ? $"{age.TotalSeconds:0}s" : $"{age.TotalMinutes:0}m")} ago" +
              (_tempest.State.BatteryVolts is double v ? $" · battery {v:0.00} V" : "");
        if (stale) WeatherTileDetail.Text = "No data from Tempest";
    }

    /// <summary>Keeps the Cameras and Activity tiles showing live information, like Windows Phone live tiles.</summary>
    private void UpdateLiveTiles(int live)
    {
        CamerasTileValue.Text = _tiles.Count == 0 ? "Set up" : $"{live}/{_tiles.Count}";
        CamerasTileDetail.Text = _tiles.Count == 0 ? "" : "live";

        var today = _events.Count(e => e.Start.Date == DateTime.Today);
        var active = _events.FirstOrDefault(e => e.IsActive);
        ActivityTileValue.Text = today.ToString();
        ActivityTileDetail.Text = active is not null ? $"{active.Label} at {active.CameraName}"
                                : _events.Count > 0 ? $"Last: {_events[0].Label}, {_events[0].TimeText}"
                                : "Nothing today";
        ActivityTile.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
            active is not null ? "AlertBrush" : "TileCrimson"];
    }

    private void CamerasTile_Click(object sender, RoutedEventArgs e) => ShowCameras();

    private void HomeButton_Click(object sender, RoutedEventArgs e) => ShowHome();

    private void ComingSoonTile_Click(object sender, RoutedEventArgs e)
    {
        var tile = (FrameworkElement)sender;
        var flyout = new Flyout
        {
            Content = new TextBlock
            {
                Text = $"{tile.Tag} isn't connected yet.",
                FontSize = (double)Application.Current.Resources["FontBody"],
            },
        };
        flyout.ShowAt(tile);
    }

    // ---- Touch buttons ----------------------------------------------------------

    private void BackButton_Click(object sender, RoutedEventArgs e) => Unfocus();

    private void FullScreenButton_Click(object sender, RoutedEventArgs e)
    {
        MenuFlyout.Hide();
        SetFullScreen(!IsFullScreen);
    }

    private void QuitButton_Click(object sender, RoutedEventArgs e) => Close();

    // ---- Phone/watch gateway and pairing ------------------------------------------------------

    private async Task StartGatewayAsync()
    {
        if (!_settings.Gateway.Enabled) return;
        try
        {
            _gateway = new Gateway(_settings.Gateway.Port, new Gateway.Dependencies(
                _agent, _voice, DispatcherQueue, StatusForPhone, SnapshotAsync, _taskService,
                _taskTools is null ? null : id => _taskTools.ReviewAsync(id, explicitRequest: true)));
            _gateway.Home = _home;
            _gateway.LinkedIn = _linkedIn;
            await _gateway.StartAsync();
        }
        catch (Exception ex)
        {
            _gateway = null;
            System.Diagnostics.Debug.WriteLine($"[gateway] failed to start: {ex.Message}");
        }
    }

    /// <summary>What the phone's home screen shows. Runs on the UI thread.</summary>
    private object StatusForPhone() => new
    {
        time = DateTime.Now,
        weather = _tempest is null ? null : LocalWeatherForAgent(),
        weatherSummary = $"{WeatherTileValue.Text} {WeatherTileDetail.Text}".Trim(),
        cameras = _cameraConfigs.Select((c, i) => new { index = i, name = c.Name, live = i < _tiles.Count && _tiles[i].IsPlaying }),
        activity = _events.Take(30).Select(e => new
        {
            id = e.Id, camera = e.CameraName, kind = e.Label, start = e.Start, active = e.IsActive, duration = e.DurationText,
            ai = e.VerdictText, attention = e.Verdict?.AttentionLabel ?? (e.Alert ? "Look now" : ""), alert = e.Alert, source = e.Source,
            snapshots = e.Snapshots.ToArray(),
        }),
    };

    private async Task<byte[]?> SnapshotAsync(int index, CancellationToken ct)
    {
        if (index < 0 || index >= _cameraConfigs.Count) return null;
        using var client = new ReolinkClient(_cameraConfigs[index]);
        try { return await client.GetSnapshotAsync(ct); } catch (Exception) { return null; }
    }

    private async void PairPhone_Click(object sender, RoutedEventArgs e)
    {
        MenuFlyout.Hide();
        if (_gateway is null)
        {
            PairingUrl.Text = "The phone gateway isn't running (check gateway.enabled and the assistant settings).";
            PairingQr.Source = null;
        }
        else
        {
            PairingUrl.Text = _gateway.TailnetUrl is { } ts ? $"Home: {_gateway.Url}   Away (Tailscale): {ts}" : _gateway.Url;
            PairingQr.Source = await QrImageAsync(_gateway.PairingPayload);
        }
        PairingPage.Visibility = Visibility.Visible;
    }

    private async void ResetPairing_Click(object sender, RoutedEventArgs e)
    {
        if (_gateway is null) return;
        _gateway.ResetPairing();
        PairingQr.Source = await QrImageAsync(_gateway.PairingPayload);
    }

    private void ClosePairing_Click(object sender, RoutedEventArgs e)
    {
        PairingPage.Visibility = Visibility.Collapsed;
        PairingQr.Source = null; // don't leave the key on screen
    }

    private static async Task<Microsoft.UI.Xaml.Media.Imaging.BitmapImage> QrImageAsync(string payload)
    {
        using var generator = new QRCoder.QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCoder.QRCodeGenerator.ECCLevel.M);
        var png = new QRCoder.PngByteQRCode(data).GetGraphic(12);
        var image = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        await stream.WriteAsync(png.AsBuffer());
        stream.Seek(0);
        await image.SetSourceAsync(stream);
        return image;
    }

    // ---- Keyboard (convenience only; everything is also reachable by touch) -----

    private void Quit_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => Close();

    private void ToggleFullScreen_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SetFullScreen(!IsFullScreen);
        args.Handled = true;
    }

    private void Escape_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_focused is not null) Unfocus(); else ShowHome();
        args.Handled = true;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _clock.Stop();
        _monitor?.Dispose();
        _tempest?.Dispose();
        _taskTools?.Dispose();
        _voice?.Dispose();
        if (_gateway is not null) _ = _gateway.DisposeAsync().AsTask();
        if (_agent is not null) _ = _agent.DisposeAsync().AsTask();
        foreach (var t in _tiles) t.Dispose();
    }
}
