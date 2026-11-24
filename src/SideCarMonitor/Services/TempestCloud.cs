using System.Text.Json;
using Microsoft.UI.Dispatching;
using SideCarMonitor.Models;

namespace SideCarMonitor.Services;

/// <summary>One forecast hour (metric, local time).</summary>
public sealed record HourForecast(
    DateTime Time, double? TempC, double? FeelsLikeC, string Conditions, string Icon,
    int PrecipProbability, string PrecipType, double? PrecipMm,
    double? WindAvgMs, double? WindGustMs, double? WindDirDeg, double? HumidityPct, double? StationPressureMb, double? Uv);

/// <summary>One forecast day (metric, local time).</summary>
public sealed record DayForecast(
    DateTime Date, double? HighC, double? LowC, string Conditions, string Icon,
    int PrecipProbability, string PrecipType, DateTime? Sunrise, DateTime? Sunset);

/// <summary>One past hour from the station's own readings, summarized (metric, local time).</summary>
public sealed record HourObserved(
    DateTime Hour, int Minutes, double? TempAvgC, double? TempMinC, double? TempMaxC,
    double? WindAvgMs, double? GustMaxMs, double? PressureMb, double? HumidityPct, double RainMm, int Strikes);

/// <summary>Everything fetched from the WeatherFlow cloud: forecast plus the station's recent history.</summary>
public sealed record WeatherOutlook(
    DateTime FetchedAt, int StationId, string StationName,
    IReadOnlyList<HourForecast> Hourly, IReadOnlyList<DayForecast> Daily, IReadOnlyList<HourObserved> PastHours,
    double? RainTodayMm, double? RainYesterdayMm);

/// <summary>
/// The WeatherFlow cloud API (needs <c>weather.tempestToken</c>): ~10 days of hourly and daily forecast for the
/// station's location, and the station's own one-minute readings for the last 24 hours, summarized per hour.
/// The local UDP feed (<see cref="TempestListener"/>) stays the source of live readings; this adds what the hub
/// doesn't broadcast. Refreshes every 30 minutes (5 after a failure) and caches the last result so a restart has
/// data straight away. The token is only ever sent to swd.weatherflow.com and never logged.
/// </summary>
public sealed class TempestCloud : IDisposable
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RetryEvery = TimeSpan.FromMinutes(5);

    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SideCarMonitor", "weather-outlook.json");

    private readonly HttpClient _http = new() { BaseAddress = new Uri("https://swd.weatherflow.com/swd/rest/"), Timeout = TimeSpan.FromSeconds(30) };
    private readonly string _token;
    private readonly int? _stationSetting;
    private readonly DispatcherQueue _ui;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _refreshing = new(1, 1);
    private int _stationId;
    private int _deviceId;
    private string _stationName = "";

    /// <summary>Latest outlook (set on the UI thread), or null until the first fetch or a fresh cache.</summary>
    public WeatherOutlook? Outlook { get; private set; }

    /// <summary>Why the last refresh failed, or null after a success.</summary>
    public string? LastError { get; private set; }

    /// <summary>Raised on the UI thread after Outlook changes.</summary>
    public event Action? Updated;

    private TempestCloud(string token, int? stationId, DispatcherQueue ui)
    {
        _token = token;
        _stationSetting = stationId;
        _ui = ui;
    }

    /// <summary>Null when no token is configured (the kiosk then runs on the local feed only).</summary>
    public static TempestCloud? Create(WeatherSettings settings, DispatcherQueue ui) =>
        string.IsNullOrWhiteSpace(settings.TempestToken) ? null : new TempestCloud(settings.TempestToken.Trim(), settings.TempestStationId, ui);

    public void Start()
    {
        LoadCache();
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    /// <summary>Fetch now instead of waiting for the next scheduled refresh.</summary>
    public Task RefreshNowAsync() => RefreshAsync(_cts.Token);

    private async Task LoopAsync(CancellationToken ct)
    {
        // A cache younger than the refresh interval counts as this round.
        if (Outlook is { } cached && DateTime.Now - cached.FetchedAt < RefreshEvery)
        {
            try { await Task.Delay(RefreshEvery - (DateTime.Now - cached.FetchedAt), ct); } catch (OperationCanceledException) { return; }
        }
        while (!ct.IsCancellationRequested)
        {
            var ok = await RefreshAsync(ct).ContinueWith(t => t.IsCompletedSuccessfully && LastError is null, TaskScheduler.Default);
            try { await Task.Delay(ok ? RefreshEvery : RetryEvery, ct); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        if (!await _refreshing.WaitAsync(0, ct)) return; // one at a time
        try
        {
            if (_deviceId == 0) await FindStationAsync(ct);
            var (hourly, daily, rainToday, rainYesterday) = await FetchForecastAsync(ct);
            var past = await FetchHistoryAsync(ct);
            var outlook = new WeatherOutlook(DateTime.Now, _stationId, _stationName, hourly, daily, past, rainToday, rainYesterday);
            SaveCache(outlook);
            LastError = null;
            _ui.TryEnqueue(() => { Outlook = outlook; Updated?.Invoke(); });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            LastError = ex is HttpRequestException or TaskCanceledException or WeatherFlowException
                ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            SystemEvents.Report("Weather forecast unavailable", LastError, dedupe: TimeSpan.FromHours(6));
        }
        finally { _refreshing.Release(); }
    }

    // ---- API calls --------------------------------------------------------------------------------

    private async Task<JsonDocument> GetAsync(string pathAndQuery, CancellationToken ct)
    {
        var sep = pathAndQuery.Contains('?') ? '&' : '?';
        using var res = await _http.GetAsync($"{pathAndQuery}{sep}token={Uri.EscapeDataString(_token)}", ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            // Never echo the request URL: it carries the token.
            var what = pathAndQuery.Split('?')[0];
            throw new WeatherFlowException(res.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden =>
                    "WeatherFlow refused the token (weather.tempestToken in settings.json). Create a new one at tempestwx.com > Settings > Data Authorizations.",
                _ => $"WeatherFlow {what} returned {(int)res.StatusCode}",
            });
        }
        return JsonDocument.Parse(body);
    }

    private async Task FindStationAsync(CancellationToken ct)
    {
        using var doc = await GetAsync("stations", ct);
        if (!doc.RootElement.TryGetProperty("stations", out var stations) || stations.GetArrayLength() == 0)
            throw new WeatherFlowException("No Tempest station on this WeatherFlow account");
        var station = stations.EnumerateArray().FirstOrDefault(s => _stationSetting is null || Int(s, "station_id") == _stationSetting);
        if (station.ValueKind != JsonValueKind.Object)
            throw new WeatherFlowException($"Station {_stationSetting} (weather.tempestStationId) isn't on this WeatherFlow account");
        var device = station.TryGetProperty("devices", out var devices)
            ? devices.EnumerateArray().FirstOrDefault(d => Str(d, "device_type") == "ST") : default;
        if (device.ValueKind != JsonValueKind.Object)
            throw new WeatherFlowException("No Tempest sensor (device type ST) on the station");
        _stationId = Int(station, "station_id") ?? 0;
        _stationName = Str(station, "name");
        _deviceId = Int(device, "device_id") ?? 0;
    }

    private async Task<(List<HourForecast>, List<DayForecast>, double?, double?)> FetchForecastAsync(CancellationToken ct)
    {
        using var doc = await GetAsync(
            $"better_forecast?station_id={_stationId}&units_temp=c&units_wind=mps&units_pressure=mb&units_precip=mm&units_distance=km", ct);
        var root = doc.RootElement;
        var forecast = root.GetProperty("forecast");

        var hourly = forecast.GetProperty("hourly").EnumerateArray().Select(h => new HourForecast(
            Local(Long(h, "time")) ?? DateTime.MinValue,
            Num(h, "air_temperature"), Num(h, "feels_like"), Str(h, "conditions"), Str(h, "icon"),
            Int(h, "precip_probability") ?? 0, Str(h, "precip_type"), Num(h, "precip"),
            Num(h, "wind_avg"), Num(h, "wind_gust"), Num(h, "wind_direction"),
            Num(h, "relative_humidity"), Num(h, "station_pressure"), Num(h, "uv"))).ToList();

        var daily = forecast.GetProperty("daily").EnumerateArray().Select(d => new DayForecast(
            Local(Long(d, "day_start_local"))?.Date ?? DateTime.MinValue,
            Num(d, "air_temp_high"), Num(d, "air_temp_low"), Str(d, "conditions"), Str(d, "icon"),
            Int(d, "precip_probability") ?? 0, Str(d, "precip_type"),
            Local(Long(d, "sunrise")), Local(Long(d, "sunset")))).ToList();

        double? rainToday = null, rainYesterday = null;
        if (root.TryGetProperty("current_conditions", out var cc))
        {
            rainToday = Num(cc, "precip_accum_local_day");
            rainYesterday = Num(cc, "precip_accum_local_yesterday");
        }
        return (hourly, daily, rainToday, rainYesterday);
    }

    /// <summary>The last 24 full hours plus the current one, from the one-minute readings.</summary>
    private async Task<List<HourObserved>> FetchHistoryAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.Now;
        var start = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Offset).AddHours(-24);
        using var doc = await GetAsync(
            $"observations/device/{_deviceId}?time_start={start.ToUnixTimeSeconds()}&time_end={now.ToUnixTimeSeconds()}", ct);
        if (!doc.RootElement.TryGetProperty("obs", out var obs) || obs.ValueKind != JsonValueKind.Array) return [];

        // obs_st row: 0 time, 1 lull, 2 wind avg, 3 gust, 4 dir, 5 wind interval, 6 station pressure, 7 air temp,
        // 8 humidity, 9 lux, 10 UV, 11 solar, 12 rain over the interval, 13 precip type, 14 strike distance,
        // 15 strike count, 16 battery, 17 report interval (minutes), ...
        var rows = obs.EnumerateArray()
            .Where(r => r.ValueKind == JsonValueKind.Array && r.GetArrayLength() > 15)
            .Select(r => (Time: Local(At(r, 0) is double t ? (long)t : null), Row: r))
            .Where(x => x.Time is not null);

        return rows.GroupBy(x => new DateTime(x.Time!.Value.Year, x.Time.Value.Month, x.Time.Value.Day, x.Time.Value.Hour, 0, 0))
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var r = g.Select(x => x.Row).ToList();
                var temps = r.Select(x => At(x, 7)).OfType<double>().ToList();
                var wind = r.Select(x => At(x, 2)).OfType<double>().ToList();
                var gusts = r.Select(x => At(x, 3)).OfType<double>().ToList();
                var hum = r.Select(x => At(x, 8)).OfType<double>().ToList();
                return new HourObserved(
                    g.Key, r.Sum(x => (int)(At(x, 17) ?? 1)),
                    temps.Count > 0 ? temps.Average() : null, temps.Count > 0 ? temps.Min() : null, temps.Count > 0 ? temps.Max() : null,
                    wind.Count > 0 ? wind.Average() : null, gusts.Count > 0 ? gusts.Max() : null,
                    r.Select(x => At(x, 6)).OfType<double>().LastOrDefault() is var p && p > 0 ? p : null,
                    hum.Count > 0 ? hum.Average() : null,
                    r.Sum(x => At(x, 12) ?? 0),
                    r.Sum(x => (int)(At(x, 15) ?? 0)));
            })
            .ToList();
    }

    // ---- Cache ---------------------------------------------------------------------------------------

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return;
            var o = JsonSerializer.Deserialize<WeatherOutlook>(File.ReadAllText(CachePath));
            if (o is null || DateTime.Now - o.FetchedAt > TimeSpan.FromHours(12)) return;
            Outlook = o; // Start() runs on the UI thread
            _stationId = o.StationId;
            _stationName = o.StationName;
        }
        catch (Exception) { /* a bad cache just means waiting for the first fetch */ }
    }

    private static void SaveCache(WeatherOutlook o)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(o));
        }
        catch (Exception) { }
    }

    // ---- JSON helpers ----------------------------------------------------------------------------------

    private static DateTime? Local(long? unix) => unix is long u ? DateTimeOffset.FromUnixTimeSeconds(u).LocalDateTime : null;

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static int? Int(JsonElement e, string name) => Num(e, name) is double d ? (int)Math.Round(d) : null;

    private static long? Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null;

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static double? At(JsonElement row, int i) =>
        i < row.GetArrayLength() && row[i].ValueKind == JsonValueKind.Number ? row[i].GetDouble() : null;

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
        _cts.Dispose();
    }

    private sealed class WeatherFlowException(string message) : Exception(message);
}
