namespace SideCarMonitor.Services;

/// <summary>
/// What SideCar's own machinery did, for the Activity feed's System filter: safeguards that stopped the agent
/// (blocked home actions, refused background-review actions, denied network requests), home control paused or
/// resumed, the agent sandbox starting or stopping, and cameras or Home Assistant going offline or recovering.
/// Any thread can report; MainWindow adds them to the feed on the UI thread.
/// </summary>
public static class SystemEvents
{
    /// <summary>(what, detail, worth a look).</summary>
    public static event Action<string, string, bool>? Reported;

    private static readonly Dictionary<string, DateTime> LastSeen = [];

    /// <param name="what">Short headline, e.g. "Stopped the agent: unlock front door".</param>
    /// <param name="detail">Why or where.</param>
    /// <param name="alert">Show as "worth a look".</param>
    /// <param name="dedupe">Same key within this window is reported once (e.g. repeated network denials).</param>
    public static void Report(string what, string detail = "", bool alert = false, TimeSpan? dedupe = null)
    {
        if (dedupe is { } window)
        {
            lock (LastSeen)
            {
                var key = what + "|" + detail;
                if (LastSeen.TryGetValue(key, out var at) && DateTime.Now - at < window) return;
                LastSeen[key] = DateTime.Now;
            }
        }
        Reported?.Invoke(what, detail, alert);
    }
}
