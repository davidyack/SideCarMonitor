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
    private VoiceService? _voice;
    private CameraTile? _focused;

    public MainWindow(AppSettings settings, bool createdNewSettings)
    {
        _settings = settings;
        _wx = new WeatherFormat(settings.Weather.Imperial);
        InitializeComponent();
        StartTempest();
        StartAssistant();

        PlaceOnKioskMonitor();

        EventList.ItemsSource = _events;
        _events.CollectionChanged += (_, _) => UpdateActivitySummary();

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
        var cameras = _settings.Cameras.Where(c => c.IsConfigured).ToList();
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

    private void OnEventStarted(MotionEvent ev)
    {
        ev.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MotionEvent.IsActive)) UpdateActivitySummary();
        };
        _events.Insert(0, ev);
        while (_events.Count > Math.Max(10, _settings.MaxEvents))
            _events.RemoveAt(_events.Count - 1);
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
        UpdateWeatherStatus();
    }

    // ---- Navigation -----------------------------------------------------------------
    // The Cameras page is always underneath; Weather and Home are opaque pages layered on top.

    private void ShowHome()
    {
        WeatherPage.Visibility = Visibility.Collapsed;
        HomePage.Visibility = Visibility.Visible;
    }

    private void ShowCameras()
    {
        WeatherPage.Visibility = Visibility.Collapsed;
        HomePage.Visibility = Visibility.Collapsed;
        LayoutTiles();
    }

    private void ShowWeather()
    {
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
        _agent.ToolHandler = name => name switch
        {
            "get_local_weather" => LocalWeatherForAgent(),
            _ => new { error = $"Unknown tool '{name}'." },
        };
        Assistant.Initialize(_agent, _voice, null);
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
        _voice?.Dispose();
        if (_agent is not null) _ = _agent.DisposeAsync().AsTask();
        foreach (var t in _tiles) t.Dispose();
    }
}
