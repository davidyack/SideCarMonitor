using System.Text.Json;
using SideCarMonitor.Models;

namespace SideCarMonitor.Services;

/// <summary>A weather alert for the Activity feed (and, when <see cref="Popup"/>, the kiosk popup and voice).</summary>
/// <param name="Kind">Rule that fired: gust, drop, colder, precip-start, precip-soon, wind-soon, lightning.</param>
/// <param name="Title">Short headline, e.g. "Gusts 27 mph".</param>
/// <param name="Detail">One line of context, e.g. "from NW, avg 12 mph; forecast had gusts of 9 mph".</param>
/// <param name="Spoken">What to say out loud.</param>
/// <param name="Glyph">Segoe Fluent icon.</param>
/// <param name="Serious">Well past the threshold (e.g. gusts twice the setting, lightning within 5 miles).</param>
public sealed record WeatherAlert(string Kind, string Title, string Detail, string Spoken, string Glyph, bool Serious);

/// <summary>
/// Watches the live Tempest readings and the forecast and raises alerts for big or unexpected changes:
/// gusts over the threshold, a sudden temperature drop, much colder than forecast, rain/snow starting or about to,
/// strong wind coming, lightning nearby. Each kind has a cooldown so it doesn't nag; a markedly worse reading
/// (stronger gust, closer lightning) alerts again. Thresholds come from <c>weather.alerts</c> in settings (in °F, mph
/// and miles). Cooldowns are saved so a restart doesn't repeat the last alerts. Call from one thread (the UI thread).
/// </summary>
public sealed class WeatherAlerts(WeatherAlertSettings settings, WeatherFormat wx)
{
    private const double MsPerMph = 0.44704, KmPerMile = 1.609344, CPerF = 5.0 / 9;

    private static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SideCarMonitor", "weather-alerts.json");

    /// <summary>What was alerted last, per kind (and the reading it was for), so cooldowns survive restarts.</summary>
    private sealed class Fired
    {
        public DateTime At { get; set; }
        public double Value { get; set; }
        public string Key { get; set; } = "";
    }

    private readonly Dictionary<string, Fired> _fired = LoadState();
    private readonly List<(DateTime At, double TempC)> _temps = [];
    private DateTime? _lastObs;
    private DateTime? _lastStrike;
    private DateTime _lastWet = DateTime.MinValue;   // last minute with precipitation seen
    private bool _wet;

    /// <summary>Check the live readings. Call after every Tempest update; cheap when nothing changed.</summary>
    public List<WeatherAlert> Observe(TempestState s, WeatherOutlook? outlook, DateTime now)
    {
        var alerts = new List<WeatherAlert>();
        if (!settings.Enabled) return alerts;
        var fresh = s.LastObservation is DateTime obs && obs != _lastObs && now - obs < TimeSpan.FromMinutes(5);
        if (fresh)
        {
            _lastObs = s.LastObservation;
            if (s.TemperatureC is double t)
            {
                _temps.Add((now, t));
                _temps.RemoveAll(x => now - x.At > TimeSpan.FromMinutes(settings.TempDropMinutes + 5));
            }
            Add(alerts, CheckGust(s, outlook, now));
            Add(alerts, CheckDrop(s, now));
            Add(alerts, CheckColderThanForecast(s, outlook, now));
        }
        Add(alerts, CheckPrecipStarted(s, outlook, now));
        Add(alerts, CheckLightning(s, now));
        if (alerts.Count > 0) SaveState();
        return alerts;
    }

    /// <summary>Check the forecast for things about to happen. Call when the forecast updates and every few minutes.</summary>
    public List<WeatherAlert> Look(TempestState? s, WeatherOutlook? outlook, DateTime now)
    {
        var alerts = new List<WeatherAlert>();
        if (!settings.Enabled || outlook is null || now - outlook.FetchedAt > TimeSpan.FromHours(3)) return alerts;
        Add(alerts, CheckPrecipSoon(s, outlook, now));
        Add(alerts, CheckWindSoon(s, outlook, now));
        if (alerts.Count > 0) SaveState();
        return alerts;
    }

    // ---- Rules ---------------------------------------------------------------------------------------

    private WeatherAlert? CheckGust(TempestState s, WeatherOutlook? outlook, DateTime now)
    {
        if (settings.GustMph <= 0 || s.WindGustMs is not double gust) return null;
        var mph = gust / MsPerMph;
        if (mph < settings.GustMph) return null;
        // Again within the cooldown only if it's 10 mph stronger than the last alert.
        if (!Allowed("gust", now, TimeSpan.FromHours(settings.CooldownHours), value: mph, worseBy: 10)) return null;
        Fire("gust", now, mph);
        var expected = ForecastAt(outlook, now)?.WindGustMs;
        var detail = $"from {WeatherFormat.Direction(s.WindDirDeg)}, average {wx.Speed(s.WindAvgMs)}" +
                     (expected is double e ? $"; forecast had gusts of {wx.Speed(e)}" : "");
        var serious = mph >= settings.GustMph * 2;
        return new("gust", $"Gusts {wx.Speed(gust)}", detail, $"Heads up: wind gusts of {mph:0} miles an hour.", "", serious);
    }

    private WeatherAlert? CheckDrop(TempestState s, DateTime now)
    {
        if (settings.TempDropF <= 0 || s.TemperatureC is not double t || _temps.Count < 2) return null;
        var window = _temps.Where(x => now - x.At <= TimeSpan.FromMinutes(settings.TempDropMinutes)).ToList();
        if (window.Count < 2) return null;
        var peak = window.MaxBy(x => x.TempC);
        var dropF = (peak.TempC - t) / CPerF;
        if (dropF < settings.TempDropF) return null;
        if (!Allowed("drop", now, TimeSpan.FromHours(settings.CooldownHours), value: dropF, worseBy: settings.TempDropF)) return null;
        Fire("drop", now, dropF);
        var minutes = Math.Max(1, (int)(now - peak.At).TotalMinutes);
        return new("drop", $"Temperature dropped {dropF:0}° in {minutes} min",
            $"now {wx.Temp(t)}, was {wx.Temp(peak.TempC)} at {peak.At:h:mm tt}",
            $"Heads up: the temperature has dropped {dropF:0} degrees in {minutes} minutes, to {wx.Temp(t).TrimEnd('°')} degrees.",
            "", dropF >= settings.TempDropF * 2);
    }

    private WeatherAlert? CheckColderThanForecast(TempestState s, WeatherOutlook? outlook, DateTime now)
    {
        if (settings.ColderThanForecastF <= 0 || s.TemperatureC is not double t) return null;
        if (ForecastTempAt(outlook, now) is not double expected) return null;
        var offF = (expected - t) / CPerF;
        if (offF < settings.ColderThanForecastF) return null;
        if (!Allowed("colder", now, TimeSpan.FromHours(Math.Max(6, settings.CooldownHours)))) return null;
        Fire("colder", now, offF);
        return new("colder", $"{offF:0}° colder than forecast", $"now {wx.Temp(t)}, forecast {wx.Temp(expected)} for this hour",
            $"Heads up: it's {offF:0} degrees colder than forecast, {wx.Temp(t).TrimEnd('°')} degrees right now.",
            "", false);
    }

    private WeatherAlert? CheckPrecipStarted(TempestState s, WeatherOutlook? outlook, DateTime now)
    {
        var wetNow = s.PrecipType > 0 || (s.LastRainStart is DateTime rs && now - rs < TimeSpan.FromMinutes(5));
        if (!wetNow) { _wet = false; return null; }
        var dryBefore = !_wet && now - _lastWet > TimeSpan.FromMinutes(settings.DryMinutesBeforeStart);
        _wet = true;
        _lastWet = now;
        if (!settings.PrecipStarted || !dryBefore) return null;
        if (!Allowed("precip-start", now, TimeSpan.FromMinutes(settings.DryMinutesBeforeStart))) return null;
        Fire("precip-start", now, 0);
        // The Tempest's rain sensor feels drops; it can't tell snow, so near freezing say "rain or snow".
        var cold = s.TemperatureC is double t && t <= 1.5;
        var forecastType = ForecastAt(outlook, now)?.PrecipType;
        var what = s.PrecipType switch
        {
            2 or 3 => "Hail",
            _ when cold => forecastType is "snow" or "sleet" ? "Snow or sleet" : "Rain or snow",
            _ => "Rain",
        };
        return new("precip-start", $"{what} started", $"{wx.Temp(s.TemperatureC)} outside", $"Heads up: {what.ToLowerInvariant()} has started.",
            "", what == "Hail");
    }

    private WeatherAlert? CheckLightning(TempestState s, DateTime now)
    {
        if (settings.LightningMiles <= 0 || s.LastStrike is not DateTime at || at == _lastStrike) return null;
        _lastStrike = at;
        if (now - at > TimeSpan.FromMinutes(5) || s.LastStrikeKm is not double km) return null;
        var miles = km / KmPerMile;
        if (miles > settings.LightningMiles) return null;
        // Again within the hour only if it's 3 miles closer than the last alert.
        if (!Allowed("lightning", now, TimeSpan.FromHours(1), value: -miles, worseBy: 3)) return null;
        Fire("lightning", now, -miles);
        return new("lightning", $"Lightning {wx.Distance(km)} away", s.StrikesToday == 1 ? "first strike today" : $"{s.StrikesToday} strikes today",
            $"Heads up: lightning about {miles:0} miles away.", "", miles <= 5);
    }

    private WeatherAlert? CheckPrecipSoon(TempestState? s, WeatherOutlook o, DateTime now)
    {
        if (!settings.PrecipComing || settings.PrecipChancePercent <= 0) return null;
        if (s is not null && (s.PrecipType > 0 || now - _lastWet < TimeSpan.FromMinutes(30))) return null; // already wet
        var thisHour = now.Date.AddHours(now.Hour);
        var current = o.Hourly.FirstOrDefault(h => h.Time == thisHour);
        if (current is not null && current.PrecipProbability >= settings.PrecipChancePercent) return null; // nothing new to say
        var soon = o.Hourly.FirstOrDefault(h => h.Time > thisHour && h.Time <= thisHour.AddHours(settings.PrecipComingHours)
                                              && h.PrecipProbability >= settings.PrecipChancePercent);
        if (soon is null) return null;
        var key = $"{soon.PrecipType}@{soon.Time:yyyyMMddHH}";
        if (!Allowed("precip-soon", now, TimeSpan.FromHours(6), key: key)) return null;
        Fire("precip-soon", now, soon.PrecipProbability, key);
        var what = string.IsNullOrEmpty(soon.PrecipType) ? "Precipitation" : char.ToUpperInvariant(soon.PrecipType[0]) + soon.PrecipType[1..];
        var amount = o.Hourly.Where(h => h.Time >= soon.Time && h.Time < soon.Time.AddHours(6)).Sum(h => h.PrecipMm ?? 0);
        return new("precip-soon", $"{what} likely around {soon.Time:h tt}",
            $"{soon.PrecipProbability}% chance, {wx.Temp(soon.TempC)}" + (amount > 0 ? $", about {wx.Rain(amount)} over the next 6 hours" : ""),
            $"Heads up: {what.ToLowerInvariant()} is likely around {soon.Time:h tt}.", "", false);
    }

    private WeatherAlert? CheckWindSoon(TempestState? s, WeatherOutlook o, DateTime now)
    {
        if (settings.GustMph <= 0 || settings.WindComingHours <= 0) return null;
        if (s?.WindGustMs is double g && g / MsPerMph >= settings.GustMph) return null; // already windy: the gust alert covers it
        var thisHour = now.Date.AddHours(now.Hour);
        var soon = o.Hourly.Where(h => h.Time > thisHour && h.Time <= thisHour.AddHours(settings.WindComingHours))
                           .Where(h => h.WindGustMs is double gust && gust / MsPerMph >= settings.GustMph)
                           .ToList();
        if (soon.Count == 0) return null;
        var first = soon[0];
        var peak = soon.MaxBy(h => h.WindGustMs)!;
        if (!Allowed("wind-soon", now, TimeSpan.FromHours(12), key: $"{first.Time:yyyyMMddHH}")) return null;
        Fire("wind-soon", now, peak.WindGustMs!.Value / MsPerMph, $"{first.Time:yyyyMMddHH}");
        return new("wind-soon", $"Wind picking up around {first.Time:h tt}",
            $"gusts to {wx.Speed(peak.WindGustMs)} around {peak.Time:ddd h tt}, from {WeatherFormat.Direction(peak.WindDirDeg)}",
            $"Heads up: wind gusts up to {peak.WindGustMs!.Value / MsPerMph:0} miles an hour are expected from about {first.Time:h tt}.",
            "", peak.WindGustMs!.Value / MsPerMph >= settings.GustMph * 2);
    }

    // ---- Helpers -------------------------------------------------------------------------------------

    private static HourForecast? ForecastAt(WeatherOutlook? o, DateTime now)
    {
        if (o is null) return null;
        var thisHour = now.Date.AddHours(now.Hour);
        return o.Hourly.FirstOrDefault(h => h.Time == thisHour);
    }

    /// <summary>Forecast temperature for this minute, interpolated between the hourly values.</summary>
    private static double? ForecastTempAt(WeatherOutlook? o, DateTime now)
    {
        if (o is null) return null;
        var thisHour = now.Date.AddHours(now.Hour);
        var a = o.Hourly.FirstOrDefault(h => h.Time == thisHour)?.TempC;
        var b = o.Hourly.FirstOrDefault(h => h.Time == thisHour.AddHours(1))?.TempC;
        if (a is not double x) return null;
        return b is double y ? x + (y - x) * now.Minute / 60.0 : x;
    }

    private bool Allowed(string kind, DateTime now, TimeSpan cooldown, double value = 0, double worseBy = double.NaN, string key = "")
    {
        if (!_fired.TryGetValue(kind, out var last)) return true;
        if (key.Length > 0 && last.Key == key) return false; // already told about this one
        if (now - last.At >= cooldown) return true;
        return !double.IsNaN(worseBy) && value >= last.Value + worseBy;
    }

    private void Fire(string kind, DateTime now, double value, string key = "") =>
        _fired[kind] = new Fired { At = now, Value = value, Key = key };

    private static void Add(List<WeatherAlert> list, WeatherAlert? a) { if (a is not null) list.Add(a); }

    private static Dictionary<string, Fired> LoadState()
    {
        try
        {
            return File.Exists(StatePath) ? JsonSerializer.Deserialize<Dictionary<string, Fired>>(File.ReadAllText(StatePath)) ?? [] : [];
        }
        catch (Exception) { return []; }
    }

    private void SaveState()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(_fired));
        }
        catch (Exception) { }
    }
}
