using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SideCarMonitor.Services;

public sealed class Reminder
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime Due { get; set; }
    public DateTime Created { get; set; }
    /// <summary>pending, done, todo (turned into a to-do), cancelled.</summary>
    public string Status { get; set; } = "pending";
    public int Snoozes { get; set; }
    /// <summary>"" (once), "daily", "weekdays", "weekly:tue,thu", or "monthly" (same day of the month).</summary>
    public string Repeat { get; set; } = "";
    /// <summary>The time of day it repeats at (the first due time's clock time).</summary>
    public TimeSpan At { get; set; }

    public bool Repeats => Repeat.Length > 0;
    public string RepeatText => Repeat switch
    {
        "" => "",
        "daily" => "every day",
        "weekdays" => "every weekday",
        "monthly" => $"every month on the {Due.Day}",
        _ when Repeat.StartsWith("weekly:") => "every " + string.Join(", ", Repeat[7..].Split(',').Select(d => d.Length > 0 ? char.ToUpper(d[0]) + d[1..] : d)),
        _ => Repeat,
    };

    /// <summary>Next due time after <paramref name="after"/> for a repeating reminder.</summary>
    public DateTime Next(DateTime after)
    {
        var day = after.Date;
        for (var i = 0; i < 400; i++)
        {
            day = day.AddDays(1);
            var ok = Repeat switch
            {
                "daily" => true,
                "weekdays" => day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday),
                "monthly" => day.Day == Math.Min(Due.Day, DateTime.DaysInMonth(day.Year, day.Month)),
                _ when Repeat.StartsWith("weekly:") => Repeat[7..].Split(',').Contains(day.DayOfWeek.ToString()[..3].ToLowerInvariant()),
                _ => false,
            };
            if (ok) return day + At;
        }
        return after.AddDays(7);
    }
    public DateTime? HandledAt { get; set; }

    public bool IsDue(DateTime now) => Status == "pending" && Due <= now;
}

/// <summary>
/// "Remind me to X in an hour / tomorrow" reminders that keep bugging you until you say Done, snooze them, or turn
/// them into a to-do. The kiosk shows due reminders (again every 10 minutes while ignored); the phone gets
/// notifications through the gateway. Only your own requests create reminders (the agent's reminder_add is refused
/// for background turns). Data: %APPDATA%\SideCarMonitor\reminders.json.
/// </summary>
public sealed class ReminderService(SideCarTasks.TaskService? tasks)
{
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SideCarMonitor", "reminders.json");
    private readonly object _lock = new();
    private readonly List<Reminder> _all = Load();

    /// <summary>Raised (any thread) when reminders change.</summary>
    public event Action? Changed;

    private static List<Reminder> Load()
    {
        try { return File.Exists(StorePath) ? JsonSerializer.Deserialize<List<Reminder>>(File.ReadAllText(StorePath)) ?? [] : []; }
        catch { return []; }
    }

    private void Save()
    {
        lock (_lock)
        {
            _all.RemoveAll(r => r.Status != "pending" && r.HandledAt < DateTime.Now.AddDays(-30));
            var tmp = StorePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_all, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, StorePath, overwrite: true);
        }
        Changed?.Invoke();
    }

    public List<Reminder> Pending { get { lock (_lock) return _all.Where(r => r.Status == "pending").OrderBy(r => r.Due).ToList(); } }
    public List<Reminder> Due(DateTime now) => Pending.Where(r => r.IsDue(now)).ToList();
    public Reminder? Find(string id) { lock (_lock) return _all.FirstOrDefault(r => r.Id == id); }

    public Reminder Add(string text, DateTime due, string repeat = "")
    {
        var r = new Reminder { Id = "R" + Guid.NewGuid().ToString("N")[..6], Text = text.Trim(), Due = due, Created = DateTime.Now, Repeat = repeat, At = due.TimeOfDay };
        lock (_lock) _all.Add(r);
        Save();
        return r;
    }

    /// <summary>done | snooze (minutes, or until tomorrow 9 AM when minutes is -1) | todo | cancel.</summary>
    public string? Act(string id, string action, int minutes = 10)
    {
        var r = Find(id);
        if (r is null) return "No such reminder";
        switch (action)
        {
            case "done":
                if (r.Repeats) { r.Due = r.Next(DateTime.Now > r.Due ? DateTime.Now : r.Due); r.Snoozes = 0; }
                else { r.Status = "done"; r.HandledAt = DateTime.Now; }
                break;
            case "cancel": r.Status = "cancelled"; r.HandledAt = DateTime.Now; break;
            case "snooze":
                r.Due = minutes < 0 ? DateTime.Today.AddDays(1).AddHours(9) : DateTime.Now.AddMinutes(Math.Clamp(minutes, 1, 60 * 24 * 7));
                r.Snoozes++;
                break;
            case "todo":
                if (tasks is null) return "The to-do list isn't available";
                tasks.Add(SideCarTasks.Actor.User, r.Text, details: $"From a reminder set {r.Created:ddd MMM d h:mm tt}",
                          date: DateOnly.FromDateTime(DateTime.Today), pinToday: true);
                if (r.Repeats) { r.Due = r.Next(DateTime.Now > r.Due ? DateTime.Now : r.Due); r.Snoozes = 0; }
                else { r.Status = "todo"; r.HandledAt = DateTime.Now; }
                break;
            default: return $"Unknown action {action}";
        }
        Save();
        return null;
    }

    // ---- Agent tools: reminder_add, reminder_list, reminder_done ----

    public object Handle(AgentHost.ToolCall call, string? userText)
    {
        var a = call.Args;
        switch (call.Name)
        {
            case "reminder_add":
            {
                if (userText is null) return new { error = "Reminders can only be set when the user asks in a conversation." };
                var text = a?["text"]?.GetValue<string>()?.Trim() ?? "";
                var whenText = a?["when"]?.GetValue<string>() ?? "";
                if (text.Length == 0) return new { error = "What should the reminder say?" };
                if (!DateTime.TryParse(whenText, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var when))
                    return new { error = $"Couldn't read the time '{whenText}'. Use local time like 2026-11-24 15:30.", now = DateTime.Now.ToString("dddd yyyy-MM-dd HH:mm") };
                if (when < DateTime.Now.AddMinutes(-1)) return new { error = "That time is in the past.", now = DateTime.Now.ToString("dddd yyyy-MM-dd HH:mm") };
                var repeat = NormalizeRepeat(a?["repeat"]?.GetValue<string>());
                if (repeat is null) return new { error = "repeat must be empty, daily, weekdays, monthly, or weekly:mon,tue,... (three-letter days)." };
                var r = Add(text, when, repeat);
                SystemEvents.Report("Reminder set", $"{r.Text} \u00B7 {r.Due:ddd h:mm tt}" + (r.Repeats ? $", {r.RepeatText}" : ""));
                return new { set = true, id = r.Id, text = r.Text, due = r.Due.ToString("dddd MMM d, h:mm tt"), repeats = r.RepeatText };
            }
            case "reminder_list":
                return new
                {
                    now = DateTime.Now.ToString("dddd yyyy-MM-dd HH:mm"),
                    reminders = Pending.Select(r => new { r.Id, r.Text, due = r.Due.ToString("ddd MMM d h:mm tt"), overdue = r.Due <= DateTime.Now, repeats = r.RepeatText }),
                };
            case "reminder_done":
            {
                if (userText is null) return new { error = "Only the user can close reminders." };
                var action = a?["action"]?.GetValue<string>() ?? "done";
                if (action is not ("done" or "cancel" or "todo")) action = "done";
                var error = Act(a?["id"]?.GetValue<string>() ?? "", action);
                return error is null ? new { ok = true } : new { error };
            }
            default:
                return new { error = $"Unknown reminder tool '{call.Name}'." };
        }
    }

    /// <summary>"" for one-time; null if not understood.</summary>
    private static string? NormalizeRepeat(string? s)
    {
        s = (s ?? "").Trim().ToLowerInvariant();
        if (s is "" or "none" or "once") return "";
        if (s is "daily" or "weekdays" or "monthly") return s;
        if (s.StartsWith("weekly:"))
        {
            string[] valid = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];
            var days = s[7..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(d => d.Length >= 3 ? d[..3] : d).ToList();
            return days.Count > 0 && days.All(valid.Contains) ? "weekly:" + string.Join(",", days.Distinct()) : null;
        }
        return null;
    }

    public static JsonObject ToJson(Reminder r) => new()
    {
        ["id"] = r.Id, ["text"] = r.Text, ["due"] = r.Due.ToString("o"), ["dueText"] = r.Due.ToString("ddd h:mm tt"),
        ["overdue"] = r.Due <= DateTime.Now, ["snoozes"] = r.Snoozes, ["repeats"] = r.RepeatText,
    };
}
