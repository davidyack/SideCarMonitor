using System.Text.Json.Serialization;

namespace SideCarTasks;

[JsonConverter(typeof(JsonStringEnumConverter<Priority>))]
public enum Priority { High, Medium, Low }

[JsonConverter(typeof(JsonStringEnumConverter<TodoStatus>))]
public enum TodoStatus { Open, Done }

/// <summary>Flexible = a target the assistant may roll forward; Hard = a real deadline that never moves on its own.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DateKind>))]
public enum DateKind { Flexible, Hard }

/// <summary>Who made a change. App code authorizes by actor; only the user can complete tasks.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Actor>))]
public enum Actor { User, Assistant, System }

[JsonConverter(typeof(JsonStringEnumConverter<EntryKind>))]
public enum EntryKind { Note, Activity, Question, Answer }

public sealed class TaskItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Details { get; set; }

    /// <summary>The item this to-do is about (e.g. the LinkedIn or X post tagged in a briefing); shown as "Open item".</summary>
    public string? Link { get; set; }

    /// <summary>What the link opens, e.g. "X post" or "LinkedIn post".</summary>
    public string? LinkLabel { get; set; }

    public Priority Priority { get; set; } = Priority.Medium;
    public TodoStatus Status { get; set; } = TodoStatus.Open;
    public DateOnly? Date { get; set; }
    public DateKind DateType { get; set; } = DateKind.Flexible;

    /// <summary>The hard deadline this task missed (it rolls forward daily like the rest, flagged in red).</summary>
    public DateOnly? MissedDeadline { get; set; }

    /// <summary>Your order within its day (set by dragging in the weekly plan); lower is first, null sorts by priority after ranked ones.</summary>
    public double? DayRank { get; set; }

    /// <summary>"Put it on Today" pin. Not a date change; stays until cleared or the task is done.</summary>
    public bool PinnedToday { get; set; }

    /// <summary>"Let assistant help": background review and automatic rollover only happen when on.</summary>
    public bool AssistantHelp { get; set; } = true;

    // Rollover bookkeeping
    public int RolloverCount { get; set; }
    public DateOnly? OriginalTarget { get; set; }
    public bool RolloverPaused { get; set; }
    public DateOnly? LastRolledOn { get; set; }

    /// <summary>Incremented on every saved change; used for optimistic concurrency and undo safety.</summary>
    public int Version { get; set; } = 1;
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset Updated { get; set; }
    public DateTimeOffset? Completed { get; set; }

    /// <summary>Notes, activity (incl. rollovers), assistant questions and your answers, oldest first.</summary>
    public List<TaskEntry> History { get; set; } = [];

    public Deliverable Deliverable { get; set; } = new();

    /// <summary>Id of the assistant's open question; the assistant waits until it's answered or context changes.</summary>
    public string? PendingQuestionId { get; set; }

    /// <summary>Fingerprint of the user-authored content at the assistant's last review ("nothing new" detection).</summary>
    public string? LastReviewFingerprint { get; set; }
    public DateTimeOffset? LastReviewedAt { get; set; }
}

public sealed class TaskEntry
{
    public string Id { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public Actor Author { get; set; }
    public EntryKind Kind { get; set; }
    public string Text { get; set; } = "";
    /// <summary>Set for automatic rollover activity entries (enables Undo).</summary>
    public RolloverRecord? Rollover { get; set; }
}

public sealed class RolloverRecord
{
    public DateOnly From { get; set; }
    public DateOnly To { get; set; }
    /// <summary>The task was a hard deadline before this roll (Undo makes it one again).</summary>
    public bool WasHard { get; set; }
    /// <summary>Task version right after the move. Undo only works if nothing changed since.</summary>
    public int VersionAfter { get; set; }
    public bool Undone { get; set; }
}

/// <summary>One versioned work product per task (draft, comparison, plan).</summary>
public sealed class Deliverable
{
    public List<DeliverableVersion> Versions { get; set; } = [];
    /// <summary>Assistant revision held back because you had edited the deliverable; Accept or Dismiss.</summary>
    public DeliverableVersion? PendingRevision { get; set; }

    [JsonIgnore] public DeliverableVersion? Current => Versions.Count > 0 ? Versions[^1] : null;
}

public sealed class DeliverableVersion
{
    public int Number { get; set; }
    public Actor Author { get; set; }
    public DateTimeOffset At { get; set; }
    public string Content { get; set; } = "";
}

public sealed class BriefEntry
{
    public DateOnly Date { get; set; }
    public DateTimeOffset At { get; set; }
    public string Text { get; set; } = "";
    public bool Dismissed { get; set; }
}

/// <summary>Everything persisted for the to-do list.</summary>
public sealed class TaskData
{
    public int Schema { get; set; } = 1;
    /// <summary>Saved timezone (Windows id) for "today", rollover and the daily brief.</summary>
    public string? TimeZoneId { get; set; }
    public List<TaskItem> Tasks { get; set; } = [];
    /// <summary>Job name → local date it last completed (makes daily jobs idempotent).</summary>
    public Dictionary<string, DateOnly> JobRuns { get; set; } = [];
    public List<BriefEntry> Briefs { get; set; } = [];
    public DateTimeOffset? LastBriefAt { get; set; }
}
