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

        LoadSavedEvents();
        EventList.ItemsSource = _events;
        _events.CollectionChanged += (_, _) => UpdateActivitySummary();
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
        _monitor.ApiStatusChanged += (i, err) => _tiles[i].SetApiError(err);
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
            if (_judge is not null)
            {
                // Ask the decision model what's in the picture (the later picture wins).
                using var judgeCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                ev.Verdict = await _judge.JudgeAsync(jpeg, ev.CameraName, CameraWatches(ev), judgeCts.Token) ?? ev.Verdict;
                SaveEvents();
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

    private void EventList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is MotionEvent ev && ev.CameraIndex < _tiles.Count)
        {
            _focused = _tiles[ev.CameraIndex];
            LayoutTiles();
        }
    }

    private void UpdateActivitySummary()
    {
        NoEventsText.Visibility = _events.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
        HouseView.Visibility = Visibility.Collapsed;
        WeatherPage.Visibility = Visibility.Collapsed;
        HomePage.Visibility = Visibility.Collapsed;
        TasksPageView.Visibility = Visibility.Visible;
        TasksPageView.Show();
    }

    private void ShowHome()
    {
        HouseView.Visibility = Visibility.Collapsed;
        TasksPageView.Visibility = Visibility.Collapsed;
        WeatherPage.Visibility = Visibility.Collapsed;
        HomePage.Visibility = Visibility.Visible;
    }

    private void ShowCameras()
    {
        HouseView.Visibility = Visibility.Collapsed;
        TasksPageView.Visibility = Visibility.Collapsed;
        WeatherPage.Visibility = Visibility.Collapsed;
        HomePage.Visibility = Visibility.Collapsed;
        LayoutTiles();
    }

    private void ShowWeather()
    {
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
            ai = e.VerdictText, attention = e.Verdict?.AttentionLabel ?? "", alert = e.Verdict?.Alert ?? false,
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
