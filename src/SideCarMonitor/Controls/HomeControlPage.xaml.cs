using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SideCarMonitor.Services;

namespace SideCarMonitor.Controls;

/// <summary>
/// The House page: lights, switches and blinds by room as big toggles, Home Assistant scripts as buttons,
/// and the recent action log. Your taps are logged but not checked by the decision model; only the
/// agent's actions are.
/// </summary>
public sealed partial class HomeControlPage : UserControl
{
    private HomeAssistantBridge? _home;

    public event Action? HomeRequested;
    /// <summary>Lights currently on (for the home tile); raised after each refresh.</summary>
    public event Action<int, int>? CountsChanged;

    public HomeControlPage() => InitializeComponent();

    public void Initialize(HomeAssistantBridge? home)
    {
        _home = home;
        if (home is not null) home.PausedChanged += _ => DispatcherQueue.TryEnqueue(UpdatePaused);
    }

    public async void Show()
    {
        UpdatePaused();
        await RefreshAsync();
    }

    private void Home_Click(object sender, RoutedEventArgs e) => HomeRequested?.Invoke();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void Resume_Click(object sender, RoutedEventArgs e) { _home?.Resume(); UpdatePaused(); ShowRecent(); }

    private void UpdatePaused()
    {
        var reason = _home?.PausedReason;
        PausedBar.Visibility = reason is null ? Visibility.Collapsed : Visibility.Visible;
        PausedText.Text = reason is null ? "" : $"SideCar's home control is paused: {reason}. Your own taps still work.";
    }

    private static T Res<T>(string key) => (T)Application.Current.Resources[key];

    /// <summary>Reloads devices and scripts; also used by the home tile timer.</summary>
    public async Task RefreshAsync()
    {
        if (_home is null)
        {
            SubtitleText.Text = "Home Assistant isn't set up (no token file).";
            return;
        }
        SubtitleText.Text = "Loading…";
        try
        {
            var devices = await _home.DevicesAsync();
            BuildRooms(devices);
            var lightsOn = devices.Count(d => d.Domain == "light" && d.IsOn);
            SubtitleText.Text = $"{lightsOn} light{(lightsOn == 1 ? "" : "s")} on · {DateTime.Now:h:mm tt}";
            CountsChanged?.Invoke(lightsOn, devices.Count(d => d.Domain == "light"));
            if (ScriptsPanel.Children.Count == 0) BuildScripts(await _home.ScriptsAsync());
        }
        catch (Exception ex)
        {
            SubtitleText.Text = $"Can't reach Home Assistant: {ex.Message}";
        }
        ShowRecent();
    }

    private void BuildRooms(List<HomeAssistantBridge.Device> devices)
    {
        RoomsPanel.Children.Clear();
        var size = Res<double>("TouchTarget");
        foreach (var room in devices.GroupBy(d => d.Area))
        {
            RoomsPanel.Children.Add(new TextBlock
            {
                Text = room.Key, FontSize = Res<double>("FontBody"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 4, 0, 0),
            });
            var grid = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = size * 3.2, ItemHeight = size * 1.35 };
            foreach (var d in room) grid.Children.Add(DeviceButton(d));
            RoomsPanel.Children.Add(grid);
        }
        if (devices.Count == 0)
            RoomsPanel.Children.Add(new TextBlock { Text = "No lights, switches or blinds are exposed to Assist in Home Assistant.", TextWrapping = TextWrapping.Wrap });
    }

    private Button DeviceButton(HomeAssistantBridge.Device d)
    {
        var on = d.IsOn;
        var state = d.Domain == "cover" ? (on ? "Open" : "Closed") : (on ? "On" : "Off");
        var b = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 6, 6),
            Padding = Res<Thickness>("PanelPadding"), CornerRadius = Res<CornerRadius>("SmallCorner"),
            Background = on ? Res<Brush>("TileAmber") : Res<Brush>("PanelBackground"),
            Content = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = d.Name, FontSize = Res<double>("FontCaption"), TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                    new TextBlock { Text = state, FontSize = Res<double>("FontCaption"), Opacity = 0.8 },
                },
            },
        };
        AutomationProperties.SetName(b, $"{d.Name}, {state}");
        b.Click += async (_, _) =>
        {
            b.IsEnabled = false;
            var error = await _home!.UserSetAsync(d, !on);
            if (error is not null) SubtitleText.Text = $"{d.Name}: {error}";
            await Task.Delay(600); // let Home Assistant report the new state
            await RefreshAsync();
        };
        return b;
    }

    private void BuildScripts(List<HomeAssistantBridge.Script> scripts)
    {
        ScriptsPanel.Children.Clear();
        foreach (var s in scripts)
        {
            var b = new Button
            {
                Content = s.Name, FontSize = Res<double>("FontBody"), HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left, MinHeight = Res<double>("TouchTarget"),
                Padding = Res<Thickness>("PanelPadding"), CornerRadius = Res<CornerRadius>("SmallCorner"),
                Background = Res<Brush>("TileTeal"), Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            };
            b.Click += async (_, _) =>
            {
                b.IsEnabled = false;
                var error = await _home!.UserRunScriptAsync(s);
                SubtitleText.Text = error is null ? $"Ran {s.Name}" : $"{s.Name}: {error}";
                b.IsEnabled = true;
                await Task.Delay(1500);
                await RefreshAsync();
            };
            ScriptsPanel.Children.Add(b);
        }
        if (scripts.Count == 0) ScriptsPanel.Children.Add(new TextBlock { Text = "No scripts exposed", Foreground = Res<Brush>("MutedText") });
    }

    private void ShowRecent()
    {
        RecentPanel.Children.Clear();
        foreach (var line in HomeAssistantBridge.RecentActions(12))
            RecentPanel.Children.Add(new TextBlock
            {
                Text = line, FontSize = Res<double>("FontCaption"), TextWrapping = TextWrapping.Wrap,
                Foreground = line.Contains("Paused") || line.Contains("(blocked)") ? Res<Brush>("AlertBrush") : Res<Brush>("MutedText"),
            });
        if (RecentPanel.Children.Count == 0)
            RecentPanel.Children.Add(new TextBlock { Text = "Nothing yet", FontSize = Res<double>("FontCaption"), Foreground = Res<Brush>("MutedText") });
    }
}
