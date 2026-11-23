using LibVLCSharp.Platforms.Windows;
using LibVLCSharp.Shared;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SideCarMonitor.Models;

namespace SideCarMonitor.Controls;

/// <summary>
/// One live Reolink RTSP stream plus motion/AI overlays. Reconnects on its own when the stream drops.
/// </summary>
public sealed partial class CameraTile : UserControl, IDisposable
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MotionLinger = TimeSpan.FromSeconds(30);

    private readonly CameraConfig _camera;
    private readonly DispatcherQueueTimer _reconnect;
    private readonly DispatcherQueueTimer _idleStop;
    private bool _wantStream;
    private LibVLC? _libVlc;
    private MediaPlayer? _player;
    private bool _disposed;

    public bool IsPlaying { get; private set; }

    /// <summary>Width/height of the camera's video, learned once the stream starts (dual-lens cameras are ~32:9).</summary>
    public double VideoAspect { get; private set; } = 16.0 / 9.0;

    /// <summary>Raised on the UI thread when VideoAspect changes, so the page can re-layout.</summary>
    public event EventHandler? AspectChanged;

    public CameraTile(CameraConfig camera)
    {
        _camera = camera;
        InitializeComponent();
        NameText.Text = camera.Name;

        _reconnect = DispatcherQueue.CreateTimer();
        _reconnect.Interval = ReconnectDelay;
        _reconnect.IsRepeating = false;
        _reconnect.Tick += (_, _) => StartStream();

        // On-motion cameras keep streaming for a while after the detection clears, then go back to sleep.
        _idleStop = DispatcherQueue.CreateTimer();
        _idleStop.Interval = MotionLinger;
        _idleStop.IsRepeating = false;
        _idleStop.Tick += (_, _) => StopStreamAndSleep();
    }

    // The VideoView raises Initialized once its swap chain exists; LibVLC must be created with those options.
    private void Video_Initialized(object? sender, InitializedEventArgs e)
    {
        if (_disposed) return;
        _libVlc = new LibVLC(enableDebugLogs: false, e.SwapChainOptions);
        _player = new MediaPlayer(_libVlc) { Mute = true };
        _player.Playing += (_, _) => DispatcherQueue.TryEnqueue(OnPlaying);
        _player.EncounteredError += (_, _) => DispatcherQueue.TryEnqueue(() => OnStreamLost("Stream error"));
        _player.EndReached += (_, _) => DispatcherQueue.TryEnqueue(() => OnStreamLost("Stream ended"));
        // Query the size off LibVLC's event thread; calling back into LibVLC from its own callback can deadlock.
        _player.Vout += (_, e) => { if (e.Count > 0) _ = Task.Run(ReadVideoAspect); };
        _player.Playing += (_, _) => _ = Task.Run(ReadVideoAspect);
        Video.MediaPlayer = _player;

        if (_camera.StreamOnMotionOnly)
            ShowSleeping();
        else
        {
            _wantStream = true;
            StartStream();
        }
    }

    private void StartStream()
    {
        if (_disposed || !_wantStream || _libVlc is null || _player is null) return;
        ShowOverlay(_camera.StreamOnMotionOnly ? "Waking camera" : "Connecting", spinning: true);

        var vlc = _libVlc;
        var player = _player;
        var url = new Uri(_camera.GetRtspUrl());
        // LibVLC calls can block; keep them off the UI thread. The player holds its own reference to the media.
        _ = Task.Run(() =>
        {
            using var media = new Media(vlc, url,
                ":rtsp-tcp",             // TCP is far more reliable than UDP for Reolink over Wi-Fi
                ":network-caching=400",
                ":no-audio");
            player.Play(media);
        });
    }

    private async Task ReadVideoAspect()
    {
        // The size isn't always known the moment the video output starts, so retry for a few seconds.
        uint w = 0, h = 0;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var p = _player;
            if (p is null) return;
            if (p.Size(0, ref w, ref h) && w > 0 && h > 0) break;
            await Task.Delay(250);
        }
        if (w == 0 || h == 0) return;
        var aspect = (double)w / h;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (Math.Abs(aspect - VideoAspect) < 0.01) return;
            VideoAspect = aspect;
            AspectChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    private void OnPlaying()
    {
        if (!_wantStream) return;
        IsPlaying = true;
        Overlay.Visibility = Visibility.Collapsed;
        LiveDot.Fill = (Brush)Application.Current.Resources["OkBrush"];
    }

    private void OnStreamLost(string reason)
    {
        if (_disposed || !_wantStream) return;
        IsPlaying = false;
        LiveDot.Fill = (Brush)Application.Current.Resources["MutedText"];
        ShowOverlay($"{reason} - retrying in {ReconnectDelay.TotalSeconds:0}s", spinning: false);
        var player = _player;
        _ = Task.Run(() => player?.Stop()).ContinueWith(_ => DispatcherQueue.TryEnqueue(() => _reconnect.Start()));
    }

    private void StopStreamAndSleep()
    {
        _wantStream = false;
        _reconnect.Stop();
        IsPlaying = false;
        var player = _player;
        _ = Task.Run(() => player?.Stop());
        ShowSleeping();
    }

    private void ShowSleeping()
    {
        LiveDot.Fill = (Brush)Application.Current.Resources["MutedText"];
        ShowOverlay("Battery camera - live view starts on motion", spinning: false);
    }

    private void ShowOverlay(string text, bool spinning)
    {
        OverlayText.Text = text;
        Spinner.IsActive = spinning;
        Spinner.Visibility = spinning ? Visibility.Visible : Visibility.Collapsed;
        Overlay.Visibility = Visibility.Visible;
    }

    /// <summary>Highlights the tile while motion or an AI detection is active.</summary>
    public void SetDetection(DetectionKind kinds)
    {
        var active = kinds != DetectionKind.None;
        Frame.BorderBrush = (Brush)Application.Current.Resources[active ? "AlertBrush" : "PanelBorder"];
        DetectionBadge.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        if (_camera.StreamOnMotionOnly)
        {
            if (active)
            {
                _idleStop.Stop();
                if (!_wantStream) { _wantStream = true; StartStream(); }
            }
            else if (_wantStream)
            {
                _idleStop.Start();
            }
        }

        if (active)
            DetectionText.Text = new MotionEvent("", 0, kinds, DateTime.Now).Label.ToUpperInvariant();
    }

    public void SetApiError(string? error)
    {
        ApiErrorBadge.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        ApiErrorText.Text = error is null ? "" : $"Motion API: {error}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _reconnect.Stop();
        _idleStop.Stop();
        var player = _player;
        var vlc = _libVlc;
        _player = null;
        _libVlc = null;
        Video.MediaPlayer = null;
        // Stop/dispose off the UI thread; LibVLC can block while tearing down RTSP sessions.
        _ = Task.Run(() =>
        {
            player?.Stop();
            player?.Dispose();
            vlc?.Dispose();
        });
    }
}
