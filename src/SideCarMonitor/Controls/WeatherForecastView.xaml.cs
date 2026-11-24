using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SideCarMonitor.Services;

namespace SideCarMonitor.Controls;

/// <summary>
/// The Weather page's Hourly and 10 days tabs. Hourly is a sideways strip: the past hours from the user's own station
/// ("Actual"), then the forecast, with a details panel for the tapped hour. 10 days is a list; tapping a day jumps the
/// strip to that day. Built in code because the cards are simple and the data changes every 30 minutes.
/// </summary>
public sealed partial class WeatherForecastView : UserControl
{
    private WeatherOutlook? _outlook;
    private WeatherFormat? _wx;
    private readonly List<(Button Card, object Hour)> _cards = [];
    private object? _selected;

    public WeatherForecastView() => InitializeComponent();

    private static T R<T>(string key) => (T)Application.Current.Resources[key];

    /// <summary>"hourly" or "days".</summary>
    public string Mode { get; private set; } = "hourly";

    public void SetMode(string mode)
    {
        Mode = mode;
        var has = _outlook is not null;
        HourlyPanel.Visibility = has && mode == "hourly" ? Visibility.Visible : Visibility.Collapsed;
        DaysPanel.Visibility = has && mode == "days" ? Visibility.Visible : Visibility.Collapsed;
        if (mode == "hourly") ScrollToSelected();
    }

    /// <summary>Redraw from the latest outlook. <paramref name="emptyReason"/> is shown when there's no outlook.</summary>
    public void Show(WeatherOutlook? outlook, WeatherFormat wx, IReadOnlyList<string> highlights, string emptyReason)
    {
        _outlook = outlook;
        _wx = wx;
        EmptyText.Text = emptyReason;
        EmptyText.Visibility = outlook is null ? Visibility.Visible : Visibility.Collapsed;
        if (outlook is null) { SetMode(Mode); return; }

        HighlightsText.Text = highlights.Count > 0 ? "Next 24 hours: " + string.Join("  ·  ", highlights) : "";
        BuildHours(outlook, wx);
        BuildDays(outlook, wx);
        SetMode(Mode);
    }

    // ---- Hourly strip ----------------------------------------------------------------------------------------

    private void BuildHours(WeatherOutlook o, WeatherFormat wx)
    {
        var now = DateTime.Now;
        var thisHour = now.Date.AddHours(now.Hour);
        // Past hours (and the current one so far) come from the station; the forecast starts at the next hour
        // (or this one, if WeatherFlow still has it and the station doesn't).
        var past = o.PastHours.Where(h => h.Hour >= thisHour.AddHours(-12) && h.Hour <= thisHour).ToList();
        var nowObserved = past.Any(h => h.Hour == thisHour);
        var coming = o.Hourly.Where(h => nowObserved ? h.Time > thisHour : h.Time >= thisHour).Take(48).ToList();

        // Keep the selection across refreshes when that hour is still shown; otherwise select "now".
        var keep = _selected switch
        {
            HourForecast f => (DateTime?)f.Time,
            HourObserved p => p.Hour,
            _ => null,
        };

        HourStrip.Children.Clear();
        _cards.Clear();
        DateTime? lastDay = null;
        foreach (var p in past) AddCard(ObservedCard(p, p.Hour == thisHour, wx, ref lastDay), p);
        foreach (var f in coming) AddCard(ForecastCard(f, f.Time == thisHour, wx, ref lastDay), f);

        var select = _cards.FirstOrDefault(c => Time(c.Hour) == keep).Hour
                     ?? _cards.FirstOrDefault(c => Time(c.Hour) == thisHour).Hour
                     ?? _cards.FirstOrDefault(c => c.Hour is HourForecast).Hour
                     ?? _cards.LastOrDefault().Hour;
        if (select is not null) Select(select);
    }

    private void AddCard(Button card, object hour)
    {
        card.Click += (_, _) => Select(hour);
        HourStrip.Children.Add(card);
        _cards.Add((card, hour));
    }

    private static DateTime? Time(object? hour) => hour switch
    {
        HourForecast f => f.Time,
        HourObserved p => p.Hour,
        _ => null,
    };

    private Button ForecastCard(HourForecast h, bool isNow, WeatherFormat wx, ref DateTime? lastDay)
    {
        var lines = new List<UIElement>
        {
            Label(isNow ? "Now" : DayAware(h.Time, ref lastDay), bold: isNow),
            Emoji(Icon(h.Icon)),
            Big(wx.Temp(h.TempC)),
            Small(h.PrecipProbability > 0 ? $"\U0001F4A7{h.PrecipProbability}%" : " "),
            Small($"{wx.SpeedNumber(h.WindAvgMs)}–{wx.SpeedNumber(h.WindGustMs)} {wx.SpeedUnit}"),
        };
        return Card(lines, R<Brush>("PanelBackground"), isNow);
    }

    private Button ObservedCard(HourObserved h, bool isNow, WeatherFormat wx, ref DateTime? lastDay)
    {
        var label = DayAware(h.Hour, ref lastDay);
        var lines = new List<UIElement>
        {
            Label(isNow ? "Now" : label, bold: isNow),
            Small(isNow ? "so far" : "Actual"),
            Big(wx.Temp(h.TempAvgC)),
            Small(h.RainMm > 0 ? $"\U0001F4A7{wx.Rain(h.RainMm)}" : " "),
            Small($"{wx.SpeedNumber(h.WindAvgMs)}–{wx.SpeedNumber(h.GustMaxMs)} {wx.SpeedUnit}"),
        };
        var card = Card(lines, R<Brush>("KioskBackground"), isNow);
        if (!isNow)
        {
            card.BorderBrush = R<Brush>("PanelBorder");
            card.BorderThickness = new Thickness(1);
        }
        return card;
    }

    /// <summary>"3 PM", or "Tue 12 AM"/"Tue" at the first hour of a new day so it's clear where days change.</summary>
    private static string DayAware(DateTime t, ref DateTime? lastDay)
    {
        var label = lastDay is not null && t.Date != lastDay.Value ? $"{t:ddd} {t:h tt}" : t.ToString("h tt");
        lastDay = t.Date;
        return label;
    }

    private Button Card(List<UIElement> lines, Brush background, bool isNow)
    {
        var stack = new StackPanel { Spacing = 0, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var l in lines) stack.Children.Add(l);
        return new Button
        {
            Content = stack,
            Background = background,
            Width = R<double>("TouchTarget") * 1.5,
            Padding = R<Thickness>("RowPadding"),
            CornerRadius = R<CornerRadius>("SmallCorner"),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            BorderBrush = isNow ? R<Brush>("AccentText") : null,
            BorderThickness = isNow ? new Thickness(2) : new Thickness(0),
        };
    }

    private void Select(object hour)
    {
        _selected = hour;
        foreach (var (card, h) in _cards)
            card.Background = ReferenceEquals(h, hour) ? R<Brush>("TileCobalt")
                            : h is HourObserved ? R<Brush>("KioskBackground") : R<Brush>("PanelBackground");
        if (_wx is { } wx)
        {
            if (hour is HourForecast f) ShowForecastDetail(f, wx);
            else if (hour is HourObserved p) ShowObservedDetail(p, wx);
        }
    }

    private void ScrollToSelected()
    {
        var card = _cards.FirstOrDefault(c => ReferenceEquals(c.Hour, _selected)).Card;
        if (card is null) return;
        // Put the selected hour near the left with a couple of earlier hours visible. Cards have a fixed width, so the
        // offset doesn't depend on layout; but the scroller needs its extent, so lay out first (and again once shown).
        void Scroll()
        {
            HourScroller.UpdateLayout();
            var index = HourStrip.Children.IndexOf(card);
            var step = card.Width + HourStrip.Spacing;
            HourScroller.ChangeView(Math.Max(0, (index - 2) * step), null, null, disableAnimation: true);
        }
        Scroll();
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, Scroll);
    }

    private void ShowForecastDetail(HourForecast h, WeatherFormat wx)
    {
        DetailTitle.Text = $"{h.Time:dddd h tt} · forecast · {h.Conditions}";
        var precip = h.PrecipProbability > 0
            ? $"{h.PrecipProbability}% {h.PrecipType}" + (h.PrecipMm > 0 ? $", {wx.Rain(h.PrecipMm.Value)}" : "")
            : "None expected";
        FillDetail(
            ("Temperature", wx.Temp(h.TempC)), ("Feels like", wx.Temp(h.FeelsLikeC)), ("Humidity", h.HumidityPct is double rh ? $"{rh:0}%" : "--"),
            ("Wind", $"{wx.Speed(h.WindAvgMs)} {WeatherFormat.Direction(h.WindDirDeg)}"), ("Gusts", wx.Speed(h.WindGustMs)), ("Precipitation", precip),
            ("Pressure", wx.Pressure(h.StationPressureMb)), ("UV index", h.Uv is double uv ? $"{uv:0}" : "--"), ("", ""));
    }

    private void ShowObservedDetail(HourObserved h, WeatherFormat wx)
    {
        DetailTitle.Text = $"{h.Hour:dddd h tt} · your station" + (h.Minutes < 55 ? $" · {h.Minutes} min of readings" : "");
        FillDetail(
            ("Average", wx.Temp(h.TempAvgC)), ("Low", wx.Temp(h.TempMinC)), ("High", wx.Temp(h.TempMaxC)),
            ("Wind", wx.Speed(h.WindAvgMs)), ("Peak gust", wx.Speed(h.GustMaxMs)), ("Rain", h.RainMm > 0 ? wx.Rain(h.RainMm) : "None"),
            ("Pressure", wx.Pressure(h.PressureMb)), ("Humidity", h.HumidityPct is double rh ? $"{rh:0}%" : "--"),
            ("Lightning", h.Strikes > 0 ? $"{h.Strikes} strikes" : "None"));
    }

    /// <summary>Three columns of label/value pairs.</summary>
    private void FillDetail(params (string Label, string Value)[] items)
    {
        DetailGrid.Children.Clear();
        DetailGrid.ColumnDefinitions.Clear();
        DetailGrid.RowDefinitions.Clear();
        for (var c = 0; c < 3; c++) DetailGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var rows = (items.Length + 2) / 3;
        for (var r = 0; r < rows; r++) DetailGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < items.Length; i++)
        {
            if (items[i].Label.Length == 0) continue;
            var cell = new StackPanel();
            cell.Children.Add(new TextBlock { Text = items[i].Label, FontSize = R<double>("FontCaption"), Foreground = R<Brush>("MutedText") });
            cell.Children.Add(new TextBlock { Text = items[i].Value, FontSize = R<double>("FontBody"), TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(cell, i % 3);
            Grid.SetRow(cell, i / 3);
            DetailGrid.Children.Add(cell);
        }
    }

    // ---- 10 days -------------------------------------------------------------------------------------------

    private void BuildDays(WeatherOutlook o, WeatherFormat wx)
    {
        DayList.Children.Clear();
        var today = DateTime.Today;
        foreach (var d in o.Daily)
        {
            var row = new Grid { ColumnSpacing = R<double>("Gap") };
            foreach (var w in new[] { 2.2, 0.8, 4.0, 1.8, 2.4, 3.2 })
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });
            var name = d.Date == today ? "Today" : d.Date == today.AddDays(1) ? "Tomorrow" : d.Date.ToString("ddd MMM d");
            Put(row, 0, new TextBlock { Text = name, FontSize = R<double>("FontBody"), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            Put(row, 1, Emoji(Icon(d.Icon)));
            Put(row, 2, Text(d.Conditions));
            Put(row, 3, Text(d.PrecipProbability > 0 ? $"\U0001F4A7 {d.PrecipProbability}% {d.PrecipType}" : "", muted: true));
            Put(row, 4, new TextBlock
            {
                Text = $"{wx.Temp(d.LowC)} / {wx.Temp(d.HighC)}", FontSize = R<double>("FontTitle"), VerticalAlignment = VerticalAlignment.Center,
            });
            Put(row, 5, Text(d.Sunrise is DateTime sr && d.Sunset is DateTime ss ? $"☀ {sr:h:mm} – {ss:h:mm tt}" : "", muted: true));

            var day = d.Date;
            var button = new Button
            {
                Content = row,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                MinHeight = R<double>("TouchTarget"),
                Padding = R<Thickness>("PanelPadding"),
                Background = R<Brush>("PanelBackground"),
                CornerRadius = R<CornerRadius>("SmallCorner"),
            };
            // Tap a day: show its hours (noon, or the first forecast hour if it's today).
            button.Click += (_, _) =>
            {
                var target = _cards.Select(c => c.Hour).OfType<HourForecast>()
                    .FirstOrDefault(h => h.Time.Date == day && (day == today || h.Time.Hour == 12))
                    ?? _cards.Select(c => c.Hour).OfType<HourForecast>().LastOrDefault(h => h.Time.Date <= day);
                if (target is not null) Select(target);
                DayTapped?.Invoke();
            };
            DayList.Children.Add(button);
        }
    }

    /// <summary>A day was tapped in 10 days; the page switches to Hourly.</summary>
    public event Action? DayTapped;

    private static void Put(Grid g, int col, FrameworkElement e) { Grid.SetColumn(e, col); g.Children.Add(e); }

    // ---- Small builders and icons ------------------------------------------------------------------------------

    private static TextBlock Label(string s, bool bold = false) => new()
    {
        Text = s, FontSize = R<double>("FontCaption"), HorizontalAlignment = HorizontalAlignment.Center,
        FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
    };

    private static TextBlock Small(string s) => new()
    {
        Text = s, FontSize = R<double>("FontCaption"), Foreground = R<Brush>("MutedText"), HorizontalAlignment = HorizontalAlignment.Center,
    };

    private static TextBlock Big(string s) => new()
    {
        Text = s, FontSize = R<double>("FontTitle"), HorizontalAlignment = HorizontalAlignment.Center,
    };

    private static TextBlock Text(string s, bool muted = false)
    {
        var t = new TextBlock { Text = s, FontSize = R<double>("FontBody"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        if (muted) t.Foreground = R<Brush>("MutedText");
        return t;
    }

    private static TextBlock Emoji(string s) => new()
    {
        Text = s, FontSize = R<double>("FontTitle"), FontFamily = new FontFamily("Segoe UI Emoji"),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>WeatherFlow icon names (e.g. "possibly-snow-night") to an emoji.</summary>
    public static string Icon(string icon) => icon switch
    {
        _ when icon.Contains("thunderstorm") => "⛈️",
        _ when icon.Contains("snow") || icon.Contains("sleet") => "\U0001F328️",
        _ when icon.Contains("rain") => "\U0001F327️",
        _ when icon.Contains("fog") => "\U0001F32B️",
        _ when icon.Contains("wind") => "\U0001F4A8",
        "partly-cloudy-day" => "⛅",
        "partly-cloudy-night" => "☁️",
        "cloudy" => "☁️",
        "clear-day" => "☀️",
        "clear-night" => "\U0001F319",
        _ => "\U0001F321️",
    };
}
