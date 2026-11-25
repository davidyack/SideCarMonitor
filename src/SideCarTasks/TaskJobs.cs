using System.Text;

namespace SideCarTasks;

/// <summary>
/// Daily jobs, each run at most once per local date (in your saved timezone) no matter how often
/// they're triggered: by the Windows scheduled task (works with the kiosk closed) or the kiosk's catch-up check.
/// </summary>
public sealed class TaskJobs(TaskService tasks, Func<DateTimeOffset>? now = null)
{
    public const string RolloverJob = "rollover";
    public const string BriefJob = "brief";

    /// <summary>The brief is written once you're likely up, so it includes overnight work.</summary>
    public static readonly TimeOnly BriefAfter = new(6, 0);

    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.Now);

    public sealed record JobReport(int Moved, BriefEntry? Brief);

    /// <summary>Runs whichever daily jobs are due. Idempotent.</summary>
    public JobReport RunDue()
    {
        var today = tasks.Today;
        var moved = 0;
        tasks.MigrateTodayPins(); // every check, not just once a day (it's a no-op once done)
        var data = tasks.Store.Read();

        if (!data.JobRuns.TryGetValue(RolloverJob, out var r) || r != today)
        {
            moved = tasks.RunRollover().Count;
            tasks.Store.Update(Actor.System, d => d.JobRuns[RolloverJob] = today);
        }

        BriefEntry? brief = null;
        var localTime = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(_now(), tasks.TimeZone).DateTime);
        data = tasks.Store.Read();
        if (localTime >= BriefAfter && (!data.JobRuns.TryGetValue(BriefJob, out var b) || b != today))
            brief = WriteBrief(today);

        return new JobReport(moved, brief);
    }

    /// <summary>
    /// One short brief, only when something matters: hard deadlines, things that need you, and useful work
    /// the assistant finished. Routine rollovers are grouped into one line.
    /// </summary>
    public BriefEntry? WriteBrief(DateOnly today) =>
        tasks.Store.Update(Actor.System, d =>
        {
            d.JobRuns[BriefJob] = today;
            var since = d.LastBriefAt ?? DateTimeOffset.MinValue;
            var open = d.Tasks.Where(t => t.Status == TodoStatus.Open).ToList();
            var lines = new List<string>();

            foreach (var t in open.Where(t => t.MissedDeadline is not null).OrderBy(t => t.MissedDeadline))
                lines.Add($"MISSED hard deadline: {t.Title} (was {t.MissedDeadline:ddd MMM d}; on today)");
            var deadlines = open.Where(t => t.DateType == DateKind.Hard && t.Date is { } dt && dt >= today && dt <= today.AddDays(2))
                                .OrderBy(t => t.Date).ToList();
            foreach (var t in deadlines)
                lines.Add(t.Date == today ? $"Hard deadline today: {t.Title}" : $"Hard deadline {t.Date:ddd}: {t.Title}");

            foreach (var t in open.Where(tasks.NeedsMe))
                lines.Add(t.PendingQuestionId is not null ? $"Question for you on \"{t.Title}\""
                        : t.Deliverable.PendingRevision is not null ? $"Revision to review on \"{t.Title}\""
                        : $"\"{t.Title}\" has rolled forward {tasks.DaysRolled(t)} days: pick a date, drop the date, or mark it done");

            var work = open.Where(t => t.Deliverable.Versions.Any(v => v.Author == Actor.Assistant && v.At > since)
                                       || (t.Deliverable.PendingRevision is { } p && p.At > since))
                           .Select(t => t.Title).ToList();
            if (work.Count > 0) lines.Add($"Assistant drafted work on: {string.Join(", ", work)}");

            var rolled = d.Tasks.SelectMany(t => t.History.Where(e => e.Rollover is { Undone: false } && e.At > since)
                                                  .Select(_ => t.Title)).Distinct().ToList();
            if (rolled.Count > 0)
                lines.Add($"Rolled {rolled.Count} unfinished to-do{(rolled.Count == 1 ? "" : "s")} to today: {string.Join("; ", rolled)}");

            // Only rollovers? Still worth one line; nothing at all? No brief.
            if (lines.Count == 0) return null;
            var brief = new BriefEntry { Date = today, At = _now(), Text = string.Join("\n", lines) };
            d.Briefs.Add(brief);
            if (d.Briefs.Count > 30) d.Briefs.RemoveRange(0, d.Briefs.Count - 30);
            d.LastBriefAt = brief.At;
            return brief;
        });
}
