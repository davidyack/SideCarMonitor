using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Dispatching;

namespace SideCarMonitor.Services;

/// <summary>Latest readings from a WeatherFlow Tempest station, all in metric (as broadcast).</summary>
public sealed class TempestState
{
    public string? StationSerial { get; set; }
    public DateTime? LastObservation { get; set; }
    public double? TemperatureC { get; set; }
    public double? HumidityPct { get; set; }
    public double? StationPressureMb { get; set; }
    public double? WindAvgMs { get; set; }
    public double? WindGustMs { get; set; }
    public double? WindLullMs { get; set; }
    public double? WindDirDeg { get; set; }
    public double? RapidWindMs { get; set; }
    public double? RapidWindDirDeg { get; set; }
    public double? Lux { get; set; }
    public double? Uv { get; set; }
    public double? SolarWm2 { get; set; }
    public int PrecipType { get; set; }            // 0 none, 1 rain, 2 hail, 3 rain + hail
    public double RainTodayMm { get; set; }
    public DateTime? LastRainStart { get; set; }
    public int StrikesToday { get; set; }
    public double? LastStrikeKm { get; set; }
    public DateTime? LastStrike { get; set; }
    public double? BatteryVolts { get; set; }

    /// <summary>Station pressure readings for the trend (kept for ~3.5 hours).</summary>
    public List<(DateTime Time, double Mb)> PressureHistory { get; } = [];

    public double? DewPointC => TemperatureC is double t && HumidityPct is double rh && rh > 0
        ? DewPoint(t, rh) : null;

    /// <summary>Wind chill below 10 C with wind, heat index above 27 C, otherwise air temperature.</summary>
    public double? FeelsLikeC
    {
        get
        {
            if (TemperatureC is not double t) return null;
            var windKmh = (WindAvgMs ?? 0) * 3.6;
            if (t <= 10 && windKmh > 4.8)
                return 13.12 + 0.6215 * t - 11.37 * Math.Pow(windKmh, 0.16) + 0.3965 * t * Math.Pow(windKmh, 0.16);
            if (t >= 27 && HumidityPct is double rh)
            {
                var f = t * 9 / 5 + 32;
                var hi = -42.379 + 2.04901523 * f + 10.14333127 * rh - 0.22475541 * f * rh - 0.00683783 * f * f
                         - 0.05481717 * rh * rh + 0.00122874 * f * f * rh + 0.00085282 * f * rh * rh
                         - 0.00000199 * f * f * rh * rh;
                return (hi - 32) * 5 / 9;
            }
            return t;
        }
    }

    /// <summary>Change in station pressure over the last ~3 hours, in mb (null until enough history).</summary>
    public double? PressureTrendMb
    {
        get
        {
            if (StationPressureMb is not double now || PressureHistory.Count < 2) return null;
            var (time, mb) = PressureHistory[0];
            return DateTime.Now - time >= TimeSpan.FromMinutes(60) ? now - mb : null;
        }
    }

    private static double DewPoint(double t, double rh)
    {
        const double a = 17.625, b = 243.04;
        var g = Math.Log(rh / 100) + a * t / (b + t);
        return b * g / (a - g);
    }
}

/// <summary>
/// Listens for the Tempest hub's local UDP broadcasts (port 50222, no account or token needed).
/// obs_st arrives every minute, rapid_wind every 3 seconds. Rain and lightning "today" totals are
/// counted from when the app started (or local midnight) because the broadcast has no daily totals.
/// </summary>
public sealed class TempestListener : IDisposable
{
    public const int Port = 50222;

    private readonly DispatcherQueue _ui;
    private readonly CancellationTokenSource _cts = new();
    private UdpClient? _udp;
    private DateTime _day = DateTime.Today;

    public TempestState State { get; } = new();

    /// <summary>Raised on the UI thread after any reading changes State.</summary>
    public event Action? Updated;

    public TempestListener(DispatcherQueue ui) => _ui = ui;

    public void Start()
    {
        _udp = new UdpClient();
        // Share the port with other Tempest listeners (Home Assistant, WeatherFlow apps, ...).
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
        _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _udp is not null)
        {
            try
            {
                var result = await _udp.ReceiveAsync(ct);
                var json = Encoding.UTF8.GetString(result.Buffer);
                _ui.TryEnqueue(() =>
                {
                    if (Apply(json)) Updated?.Invoke();
                });
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { await Task.Delay(1000, ct).ConfigureAwait(false); }
        }
    }

    /// <summary>Applies one broadcast message to State. Runs on the UI thread so State has a single writer.</summary>
    private bool Apply(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var s = State;

            if (DateTime.Today != _day)
            {
                _day = DateTime.Today;
                s.RainTodayMm = 0;
                s.StrikesToday = 0;
            }

            switch (root.GetProperty("type").GetString())
            {
                case "obs_st":
                    var o = root.GetProperty("obs")[0];
                    double? D(int i) => o.GetArrayLength() > i && o[i].ValueKind == JsonValueKind.Number ? o[i].GetDouble() : null;
                    s.StationSerial = root.TryGetProperty("serial_number", out var sn) ? sn.GetString() : s.StationSerial;
                    s.LastObservation = DateTimeOffset.FromUnixTimeSeconds((long)(D(0) ?? 0)).LocalDateTime;
                    s.WindLullMs = D(1);
                    s.WindAvgMs = D(2);
                    s.WindGustMs = D(3);
                    s.WindDirDeg = D(4);
                    s.StationPressureMb = D(6);
                    s.TemperatureC = D(7);
                    s.HumidityPct = D(8);
                    s.Lux = D(9);
                    s.Uv = D(10);
                    s.SolarWm2 = D(11);
                    s.RainTodayMm += D(12) ?? 0;
                    s.PrecipType = (int)(D(13) ?? 0);
                    s.StrikesToday += (int)(D(15) ?? 0);
                    if ((D(15) ?? 0) > 0 && D(14) is double km && km > 0) s.LastStrikeKm = km;
                    s.BatteryVolts = D(16);
                    if (s.StationPressureMb is double p)
                    {
                        s.PressureHistory.Add((DateTime.Now, p));
                        s.PressureHistory.RemoveAll(h => DateTime.Now - h.Time > TimeSpan.FromHours(3.5));
                    }
                    return true;

                case "rapid_wind":
                    var w = root.GetProperty("ob");
                    s.RapidWindMs = w[1].GetDouble();
                    s.RapidWindDirDeg = w[2].GetDouble();
                    return true;

                case "evt_strike":
                    var e = root.GetProperty("evt");
                    s.LastStrike = DateTimeOffset.FromUnixTimeSeconds(e[0].GetInt64()).LocalDateTime;
                    s.LastStrikeKm = e[1].GetDouble();
                    return true;

                case "evt_precip":
                    s.LastRainStart = DateTimeOffset.FromUnixTimeSeconds(root.GetProperty("evt")[0].GetInt64()).LocalDateTime;
                    return true;
            }
        }
        catch (Exception)
        {
            // Ignore malformed or unexpected packets.
        }
        return false;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp?.Dispose();
        _cts.Dispose();
    }
}
