using Microsoft.UI.Dispatching;
using SideCarMonitor.Models;

namespace SideCarMonitor.Services;

/// <summary>
/// Polls each camera's detection state and turns it into MotionEvents.
/// All callbacks are raised on the UI thread.
/// </summary>
public sealed class MotionMonitor : IDisposable
{
    private readonly IReadOnlyList<CameraConfig> _cameras;
    private readonly TimeSpan _interval;
    private readonly DispatcherQueue _ui;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Camera index, current flags (None when idle).</summary>
    public event Action<int, DetectionKind>? StateChanged;
    /// <summary>A new activity period started.</summary>
    public event Action<MotionEvent>? EventStarted;
    /// <summary>Camera index, error message or null when the camera recovers.</summary>
    public event Action<int, string?>? ApiStatusChanged;

    public MotionMonitor(IReadOnlyList<CameraConfig> cameras, TimeSpan interval, DispatcherQueue ui)
    {
        _cameras = cameras;
        _interval = interval < TimeSpan.FromMilliseconds(250) ? TimeSpan.FromMilliseconds(250) : interval;
        _ui = ui;
    }

    public void Start()
    {
        for (var i = 0; i < _cameras.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(_cameras[i].Host)) continue;
            var index = i;
            _ = Task.Run(() => PollLoopAsync(index, _cts.Token));
        }
    }

    private async Task PollLoopAsync(int index, CancellationToken ct)
    {
        var camera = _cameras[index];
        using var client = new ReolinkClient(camera);
        var last = DetectionKind.None;
        string? lastError = "";
        MotionEvent? current = null;

        while (!ct.IsCancellationRequested)
        {
            DetectionKind state;
            string? error = null;
            try
            {
                state = await client.GetDetectionStateAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                state = DetectionKind.None;
                error = ex.Message;
            }

            if (error != lastError)
            {
                lastError = error;
                Post(() => ApiStatusChanged?.Invoke(index, error));
            }

            if (state != last)
            {
                var now = DateTime.Now;
                var ev = current;
                if (last == DetectionKind.None && state != DetectionKind.None)
                {
                    current = new MotionEvent(camera.Name, index, state, now);
                    var started = current;
                    Post(() => EventStarted?.Invoke(started));
                }
                else if (state == DetectionKind.None && ev is not null)
                {
                    current = null;
                    Post(() => ev.End = now);
                }
                else if (ev is not null)
                {
                    var kinds = state;
                    Post(() => ev.Kinds |= kinds);
                }

                var s = state;
                Post(() => StateChanged?.Invoke(index, s));
                last = state;
            }

            try { await Task.Delay(error is null ? _interval : TimeSpan.FromSeconds(10), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void Post(Action action) => _ui.TryEnqueue(() => action());

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
