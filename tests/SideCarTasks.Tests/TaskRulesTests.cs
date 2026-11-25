using SideCarTasks;
using Xunit;

namespace SideCarTasks.Tests;

/// <summary>Acceptance tests for the to-do list rules (rollover, authorization, deliverables, concurrency).</summary>
public sealed class TaskRulesTests : IDisposable
{
    private const string Zone = "Mountain Standard Time";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sidecar-tasks-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _clock = new(2026, 11, 23, 9, 0, 0, TimeSpan.FromHours(-7)); // Mon Nov 23, 9 AM local
    private readonly TaskService _svc;
    private readonly TaskJobs _jobs;

    public TaskRulesTests()
    {
        var store = new TaskStore(Path.Combine(_dir, "tasks.json"));
        _svc = new TaskService(store, () => _clock, Zone);
        _jobs = new TaskJobs(_svc, () => _clock);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private DateOnly Today => _svc.Today;
    private void NextDays(int n) => _clock = _clock.AddDays(n);

    [Fact]
    public void Dropping_into_a_day_moves_it_there_as_a_flexible_target_in_the_order_given()
    {
        var a = _svc.Add(Actor.User, "A", priority: Priority.High, date: Today.AddDays(1));
        var b = _svc.Add(Actor.User, "B", priority: Priority.Low, date: Today.AddDays(1));
        var c = _svc.Add(Actor.User, "C", priority: Priority.Medium, date: Today.AddDays(3));

        _svc.OrderDay(Actor.User, Today.AddDays(1), [b.Id, c.Id, a.Id]); // C dragged in between, B on top

        var day = TaskService.InDayOrder(_svc.All().Where(t => t.Date == Today.AddDays(1))).Select(t => t.Title).ToList();
        Assert.Equal(["B", "C", "A"], day);
        var moved = _svc.Get(c.Id)!;
        Assert.Equal(DateKind.Flexible, moved.DateType);
        Assert.Contains(moved.History, e => e.Text.Contains("Moved by you"));
    }

    [Fact]
    public void Hard_deadlines_reorder_within_their_day_but_do_not_move_and_the_assistant_cannot_reorder()
    {
        var hard = _svc.Add(Actor.User, "Taxes", date: Today.AddDays(2), dateType: DateKind.Hard);
        var other = _svc.Add(Actor.User, "Other", date: Today.AddDays(2));

        _svc.OrderDay(Actor.User, Today.AddDays(2), [other.Id, hard.Id]); // same day: fine
        Assert.Equal(1, _svc.Get(hard.Id)!.DayRank);
        Assert.Throws<TaskRuleException>(() => _svc.OrderDay(Actor.User, Today.AddDays(4), [hard.Id]));
        Assert.Throws<TaskRuleException>(() => _svc.OrderDay(Actor.Assistant, Today.AddDays(2), [hard.Id, other.Id]));
        Assert.Equal(Today.AddDays(2), _svc.Get(hard.Id)!.Date);
    }

    [Fact]
    public void Unfinished_todos_roll_to_today_once_after_todays_own_in_their_old_order()
    {
        var todays = _svc.Add(Actor.User, "Already today", date: Today);
        _svc.OrderDay(Actor.User, Today, [todays.Id]);
        var a = _svc.Add(Actor.User, "Renew passport", priority: Priority.Low, date: Today.AddDays(-1));
        var b = _svc.Add(Actor.User, "Older one", date: Today.AddDays(-3));

        _jobs.RunDue();
        _jobs.RunDue();          // same day again: no second move
        _svc.RunRollover();      // even calling rollover directly again

        var after = _svc.Get(a.Id)!;
        Assert.Equal(Today, after.Date);
        Assert.Equal(1, after.RolloverCount);
        Assert.Equal(Today.AddDays(-1), after.OriginalTarget);
        var moves = after.History.Where(e => e.Rollover is not null).ToList();
        Assert.Single(moves);
        Assert.Contains("Rolled from", moves[0].Text);
        Assert.Equal(Today, moves[0].Rollover!.To);
        var order = TaskService.InDayOrder(_svc.All().Where(t => t.Date == Today)).Select(t => t.Title).ToList();
        Assert.Equal(["Already today", "Older one", "Renew passport"], order); // oldest first, after today's own
        Assert.Equal(3, _svc.DaysRolled(_svc.Get(b.Id)!));
    }

    [Fact]
    public void Missed_hard_deadline_rolls_to_today_flagged_while_undated_today_and_done_stay()
    {
        var hard = _svc.Add(Actor.User, "File taxes", date: Today.AddDays(-1), dateType: DateKind.Hard);
        var today = _svc.Add(Actor.User, "Call plumber", date: Today);
        var undated = _svc.Add(Actor.User, "Someday");
        var done = _svc.Add(Actor.User, "Old", date: Today.AddDays(-2));
        _svc.MarkDone(Actor.User, done.Id);

        _jobs.RunDue();

        var h = _svc.Get(hard.Id)!;
        Assert.Equal(Today, h.Date);
        Assert.Equal(Today.AddDays(-1), h.MissedDeadline);
        Assert.Equal(DateKind.Flexible, h.DateType);
        Assert.Equal(Today, _svc.Get(today.Id)!.Date);
        Assert.Null(_svc.Get(undated.Id)!.Date);
        Assert.Equal(Today.AddDays(-2), _svc.Get(done.Id)!.Date);
        Assert.Contains(_svc.View(TaskView.Today), t => t.Id == hard.Id);

        // Undo puts the hard deadline back exactly as it was.
        var move = h.History.Single(e => e.Rollover is not null);
        _svc.UndoRollover(Actor.User, hard.Id, move.Id);
        var undone = _svc.Get(hard.Id)!;
        Assert.Equal(DateKind.Hard, undone.DateType);
        Assert.Null(undone.MissedDeadline);
        Assert.Equal(Today.AddDays(-1), undone.Date);
    }

    [Fact]
    public void After_a_week_of_rolling_it_keeps_rolling_but_asks_you_to_decide()
    {
        var t = _svc.Add(Actor.User, "Clean garage", date: Today.AddDays(-1));
        for (var i = 0; i < 5; i++) { _jobs.RunDue(); NextDays(1); }
        _jobs.RunDue();                                  // rolling 6 days
        Assert.False(_svc.NeedsDecision(_svc.Get(t.Id)!));
        NextDays(1); _jobs.RunDue();                     // 7 days
        var week = _svc.Get(t.Id)!;
        Assert.Equal(Today, week.Date);                  // still on today
        Assert.Equal(7, _svc.DaysRolled(week));
        Assert.True(_svc.NeedsDecision(week));
        Assert.Contains(_svc.View(TaskView.NeedsMe), x => x.Id == t.Id);

        // Pick a date = explicit manual reschedule: starts over.
        _svc.Edit(Actor.User, t.Id, date: Today.AddDays(7));
        var picked = _svc.Get(t.Id)!;
        Assert.Equal(0, picked.RolloverCount);
        Assert.False(_svc.NeedsDecision(picked));
    }

    [Fact]
    public void Old_today_pins_become_dated_today_and_put_on_today_dates_it()
    {
        var undated = _svc.Add(Actor.User, "Read report");
        _svc.Store.Update(Actor.System, d => { d.Tasks.Single(x => x.Id == undated.Id).PinnedToday = true; return true; }); // legacy data
        _jobs.RunDue();
        var migrated = _svc.Get(undated.Id)!;
        Assert.False(migrated.PinnedToday);
        Assert.Equal(Today, migrated.Date);

        var later = _svc.Add(Actor.User, "Later", date: Today.AddDays(5));
        _svc.PinToday(Actor.User, later.Id, true);
        Assert.Equal(Today, _svc.Get(later.Id)!.Date);
        Assert.Equal(Today, _svc.Add(Actor.User, "New for today", pinToday: true).Date);
    }

    [Fact]
    public void Disabling_help_prevents_background_action()
    {
        var t = _svc.Add(Actor.User, "Private thing", date: Today.AddDays(-1));
        _svc.SetAssistantHelp(Actor.User, t.Id, false);

        _jobs.RunDue();

        var after = _svc.Get(t.Id)!;
        Assert.Equal(Today, after.Date); // rolling to today is date upkeep, not assistant work
        Assert.False(_svc.EligibleForReview(after, explicitRequest: false));
        Assert.DoesNotContain(_svc.SweepCandidates(10), x => x.Id == t.Id);
        // You can still explicitly ask for a pass.
        Assert.True(_svc.EligibleForReview(after, explicitRequest: true));
    }

    [Fact]
    public void Assistant_draft_leaves_the_task_open_and_assistant_cannot_mark_done()
    {
        var t = _svc.Add(Actor.User, "Write thank-you note");
        _svc.SaveDeliverable(Actor.Assistant, t.Id, "Dear Sam, thank you…");

        var after = _svc.Get(t.Id)!;
        Assert.Equal(TodoStatus.Open, after.Status);
        Assert.Equal(1, after.Deliverable.Current!.Number);
        Assert.Throws<TaskRuleException>(() => _svc.MarkDone(Actor.Assistant, t.Id));
        Assert.Throws<TaskRuleException>(() => _svc.Edit(Actor.Assistant, t.Id, date: Today));

        _svc.MarkDone(Actor.User, t.Id); // you close it yourself
        Assert.Equal(TodoStatus.Done, _svc.Get(t.Id)!.Status);
    }

    [Fact]
    public void Human_edited_draft_is_protected_and_revision_can_be_accepted_or_dismissed()
    {
        var t = _svc.Add(Actor.User, "Plan trip");
        _svc.SaveDeliverable(Actor.Assistant, t.Id, "Plan v1");
        _svc.SaveDeliverable(Actor.User, t.Id, "Plan v1, edited by me");

        _svc.SaveDeliverable(Actor.Assistant, t.Id, "Plan v3 from assistant", basedOnVersion: 2);

        var after = _svc.Get(t.Id)!;
        Assert.Equal("Plan v1, edited by me", after.Deliverable.Current!.Content);
        Assert.Equal(Actor.User, after.Deliverable.Current.Author);
        Assert.NotNull(after.Deliverable.PendingRevision);
        Assert.Contains(_svc.View(TaskView.NeedsMe), x => x.Id == t.Id);

        _svc.DismissRevision(Actor.User, t.Id);
        Assert.Null(_svc.Get(t.Id)!.Deliverable.PendingRevision);

        _svc.SaveDeliverable(Actor.Assistant, t.Id, "Another idea");
        _svc.AcceptRevision(Actor.User, t.Id);
        var accepted = _svc.Get(t.Id)!;
        Assert.Equal("Another idea", accepted.Deliverable.Current!.Content);
        Assert.Equal(3, accepted.Deliverable.Current.Number);
    }

    [Fact]
    public void Stale_assistant_base_version_goes_to_pending_instead_of_overwriting()
    {
        var t = _svc.Add(Actor.User, "Budget");
        _svc.SaveDeliverable(Actor.Assistant, t.Id, "v1");
        _svc.SaveDeliverable(Actor.Assistant, t.Id, "v2");
        _svc.SaveDeliverable(Actor.Assistant, t.Id, "late write based on v1", basedOnVersion: 1);

        var after = _svc.Get(t.Id)!;
        Assert.Equal("v2", after.Deliverable.Current!.Content);
        Assert.Equal("late write based on v1", after.Deliverable.PendingRevision!.Content);
    }

    [Fact]
    public void Retried_notes_do_not_duplicate()
    {
        var t = _svc.Add(Actor.User, "Taxes");
        _svc.AddNote(Actor.Assistant, t.Id, "Gathered the 1099 checklist.");
        _svc.AddNote(Actor.Assistant, t.Id, "Gathered the 1099 checklist."); // retry

        Assert.Single(_svc.Get(t.Id)!.History, e => e.Kind == EntryKind.Note);
    }

    [Fact]
    public void Concurrent_rollover_runs_never_create_duplicate_moves()
    {
        var t = _svc.Add(Actor.User, "Race", date: Today.AddDays(-1));
        Parallel.For(0, 8, _ => _jobs.RunDue());

        var after = _svc.Get(t.Id)!;
        Assert.Single(after.History, e => e.Rollover is not null);
        Assert.Equal(1, after.RolloverCount);
    }

    [Fact]
    public void Stale_manual_edit_is_rejected_not_clobbered()
    {
        var t = _svc.Add(Actor.User, "Report");
        var opened = _svc.Get(t.Id)!.Version;
        _svc.Edit(Actor.User, t.Id, expectedVersion: opened, details: "first edit");

        Assert.Throws<TaskRuleException>(() => _svc.Edit(Actor.User, t.Id, expectedVersion: opened, details: "second, stale"));
        Assert.Equal("first edit", _svc.Get(t.Id)!.Details);
    }

    [Fact]
    public void Undo_restores_date_pauses_rollover_and_never_overwrites_a_newer_edit()
    {
        var t = _svc.Add(Actor.User, "Gym", date: Today.AddDays(-1));
        _svc.RunRollover();
        var move = _svc.Get(t.Id)!.History.Single(e => e.Rollover is not null);

        _svc.UndoRollover(Actor.User, t.Id, move.Id);
        var undone = _svc.Get(t.Id)!;
        Assert.Equal(Today.AddDays(-1), undone.Date);
        Assert.True(undone.RolloverPaused);
        NextDays(1); _svc.RunRollover();
        Assert.Equal(Today.AddDays(-2), _svc.Get(t.Id)!.Date); // paused: not rolled
        Assert.True(_svc.NeedsDecision(_svc.Get(t.Id)!));

        // A second task: you edit after the move, so Undo is refused.
        var u = _svc.Add(Actor.User, "Dentist", date: Today.AddDays(-1));
        _svc.RunRollover();
        var uMove = _svc.Get(u.Id)!.History.Single(e => e.Rollover is not null);
        _svc.Edit(Actor.User, u.Id, details: "bring insurance card");
        Assert.Throws<TaskRuleException>(() => _svc.UndoRollover(Actor.User, u.Id, uMove.Id));
    }

    [Fact]
    public void Assistant_writes_do_not_change_the_review_fingerprint_but_yours_do()
    {
        var t = _svc.Add(Actor.User, "Research laptops");
        var fp = TaskService.Fingerprint(_svc.Get(t.Id)!);

        _svc.AddNote(Actor.Assistant, t.Id, "Compared three models.");
        _svc.SaveDeliverable(Actor.Assistant, t.Id, "Comparison table…");
        Assert.Equal(fp, TaskService.Fingerprint(_svc.Get(t.Id)!));

        _svc.AddNote(Actor.User, t.Id, "Budget is $1500.");
        Assert.NotEqual(fp, TaskService.Fingerprint(_svc.Get(t.Id)!));
    }

    [Fact]
    public void Question_blocks_background_review_until_you_answer()
    {
        var t = _svc.Add(Actor.User, "Book hotel");
        _svc.Ask(Actor.Assistant, t.Id, "Which dates?");
        Assert.False(_svc.EligibleForReview(_svc.Get(t.Id)!, explicitRequest: false));
        Assert.Contains(_svc.View(TaskView.NeedsMe), x => x.Id == t.Id);

        _svc.AddNote(Actor.User, t.Id, "Dec 4-6");
        var answered = _svc.Get(t.Id)!;
        Assert.Null(answered.PendingQuestionId);
        Assert.Equal(EntryKind.Answer, answered.History.Last().Kind);
        Assert.True(_svc.EligibleForReview(answered, explicitRequest: false));
    }

    [Fact]
    public void Brief_groups_rollovers_and_appears_only_when_something_matters()
    {
        _clock = new DateTimeOffset(2026, 11, 23, 7, 0, 0, TimeSpan.FromHours(-7));
        Assert.Null(_jobs.RunDue().Brief); // nothing at all → no brief

        NextDays(1);
        _svc.Add(Actor.User, "A", date: Today.AddDays(-1));
        _svc.Add(Actor.User, "B", priority: Priority.Low, date: Today.AddDays(-1));
        _svc.Add(Actor.User, "Taxes", date: Today, dateType: DateKind.Hard);
        var report = _jobs.RunDue();

        Assert.Equal(2, report.Moved);
        Assert.NotNull(report.Brief);
        Assert.Contains("Hard deadline today: Taxes", report.Brief!.Text);
        Assert.Contains("Rolled 2 unfinished to-dos to today", report.Brief.Text);
        Assert.Single(report.Brief.Text.Split('\n'), l => l.StartsWith("Rolled")); // grouped, one line
        Assert.Null(_jobs.RunDue().Brief); // once per day
    }
}
