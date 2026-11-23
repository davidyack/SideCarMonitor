namespace SideCarMonitor.Services;

/// <summary>Turns metric Tempest readings into display strings in the configured units.</summary>
public sealed class WeatherFormat(bool imperial)
{
    private static readonly string[] Compass =
        ["N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW"];

    public string Temp(double? c) => c is double v ? $"{(imperial ? v * 9 / 5 + 32 : v):0}°" : "--";

    public string Speed(double? ms) => ms is double v
        ? imperial ? $"{v * 2.23694:0} mph" : $"{v * 3.6:0} km/h"
        : "--";

    public string SpeedNumber(double? ms) => ms is double v ? (imperial ? v * 2.23694 : v * 3.6).ToString("0") : "--";
    public string SpeedUnit => imperial ? "mph" : "km/h";

    public string Rain(double mm) => imperial ? $"{mm / 25.4:0.00} in" : $"{mm:0.0} mm";

    public string Pressure(double? mb) => mb is double v ? imperial ? $"{v * 0.02953:0.00} inHg" : $"{v:0} mb" : "--";

    public string PressureTrend(double? deltaMb) => deltaMb switch
    {
        null => "trend in ~1 hr",
        > 1.0 => "rising",
        < -1.0 => "falling",
        _ => "steady",
    };

    public string Distance(double? km) => km is double v ? imperial ? $"{v * 0.621371:0} mi" : $"{v:0} km" : "--";

    public static string Direction(double? deg) => deg is double d ? Compass[(int)Math.Round(d / 22.5) % 16] : "";

    public static string PrecipText(int type) => type switch
    {
        1 => "Raining",
        2 => "Hail",
        3 => "Rain and hail",
        _ => "",
    };

    /// <summary>Short sky description from what the station can measure.</summary>
    public static string Condition(TempestState s)
    {
        var precip = PrecipText(s.PrecipType);
        if (precip.Length > 0) return precip;
        if (s.LastStrike is DateTime strike && DateTime.Now - strike < TimeSpan.FromMinutes(15)) return "Lightning nearby";
        if ((s.WindGustMs ?? 0) > 13) return "Very windy";
        if ((s.WindAvgMs ?? 0) > 7) return "Windy";
        if (s.Lux is double lux)
        {
            if (lux < 50) return "Night";
            if (lux > 50000) return "Sunny";
            if (lux > 10000) return "Partly sunny";
            return "Overcast";
        }
        return "";
    }
}
