using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SideCarMonitor.Services;

namespace SideCarMonitor.Controls;

/// <summary>
/// LinkedIn: Signal and Noise tabs. Each post has Noise / Not noise (that's how it learns), Profile and Open.
/// The right side shows today's digest, or the profile of the person you tapped (their posts over time and
/// an AI summary, with Refresh from LinkedIn).
/// </summary>
public sealed partial class LinkedInPage : UserControl
{
    private LinkedInFeed? _feed;
    private string _tab = "signal";
    private string? _profileUrl;

    public event Action? HomeRequested;

    public LinkedInPage() => InitializeComponent();

    public void Initialize(LinkedInFeed? feed)
    {
        _feed = feed;
        if (feed is null) return;
        feed.Changed += () => DispatcherQueue.TryEnqueue(() => { if (Visibility == Visibility.Visible) Render(keepScroll: true); });
        feed.Progress += text => DispatcherQueue.TryEnqueue(() => { if (text.Length > 0) SubtitleText.Text = text; else UpdateSubtitle(); });
    }

    public void Show() { _profileUrl = null; Render(keepScroll: false); }

    private static T Res<T>(string key) => (T)Application.Current.Resources[key];
    private static double Font(string key) => Res<double>(key);

    private void Home_Click(object sender, RoutedEventArgs e) => HomeRequested?.Invoke();
    private void Tab_Click(object sender, RoutedEventArgs e) { _tab = (string)((Button)sender).Tag; Render(keepScroll: false); }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_feed is null) return;
        RunButton.IsEnabled = false;
        var result = await Task.Run(() => _feed.RunAsync(manual: true));
        RunButton.IsEnabled = true;
        SubtitleText.Text = result;
        Render(keepScroll: false);
    }

    private void UpdateSubtitle()
    {
        var s = _feed?.State;
        SubtitleText.Text = s?.NeedsAttention == true ? "LinkedIn needs you to sign in again (run SideCarMonitor.exe --linkedin-login)"
            : s?.LastRun is { } at ? $"Last read {at:ddd h:mm tt} · {s.LastRunResult}" : "Not read yet: tap Run now";
    }

    private void Render(bool keepScroll)
    {
        if (_feed is null) { SubtitleText.Text = "LinkedIn isn't set up."; return; }
        var offset = ListScroll.VerticalOffset;
        UpdateSubtitle();
        var signal = _feed.Signal.ToList();
        var noise = _feed.Noise.ToList();
        SignalTab.Content = $"Signal {signal.Count}";
        NoiseTab.Content = $"Noise {noise.Count}";
        SignalTab.Background = Res<Brush>(_tab == "signal" ? "TileCobalt" : "PanelBackground");
        NoiseTab.Background = Res<Brush>(_tab == "noise" ? "TileCobalt" : "PanelBackground");

        PostsPanel.Children.Clear();
        var list = _tab == "signal" ? signal : noise;
        foreach (var p in list.Take(60)) PostsPanel.Children.Add(PostCard(p));
        if (list.Count == 0)
            PostsPanel.Children.Add(new TextBlock { Text = _tab == "signal" ? "Nothing here yet. Tap Run now." : "No noise. Nice.", FontSize = Font("FontBody"), Foreground = Res<Brush>("MutedText") });
        if (keepScroll) ListScroll.ChangeView(null, offset, null, disableAnimation: true);

        if (_profileUrl is not null) RenderProfile(_profileUrl); else RenderDigest();
    }

    private Border PostCard(LiPost p, bool compact = false)
    {
        var body = new StackPanel { Spacing = 4 };
        body.Children.Add(new TextBlock
        {
            Text = p.Author + (p.Age.Length > 0 ? $"  ·  {p.Age}" : "") + (p.Promoted ? "  ·  Promoted" : ""),
            FontSize = Font("FontBody"), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (!compact && p.Headline.Length > 0)
            body.Children.Add(new TextBlock { Text = p.Headline, FontSize = Font("FontCaption"), Foreground = Res<Brush>("MutedText"), TextTrimming = TextTrimming.CharacterEllipsis });
        if (!compact && p.Context.Length > 0)
            body.Children.Add(new TextBlock { Text = p.Context, FontSize = Font("FontCaption"), Foreground = Res<Brush>("AccentText"), TextTrimming = TextTrimming.CharacterEllipsis });
        body.Children.Add(new TextBlock { Text = p.Text, FontSize = Font("FontCaption"), TextWrapping = TextWrapping.Wrap, MaxLines = compact ? 3 : 5, TextTrimming = TextTrimming.WordEllipsis });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
        if (!p.FromProfile)
        {
            var toNoise = p.IsSignal;
            buttons.Children.Add(Action(toNoise ? "Noise" : "Not noise", toNoise ? "AlertBrush" : "TileEmerald", () => { _feed!.Label(p.Id, signal: !toNoise); }));
        }
        if (!compact && p.AuthorUrl.Length > 0)
            buttons.Children.Add(Action("Profile", "TileMauve", () => { _profileUrl = p.AuthorUrl; RenderProfile(p.AuthorUrl); }));
        if (p.Link.Length > 0)
            buttons.Children.Add(Action("Open", "PanelBorder", () => OpenInBrowser(p.Link)));
        body.Children.Add(buttons);

        return new Border
        {
            Child = body, Padding = Res<Thickness>("PanelPadding"), CornerRadius = Res<CornerRadius>("SmallCorner"),
            Background = Res<Brush>("PanelBackground"),
        };
    }

    private static Button Action(string text, string brush, Action onClick)
    {
        var b = new Button
        {
            Content = text, FontSize = Font("FontCaption"), MinHeight = Res<double>("ActivityThumbHeight"),
            Padding = new Thickness(16, 6, 16, 6), CornerRadius = Res<CornerRadius>("SmallCorner"),
            Background = Res<Brush>(brush), Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    private static void OpenInBrowser(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser */ }
    }

    private TextBlock Heading(string text) =>
        new() { Text = text, FontSize = Font("FontTitle"), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };

    private void RenderDigest()
    {
        SidePanel.Children.Clear();
        var s = _feed!.State;
        SidePanel.Children.Add(Heading("Today's digest"));
        SidePanel.Children.Add(new TextBlock
        {
            Text = s.Digest.Length > 0 ? s.Digest : "No digest yet. It's written after each read.",
            FontSize = Font("FontBody"), TextWrapping = TextWrapping.Wrap,
        });
        if (s.DigestAt is { } at)
            SidePanel.Children.Add(new TextBlock { Text = $"Written {at:ddd h:mm tt} from your signal posts", FontSize = Font("FontCaption"), Foreground = Res<Brush>("MutedText") });
        SidePanel.Children.Add(new TextBlock
        {
            Text = "Tap Noise or Not noise on any post: SideCar uses your choices to sort the next read.",
            FontSize = Font("FontCaption"), Foreground = Res<Brush>("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0),
        });
    }

    private async void RenderProfile(string url)
    {
        if (_feed is null || !_feed.EnsurePerson(url) || !_feed.State.People.TryGetValue(url, out var person)) return;
        SidePanel.Children.Clear();
        var back = new Button { Content = "← Digest", FontSize = Font("FontCaption"), Padding = new Thickness(12, 6, 12, 6) };
        back.Click += (_, _) => { _profileUrl = null; RenderDigest(); };
        SidePanel.Children.Add(back);
        SidePanel.Children.Add(Heading(person.Name));
        if (person.Headline.Length > 0)
            SidePanel.Children.Add(new TextBlock { Text = person.Headline, FontSize = Font("FontBody"), Foreground = Res<Brush>("MutedText"), TextWrapping = TextWrapping.Wrap });

        var summary = new TextBlock { Text = person.Summary.Length > 0 ? person.Summary : "Summarizing…", FontSize = Font("FontBody"), TextWrapping = TextWrapping.Wrap, Foreground = Res<Brush>("AccentText") };
        SidePanel.Children.Add(summary);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var refresh = Action("Refresh from LinkedIn", "TileCobalt", () => { });
        refresh.Click += async (_, _) =>
        {
            refresh.IsEnabled = false; summary.Text = "Reading their recent activity…";
            var added = await Task.Run(() => _feed.RefreshPersonAsync(url));
            summary.Text = $"Added {added} post{(added == 1 ? "" : "s")}. Summarizing…";
            RenderProfile(url);
        };
        actions.Children.Add(refresh);
        actions.Children.Add(Action("Open profile", "PanelBorder", () => OpenInBrowser(url)));
        SidePanel.Children.Add(actions);

        SidePanel.Children.Add(new TextBlock { Text = $"Their posts ({person.PostIds.Count})", FontSize = Font("FontBody"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        foreach (var id in person.PostIds.Take(20))
            if (_feed.State.Posts.TryGetValue(id, out var p)) SidePanel.Children.Add(PostCard(p, compact: true));

        try
        {
            var text = await Task.Run(() => _feed.SummarizePersonAsync(url));
            if (_profileUrl == url) summary.Text = text.Length > 0 ? text : "No summary yet.";
        }
        catch (Exception ex)
        {
            if (_profileUrl == url) summary.Text = $"Couldn't summarize: {ex.Message}";
        }
    }
}
