using System.Globalization;
using System.Text.Json.Nodes;

namespace SideCarMonitor.Services;

/// <summary>
/// Answers the agent's calendar tools: calendar_events, calendar_free_time, calendar_add.
/// Reading is free. Adding is guarded here (not just by the prompt): only from your own request in a conversation,
/// at most one appointment per hour, checked by the decision model for odd activity, and logged
/// (%APPDATA%\SideCarMonitor\calendar-actions.log + the Activity feed's System filter).
/// </summary>
public sealed class CalendarTools(CalendarService calendars, EventJudge? decision)
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SideCarMonitor", "calendar-actions.log");
    private DateTime _lastAdd = DateTime.MinValue;

    public async Task<object?> HandleAsync(AgentHost.ToolCall call, string? userText)
    {
        try
        {
            if (calendars.IsStale) await calendars.RefreshAsync();
            var a = call.Args;
            return call.Name switch
            {
                "calendar_events" => Events(Date(a?["from"]) ?? DateTime.Today, Date(a?["to"]) ?? DateTime.Today.AddDays(7)),
                "calendar_free_time" => Free(a),
                "calendar_add" => await AddAsync(a, call.Turn, userText),
                _ => new { error = $"Unknown calendar tool '{call.Name}'." },
            };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    private object Events(DateTime from, DateTime to)
    {
        if (to <= from) to = from.AddDays(1);
        return new
        {
            now = DateTime.Now.ToString("dddd yyyy-MM-dd HH:mm"),
            calendars = calendars.Calendars,
            problems = calendars.Problems,
            events = calendars.Events.Where(e => e.End > from && e.Start < to.Date.AddDays(1)).Select(e => new
            {
                calendar = e.Calendar, subject = e.Subject, allDay = e.AllDay, showAs = e.ShowAs,
                start = e.Start.ToString("ddd yyyy-MM-dd HH:mm"), end = e.End.ToString("HH:mm"),
                location = e.Location, online = e.JoinUrl.Length > 0, organizer = e.Organizer,
            }),
        };
    }

    private object Free(JsonNode? a)
    {
        var minutes = a?["minutes"]?.GetValue<int>() ?? 30;
        var from = Date(a?["from"]) ?? DateTime.Now;
        var to = Date(a?["to"]) ?? from.Date.AddDays(5);
        var startHour = a?["dayStartHour"]?.GetValue<int>() ?? 8;
        var endHour = a?["dayEndHour"]?.GetValue<int>() ?? 17;
        return new
        {
            now = DateTime.Now.ToString("dddd yyyy-MM-dd HH:mm"),
            note = "Free in ALL calendars, weekdays within working hours.",
            slots = calendars.FreeSlots(from < DateTime.Now ? DateTime.Now : from, to, minutes, startHour, endHour)
                .Take(15).Select(s => new { start = s.Start.ToString("ddd yyyy-MM-dd HH:mm"), end = s.End.ToString("HH:mm") }),
        };
    }

    private async Task<object> AddAsync(JsonNode? a, string turn, string? userText)
    {
        var label = a?["calendar"]?.GetValue<string>()?.Trim() ?? "";
        var subject = a?["subject"]?.GetValue<string>()?.Trim() ?? "";
        var start = Date(a?["start"]);
        var end = Date(a?["end"]);
        var what = $"{subject} on {label}, {start:ddd MMM d h:mm tt}-{end:h:mm tt}";
        var entry = new JsonObject { ["at"] = DateTime.Now.ToString("o"), ["turn"] = turn, ["asked"] = userText, ["what"] = what };

        object Refuse(string why)
        {
            entry["result"] = "refused"; entry["why"] = why; Log(entry);
            SystemEvents.Report($"Stopped the agent: add \"{subject}\"", why, alert: false);
            return new { error = why, blocked = true };
        }

        if (userText is null)
            return Refuse("Appointments can only be added when the user asks in a conversation, not from background tasks.");
        var match = calendars.Calendars.FirstOrDefault(c => c.Equals(label, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return new { error = $"Which calendar? Ask the user: {string.Join(" or ", calendars.Calendars)}.", needsCalendar = true };
        if (subject.Length == 0 || start is null || end is null || end <= start)
            return new { error = "Need subject, start and end (local time, e.g. 2026-11-24 14:00)." };
        if (start < DateTime.Now.AddMinutes(-5))
            return new { error = "That time is in the past." };
        if (DateTime.Now - _lastAdd < TimeSpan.FromHours(1))
            return Refuse($"Only one appointment per hour; the last was added at {_lastAdd:h:mm tt}. Try again after {_lastAdd.AddHours(1):h:mm tt}.");

        if (decision is not null)
        {
            var odd = await decision.OddActionAsync(new JsonObject
            {
                ["now"] = DateTime.Now.ToString("dddd h:mm tt"),
                ["user_said"] = userText,
                ["assistant_action"] = $"add calendar appointment: {what}",
                ["recent_home_actions"] = new JsonArray(),
            });
            entry["odd"] = Math.Round(odd, 3);
            if (odd >= 0.6) return Refuse($"This didn't look like what the user asked for (odd score {odd:0.00}); nothing was added.");
        }

        var created = await calendars.AddAsync(match, subject, start.Value, end.Value, a?["location"]?.GetValue<string>(), a?["notes"]?.GetValue<string>());
        _lastAdd = DateTime.Now;
        entry["result"] = "added"; Log(entry);
        SystemEvents.Report($"SideCar added an appointment to {match}", $"{subject}, {start:ddd MMM d h:mm tt}");
        return new { added = true, calendar = match, subject, start = start.Value.ToString("ddd yyyy-MM-dd HH:mm"), end = end.Value.ToString("HH:mm") };
    }

    private static DateTime? Date(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) && DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var d) ? d : null;

    private static void Log(JsonObject entry)
    {
        try { File.AppendAllText(LogPath, entry.ToJsonString() + Environment.NewLine); } catch { }
    }
}
