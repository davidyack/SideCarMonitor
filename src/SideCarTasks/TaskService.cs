using System.Security.Cryptography;
using System.Text;

namespace SideCarTasks;

/// <summary>Thrown when a change isn't allowed (by actor or state) or is based on an out-of-date version.</summary>
public sealed class TaskRuleException(string message) : Exception(message);

public enum TaskView { Today, Upcoming, NeedsMe, Done }

/// <summary>
/// All task rules live here, in app code: date math (in your saved timezone), authorization by actor,
/// optimistic concurrency, rollover with audit + undo, deliverable versioning, and view membership.
/// The assistant (model) only interprets requests and produces work; it calls these methods through tools.
/// </summary>
public sealed class TaskService
{
    /// <summary>After this many days of rolling forward, a to-do asks you to decide (new date, no date, or done).</summary>
    public const int RollDaysBeforeAsk = 7;

    /// <summary>How many days an open to-do has been rolling forward (0 if it isn't).</summary>
    /// Only a to-do sitting on today (or still overdue) is rolling; one moved ahead to a later day isn't.
    public int DaysRolled(TaskItem t) =>
        t.OriginalTarget is { } o && t.Status == TodoStatus.Open && t.Date is { } d && d <= Today ? Math.Max(0, Today.DayNumber - o.DayNumber) : 0;

    private readonly TaskStore _store;
    private readonly Func<DateTimeOffset> _now;

    public TaskService(TaskStore store, Func<DateTimeOffset>? now = null, string? defaultTimeZoneId = null)
    {
        _store = store;
        _now = now ?? (() => DateTimeOffset.Now);
        // Save the timezone once; "today" is always computed in it, even if the PC's zone changes later.
        var data = _store.Read();
        if (data.TimeZoneId is null)
            _store.Update(Actor.System, d => d.TimeZoneId ??= defaultTimeZoneId ?? TimeZoneInfo.Local.Id);
    }

    public TaskStore Store => _store;

    public TimeZoneInfo TimeZone => TimeZoneInfo.FindSystemTimeZoneById(_store.Read().TimeZoneId ?? TimeZoneInfo.Local.Id);

    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_now(), TimeZone).DateTime);

    // ---- Reading ------------------------------------------------------------------------------

    public TaskItem? Get(string id) => _store.Read().Tasks.FirstOrDefault(t => t.Id == id);

    public IReadOnlyList<TaskItem> All() => _store.Read().Tasks;

    /// <summary>
    /// True when a to-do has rolled forward a week (it keeps rolling, but asks you to decide), or you undid a roll
    /// and it's sitting overdue.
    /// </summary>
    public bool NeedsDecision(TaskItem t) =>
        t.Status == TodoStatus.Open &&
        (DaysRolled(t) >= RollDaysBeforeAsk || (t.RolloverPaused && t.Date is { } d && d < Today));

    public bool NeedsMe(TaskItem t) =>
        t.Status == TodoStatus.Open &&
        (NeedsDecision(t) || t.PendingQuestionId is not null || t.Deliverable.PendingRevision is not null);

    public bool IsOverdue(TaskItem t) => t.Status == TodoStatus.Open && t.Date is { } d && d < Today;

    /// <summary>Each open task appears in exactly one view (Needs me wins), so there's no second inbox.</summary>
    public IReadOnlyList<TaskItem> View(TaskView view)
    {
        var today = Today;
        var tasks = _store.Read().Tasks;
        IEnumerable<TaskItem> q = view switch
        {
            TaskView.NeedsMe => tasks.Where(NeedsMe),
            TaskView.Today => tasks.Where(t => t.Status == TodoStatus.Open && !NeedsMe(t) && t.Date is { } d && d <= today),
            TaskView.Upcoming => tasks.Where(t => t.Status == TodoStatus.Open && !NeedsMe(t) && (t.Date is null || t.Date > today)),
            TaskView.Done => tasks.Where(t => t.Status == TodoStatus.Done),
            _ => [],
        };
        return view switch
        {
            TaskView.Done => q.OrderByDescending(t => t.Completed).ToList(),
            TaskView.Upcoming => q.OrderBy(t => t.Date is null).ThenBy(t => t.Date).ThenBy(t => t.Priority).ToList(),
            // Overdue hard deadlines first, then hard deadlines, then priority.
            _ => q.OrderByDescending(t => t.DateType == DateKind.Hard && IsOverdue(t))
                  .ThenByDescending(t => t.DateType == DateKind.Hard && t.Date is not null)
                  .ThenBy(t => t.Priority).ThenBy(t => t.Date).ToList(),
        };
    }

    // ---- Changes you make (UI, or the assistant acting on your explicit request) -------------

    public TaskItem Add(Actor actor, string title, string? details = null, Priority priority = Priority.Medium,
                        DateOnly? date = null, DateKind dateType = DateKind.Flexible, bool pinToday = false,
                        string? link = null, string? linkLabel = null)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new TaskRuleException("A task needs a title.");
        return _store.Update(actor, d =>
        {
            var now = _now();
            var t = new TaskItem
            {
                Id = NewTaskId(d),
                Title = title.Trim(),
                Details = string.IsNullOrWhiteSpace(details) ? null : details.Trim(),
                Link = string.IsNullOrWhiteSpace(link) ? null : link.Trim(),
                LinkLabel = string.IsNullOrWhiteSpace(link) || string.IsNullOrWhiteSpace(linkLabel) ? null : linkLabel.Trim(),
                Priority = priority,
                // "For today" means dated today (there's no separate Today pin any more).
                Date = date ?? (pinToday ? Today : null),
                DateType = date is null ? DateKind.Flexible : dateType,
                Created = now,
                Updated = now,
            };
            Log(t, actor, EntryKind.Activity, $"Created by {Who(actor)}.");
            d.Tasks.Add(t);
            return t;
        });
    }

    /// <summary>Edits fields. Changing the date is an explicit manual reschedule: it resets rollover.</summary>
    public TaskItem Edit(Actor actor, string id, int? expectedVersion = null, string? title = null, string? details = null,
                         Priority? priority = null, DateOnly? date = null, bool clearDate = false, DateKind? dateType = null)
    {
        RequireUserIntent(actor, "edit a task");
        return Mutate(actor, id, expectedVersion, t =>
        {
            var changes = new List<string>();
            if (title is not null && title.Trim() != t.Title) { t.Title = title.Trim(); changes.Add("title"); }
            if (details is not null && details.Trim() != (t.Details ?? "")) { t.Details = details.Trim().Length == 0 ? null : details.Trim(); changes.Add("details"); }
            if (priority is { } p && p != t.Priority) { t.Priority = p; changes.Add($"priority → {p}"); }
            if (dateType is { } k && k != t.DateType) { t.DateType = k; changes.Add(k == DateKind.Hard ? "now a hard deadline" : "now a flexible target"); }
            if (clearDate && t.Date is not null)
            {
                changes.Add($"date cleared (was {t.Date:ddd MMM d})");
                t.Date = null;
                ResetRollover(t);
            }
            else if (date is { } nd && nd != t.Date)
            {
                changes.Add($"date {t.Date?.ToString("ddd MMM d") ?? "none"} → {nd:ddd MMM d}");
                t.Date = nd;
                ResetRollover(t);
            }
            if (changes.Count == 0) return false;
            Log(t, actor, EntryKind.Activity, $"Edited by {Who(actor)}: {string.Join(", ", changes)}.");
            if (changes.Any(c => c is "title" or "details")) t.PendingQuestionId = null; // context changed
            return true;
        });
    }

    /// <summary>Tasks of one day in the order you set (dragged order first, then by priority).</summary>
    public static IEnumerable<TaskItem> InDayOrder(IEnumerable<TaskItem> tasks) =>
        tasks.OrderBy(t => t.DayRank ?? double.MaxValue).ThenBy(t => t.Priority).ThenBy(t => t.Created);

    /// <summary>
    /// A drop in the weekly plan: <paramref name="ids"/> is the day's new order, top first. Tasks dropped in from
    /// another day move to <paramref name="date"/> (null = no day) as a flexible target, like any manual reschedule.
    /// Hard deadlines can be reordered within their day but don't move to another day. Only you can do this.
    /// </summary>
    public int OrderDay(Actor actor, DateOnly? date, IReadOnlyList<string> ids)
    {
        RequireUserIntent(actor, "reorder your day");
        if (ids.Count == 0) return 0;
        return _store.Update(actor, d =>
        {
            var tasks = ids.Select(id => Find(d, id)).ToList();
            if (tasks.FirstOrDefault(t => t.DateType == DateKind.Hard && t.Date != date) is { } hard)
                throw new TaskRuleException($"\"{hard.Title}\" has a hard deadline; change its date on the task itself.");
            var changed = 0;
            for (var i = 0; i < tasks.Count; i++)
            {
                var t = tasks[i];
                var touched = false;
                if (t.Date != date)
                {
                    Log(t, actor, EntryKind.Activity, date is { } nd
                        ? $"Moved by {Who(actor)} from {t.Date?.ToString("ddd MMM d") ?? "no day"} to {nd:ddd MMM d}."
                        : $"Date cleared by {Who(actor)} (was {t.Date:ddd MMM d}).");
                    t.Date = date;
                    t.DateType = DateKind.Flexible;
                    ResetRollover(t);
                    touched = true;
                }
                if (t.DayRank != i) { t.DayRank = i; touched = true; }
                if (touched) { Touch(t); changed++; }
            }
            return changed;
        });
    }

    /// <summary>"Put it on today" (the old Today pin): dates the to-do today as a flexible target. Unpinning does nothing.</summary>
    public TaskItem PinToday(Actor actor, string id, bool pinned)
    {
        RequireUserIntent(actor, "put a task on today");
        if (!pinned) return Get(id) ?? throw new TaskRuleException("No such task.");
        return Edit(actor, id, date: Today, dateType: DateKind.Flexible);
    }

    public TaskItem SetAssistantHelp(Actor actor, string id, bool on)
    {
        RequireUserIntent(actor, "change \"Let assistant help\"");
        return Mutate(actor, id, null, t =>
        {
            if (t.AssistantHelp == on) return false;
            t.AssistantHelp = on;
            Log(t, actor, EntryKind.Activity, on ? "Assistant help turned on." : "Assistant help turned off.");
            return true;
        });
    }

    /// <summary>Only you can complete a task. A deliverable is progress, not completion.</summary>
    public TaskItem MarkDone(Actor actor, string id)
    {
        RequireUserIntent(actor, "mark a task done");
        return Mutate(actor, id, null, t =>
        {
            if (t.Status == TodoStatus.Done) return false;
            t.Status = TodoStatus.Done;
            t.Completed = _now();
            t.PinnedToday = false; // the pin lasts until cleared or the task is finished
            t.PendingQuestionId = null;
            Log(t, actor, EntryKind.Activity, "Marked done.");
            return true;
        });
    }

    public TaskItem Reopen(Actor actor, string id)
    {
        RequireUserIntent(actor, "reopen a task");
        return Mutate(actor, id, null, t =>
        {
            if (t.Status == TodoStatus.Open) return false;
            t.Status = TodoStatus.Open;
            t.Completed = null;
            Log(t, actor, EntryKind.Activity, "Reopened.");
            return true;
        });
    }

    /// <summary>Needs-me decision: drop the date (keeps the task, stops rollover).</summary>
    public TaskItem DropDate(Actor actor, string id) => Edit(actor, id, clearDate: true);

    /// <summary>Resume automatic rollover after an Undo paused it.</summary>
    public TaskItem ResumeRollover(Actor actor, string id)
    {
        RequireUserIntent(actor, "resume automatic rollover");
        return Mutate(actor, id, null, t =>
        {
            if (!t.RolloverPaused) return false;
            t.RolloverPaused = false;
            Log(t, actor, EntryKind.Activity, "Automatic rollover resumed.");
            return true;
        });
    }

    // ---- Notes, questions, deliverables -----------------------------------------------------

    /// <summary>
    /// Adds a note. Your note while the assistant has a question open counts as the answer.
    /// Identical notes by the same author within 10 minutes are ignored (safe retries, no duplicates).
    /// </summary>
    public TaskItem AddNote(Actor actor, string id, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new TaskRuleException("The note is empty.");
        return Mutate(actor, id, null, t =>
        {
            var trimmed = text.Trim();
            if (t.History.Any(e => e.Author == actor && e.Text == trimmed && _now() - e.At < TimeSpan.FromMinutes(10)))
                return false;
            var kind = actor == Actor.User && t.PendingQuestionId is not null ? EntryKind.Answer : EntryKind.Note;
            Log(t, actor, kind, trimmed);
            if (actor == Actor.User) t.PendingQuestionId = null;
            return true;
        });
    }

    /// <summary>The assistant asks you one specific question and stops on this task until you answer.</summary>
    public TaskItem Ask(Actor actor, string id, string question)
    {
        if (actor != Actor.Assistant) throw new TaskRuleException("Only the assistant asks questions on a task.");
        if (string.IsNullOrWhiteSpace(question)) throw new TaskRuleException("The question is empty.");
        return Mutate(actor, id, null, t =>
        {
            if (t.PendingQuestionId is not null) return false; // one open question at a time
            var e = Log(t, actor, EntryKind.Question, question.Trim());
            t.PendingQuestionId = e.Id;
            return true;
        });
    }

    /// <summary>
    /// Saves deliverable content. The assistant never overwrites your work: if the current version is yours,
    /// or changed since the version the assistant started from, its output is held as a pending revision.
    /// </summary>
    public TaskItem SaveDeliverable(Actor actor, string id, string content, int? basedOnVersion = null)
    {
        if (string.IsNullOrWhiteSpace(content)) throw new TaskRuleException("The deliverable is empty.");
        return Mutate(actor, id, null, t =>
        {
            var current = t.Deliverable.Current;
            if (current is not null && current.Content == content.Trim()) return false; // nothing new
            var version = new DeliverableVersion { Author = actor, At = _now(), Content = content.Trim() };

            var protectYours = actor == Actor.Assistant && current is not null
                && (current.Author == Actor.User || (basedOnVersion is { } b && b != current.Number));
            if (protectYours)
            {
                version.Number = current!.Number; // a proposal on top of your version
                t.Deliverable.PendingRevision = version;
                Log(t, actor, EntryKind.Activity, "Assistant suggested a revision to your edited deliverable (Accept or Dismiss).");
                return true;
            }

            version.Number = (current?.Number ?? 0) + 1;
            t.Deliverable.Versions.Add(version);
            Log(t, actor, EntryKind.Activity, $"Deliverable v{version.Number} saved by {Who(actor)}.");
            return true;
        });
    }

    public TaskItem AcceptRevision(Actor actor, string id)
    {
        RequireUserIntent(actor, "accept a revision");
        return Mutate(actor, id, null, t =>
        {
            var rev = t.Deliverable.PendingRevision ?? throw new TaskRuleException("There's no pending revision.");
            t.Deliverable.Versions.Add(new DeliverableVersion
            {
                Number = (t.Deliverable.Current?.Number ?? 0) + 1, Author = Actor.Assistant, At = _now(), Content = rev.Content,
            });
            t.Deliverable.PendingRevision = null;
            Log(t, actor, EntryKind.Activity, $"Accepted the assistant's revision as v{t.Deliverable.Current!.Number}.");
            return true;
        });
    }

    public TaskItem DismissRevision(Actor actor, string id)
    {
        RequireUserIntent(actor, "dismiss a revision");
        return Mutate(actor, id, null, t =>
        {
            if (t.Deliverable.PendingRevision is null) return false;
            t.Deliverable.PendingRevision = null;
            Log(t, actor, EntryKind.Activity, "Dismissed the assistant's revision.");
            return true;
        });
    }

    // ---- Rollover -------------------------------------------------------------------------------

    public sealed record RolloverResult(string TaskId, string Title, DateOnly From, DateOnly To);

    /// <summary>
    /// Moves overdue flexible targets forward from today: high +1, medium +3, low +5 days.
    /// Skips tasks due today, hard deadlines, undated, done, help off, paused, already moved today,
    /// and tasks already moved twice (those surface in Needs me). One atomic save covers every move
    /// and its audit entry. Safe to run repeatedly on the same day.
    /// </summary>
    /// <summary>The old "On Today" pin is gone: an undated pinned to-do becomes dated today, once. Safe to call often.</summary>
    public int MigrateTodayPins()
    {
        if (!_store.Read().Tasks.Any(t => t.PinnedToday)) return 0;
        var today = Today;
        return _store.Update(Actor.System, d =>
        {
            var n = 0;
            foreach (var t in d.Tasks.Where(t => t.PinnedToday))
            {
                t.PinnedToday = false;
                if (t.Status == TodoStatus.Open && t.Date is null)
                {
                    t.Date = today;
                    Log(t, Actor.System, EntryKind.Activity, $"Was pinned to Today: now dated {today:ddd MMM d}.");
                    Touch(t);
                    n++;
                }
            }
            return n;
        });
    }

    public IReadOnlyList<RolloverResult> RunRollover()
    {
        var today = Today;
        MigrateTodayPins();
        return _store.Update(Actor.System, d =>
        {
            var moved = new List<RolloverResult>();
            // Everything still open from an earlier day rolls to today, after today's own to-dos, in its old order.
            var nextRank = d.Tasks.Where(t => t.Date == today && t.DayRank is not null).Select(t => t.DayRank!.Value).DefaultIfEmpty(-1).Max() + 1;
            var due = d.Tasks.Where(t => t.Status == TodoStatus.Open && t.Date is { } from && from < today
                                         && !t.RolloverPaused && t.LastRolledOn != today)
                             .OrderBy(t => t.Date).ThenBy(t => t.DayRank ?? double.MaxValue).ThenBy(t => t.Priority).ToList();
            foreach (var t in due)
            {
                var from = t.Date!.Value;
                var wasHard = t.DateType == DateKind.Hard;
                t.OriginalTarget ??= from;
                if (wasHard) { t.MissedDeadline ??= from; t.DateType = DateKind.Flexible; }
                t.Date = today;
                t.DayRank = nextRank++;
                t.RolloverCount++;
                t.LastRolledOn = today;
                t.Version++;
                t.Updated = _now();
                var days = today.DayNumber - t.OriginalTarget.Value.DayNumber;
                var entry = NewEntry(Actor.System, EntryKind.Activity,
                    $"Rolled from {from:ddd MMM d} to today ({today:ddd MMM d})" +
                    (wasHard ? $": missed its hard deadline of {from:ddd MMM d}" : "") +
                    $"; rolling {days} day{(days == 1 ? "" : "s")}" +
                    (days >= RollDaysBeforeAsk ? ". It's been a week: decide what to do with it." : "."));
                entry.Rollover = new RolloverRecord { From = from, To = today, VersionAfter = t.Version, WasHard = wasHard };
                t.History.Add(entry);
                moved.Add(new RolloverResult(t.Id, t.Title, from, today));
            }
            return moved;
        });
    }

    /// <summary>
    /// Undoes an automatic move: restores the prior date and pauses rollover until you reschedule or
    /// resume it. Refused if the task changed since the move (never overwrites a newer edit).
    /// </summary>
    public TaskItem UndoRollover(Actor actor, string id, string entryId)
    {
        RequireUserIntent(actor, "undo a rollover");
        return _store.Update(actor, d =>
        {
            var t = Find(d, id);
            var e = t.History.FirstOrDefault(x => x.Id == entryId)?.Rollover
                    ?? throw new TaskRuleException("That isn't an automatic move.");
            if (e.Undone) throw new TaskRuleException("That move was already undone.");
            if (t.Version != e.VersionAfter)
                throw new TaskRuleException("The task changed after that move, so Undo won't overwrite your newer edit.");
            t.Date = e.From;
            if (e.WasHard) { t.DateType = DateKind.Hard; if (t.MissedDeadline == e.From) t.MissedDeadline = null; }
            t.RolloverPaused = true;
            e.Undone = true;
            Touch(t);
            Log(t, actor, EntryKind.Activity, $"Undid the automatic move; back to {e.From:ddd MMM d}. Rollover paused until you reschedule or resume it.");
            return t;
        });
    }

    // ---- Assistant review support -----------------------------------------------------------

    /// <summary>
    /// Fingerprint of everything YOU control on a task. Assistant writes don't change it, so the assistant's
    /// own edits never retrigger it; your edits, notes and answers do.
    /// </summary>
    public static string Fingerprint(TaskItem t)
    {
        var sb = new StringBuilder()
            .Append(t.Title).Append('|').Append(t.Details).Append('|').Append(t.Priority).Append('|')
            .Append(t.Date).Append('|').Append(t.DateType).Append('|').Append(t.Status).Append('|');
        foreach (var e in t.History.Where(e => e.Author == Actor.User && e.Kind is EntryKind.Note or EntryKind.Answer))
            sb.Append(e.Id).Append(';');
        foreach (var v in t.Deliverable.Versions.Where(v => v.Author == Actor.User))
            sb.Append('v').Append(v.Number).Append(';');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    /// <summary>Is this task eligible for a background review right now?</summary>
    public bool EligibleForReview(TaskItem t, bool explicitRequest) =>
        t.Status == TodoStatus.Open
        && (explicitRequest || (t.AssistantHelp && t.PendingQuestionId is null && t.Deliverable.PendingRevision is null
                                && t.LastReviewFingerprint != Fingerprint(t)));

    public void MarkReviewed(string id, string fingerprint) =>
        _store.Update(Actor.System, d =>
        {
            var t = Find(d, id);
            t.LastReviewFingerprint = fingerprint; // bookkeeping only: no Version bump, no audit noise
            t.LastReviewedAt = _now();
            return true;
        });

    /// <summary>Tasks for a bounded sweep, most time-sensitive first.</summary>
    public IReadOnlyList<TaskItem> SweepCandidates(int max)
    {
        var today = Today;
        return _store.Read().Tasks
            .Where(t => EligibleForReview(t, explicitRequest: false))
            .OrderByDescending(t => t.DateType == DateKind.Hard && t.Date is not null)
            .ThenBy(t => t.Date ?? DateOnly.MaxValue)
            .ThenByDescending(t => t.PinnedToday)
            .ThenBy(t => t.Priority)
            .Take(max)
            .ToList();
    }

    // ---- Helpers --------------------------------------------------------------------------------

    /// <summary>Changes only a user (directly, or through the assistant on your explicit request) may make.</summary>
    private static void RequireUserIntent(Actor actor, string what)
    {
        if (actor != Actor.User) throw new TaskRuleException($"Only you can {what}; the assistant can't do that on its own.");
    }

    private TaskItem Mutate(Actor actor, string id, int? expectedVersion, Func<TaskItem, bool> change) =>
        _store.Update(actor, d =>
        {
            var t = Find(d, id);
            if (expectedVersion is { } v && v != t.Version)
                throw new TaskRuleException("This task changed since you opened it. Showing the latest version.");
            if (change(t)) Touch(t);
            return t;
        });

    private void Touch(TaskItem t)
    {
        t.Version++;
        t.Updated = _now();
    }

    private static void ResetRollover(TaskItem t)
    {
        t.RolloverCount = 0;
        t.RolloverPaused = false;
        t.OriginalTarget = null;
    }

    private static TaskItem Find(TaskData d, string id) =>
        d.Tasks.FirstOrDefault(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? throw new TaskRuleException($"No task with id {id}.");

    private TaskEntry Log(TaskItem t, Actor actor, EntryKind kind, string text)
    {
        var e = NewEntry(actor, kind, text);
        t.History.Add(e);
        return e;
    }

    private TaskEntry NewEntry(Actor actor, EntryKind kind, string text) =>
        new() { Id = "e" + Guid.NewGuid().ToString("N")[..8], At = _now(), Author = actor, Kind = kind, Text = text };

    private static string NewTaskId(TaskData d)
    {
        var n = d.Tasks.Count + 1;
        string id;
        do id = $"T{n++}"; while (d.Tasks.Any(t => t.Id == id));
        return id;
    }

    private static string Who(Actor a) => a switch { Actor.User => "you", Actor.Assistant => "the assistant", _ => "SideCar" };
}
