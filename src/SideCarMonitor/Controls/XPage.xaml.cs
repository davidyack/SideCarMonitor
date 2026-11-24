using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SideCarMonitor.Services;

namespace SideCarMonitor.Controls;

/// <summary>
/// X: Signal / Noise / Saved, Update (up to 100 new posts), per post Noise / Not noise, Save (bookmark on X), Replies
/// (insight analysis), Unfollow (with confirm) and Open. Right side: the briefing card, the list builder, or a post's
/// reply insights. Header: Play briefing; while playing Pause / Interrupt / Done.
/// </summary>
public sealed partial class XPage : UserControl
{
    private XFeed? _feed;
    private string _tab = "signal";
    private string? _repliesFor;
    private string? _confirmUnfollow;
    private string _note = "";

    public event Action? HomeRequested;

    public XPage() => InitializeComponent();

    public void Initialize(XFeed? feed)
    {
        _feed = feed;
        if (feed is null) return;
        feed.Changed += () => DispatcherQueue.TryEnqueue(() => { if (Visibility == Visibility.Visible) Render(true); });
        feed.Progress += t => DispatcherQueue.TryEnqueue(() => { if (t.Length > 0) SubtitleText.Text = t; else UpdateSubtitle(); });
    }

    public void Show() { _repliesFor = null; Render(false); }

    private static T Res<T>(string key) => (T)Application.Current.Resources[key];
    private static double Font(string key) => Res<double>(key);
    private void Home_Click(object sender, RoutedEventArgs e) => HomeRequested?.Invoke();

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_feed is null) return;
        UpdateButton.IsEnabled = false;
        _note = await Task.Run(() => _feed.UpdateAsync());
        UpdateButton.IsEnabled = true;
        Render(false);
    }

    private void UpdateSubtitle()
    {
        var s = _feed?.State;
        SubtitleText.Text = _note.Length > 0 ? _note
            : s?.LastUpdate is { } at ? $"Updated {at:ddd h:mm tt} · {s.LastResult} · {s.PostsRead} posts read so far (≈${s.PostsRead * 0.005:0.00})"
            : "Tap Update to pull up to 100 new posts (about $0.50)";
    }

    private Button Chip(string text, string brush, Action onClick)
    {
        var b = new Button
        {
            Content = text, FontSize = Font("FontCaption"), MinHeight = Res<double>("ActivityThumbHeight"), Padding = new Thickness(16, 6, 16, 6),
            CornerRadius = Res<CornerRadius>("SmallCorner"), Background = Res<Brush>(brush), Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    private void Render(bool keepScroll)
    {
        if (_feed is null) { SubtitleText.Text = "X isn't set up."; return; }
        var offset = ListScroll.VerticalOffset;
        UpdateSubtitle();
        Tabs.Children.Clear();
        foreach (var (tag, label, count) in new[] { ("signal", "Signal", _feed.Signal.Count()), ("noise", "Noise", _feed.Noise.Count()), ("saved", "Saved", _feed.SavedPosts.Count()) })
        {
            var b = new Button
            {
                Content = $"{label} {count}", FontSize = Font("FontBody"), Height = Res<double>("TouchTarget"), Padding = Res<Thickness>("PanelPadding"),
                Background = Res<Brush>(_tab == tag ? "TileCobalt" : "PanelBackground"),
            };
            b.Click += (_, _) => { _tab = tag; Render(false); };
            Tabs.Children.Add(b);
        }
        PostsPanel.Children.Clear();
        var list = (_tab switch { "noise" => _feed.Noise, "saved" => _feed.SavedPosts, _ => _feed.Signal }).Take(80).ToList();
        foreach (var p in list) PostsPanel.Children.Add(PostCard(p));
        if (list.Count == 0)
            PostsPanel.Children.Add(new TextBlock { Text = _tab == "saved" ? "Nothing saved yet." : "Nothing here. Tap Update.", FontSize = Font("FontBody"), Foreground = Res<Brush>("MutedText") });
        if (keepScroll) ListScroll.ChangeView(null, offset, null, true);
        if (_repliesFor is not null && _feed.State.Posts.TryGetValue(_repliesFor, out var rp)) RenderReplies(rp); else RenderSide();
        RenderBriefingBar();
    }

    private Border PostCard(XItem p)
    {
        var body = new StackPanel { Spacing = 4 };
        var age = DateTime.Now - p.Created;
        body.Children.Add(new TextBlock
        {
            Text = $"{p.Author}  @{p.Username}  ·  {(age.TotalHours < 1 ? $"{(int)age.TotalMinutes}m" : age.TotalDays < 1 ? $"{(int)age.TotalHours}h" : $"{(int)age.TotalDays}d")}" +
                   (p.IsRepost ? "  ·  repost" : ""),
            FontSize = Font("FontBody"), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        body.Children.Add(new TextBlock { Text = p.Text, FontSize = Font("FontCaption"), TextWrapping = TextWrapping.Wrap, MaxLines = 6, TextTrimming = TextTrimming.WordEllipsis });
        body.Children.Add(new TextBlock { Text = $"{p.Likes:N0} likes · {p.Reposts:N0} reposts · {p.Replies:N0} replies", FontSize = Font("FontCaption"), Foreground = Res<Brush>("MutedText") });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
        if (_confirmUnfollow == p.AuthorId)
        {
            buttons.Children.Add(new TextBlock { Text = $"Unfollow @{p.Username}?", FontSize = Font("FontBody"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            buttons.Children.Add(Chip("Yes, unfollow", "AlertBrush", async () =>
            {
                _confirmUnfollow = null;
                var err = await Task.Run(() => _feed!.UnfollowAsync(p.AuthorId));
                _note = err is null ? $"Unfollowed @{p.Username}" : $"Couldn't unfollow: {err}";
                Render(true);
            }));
            buttons.Children.Add(Chip("Cancel", "PanelBorder", () => { _confirmUnfollow = null; Render(true); }));
        }
        else
        {
            // One big Actions button; the actions open as large touch buttons.
            buttons.Children.Add(ActionsButton(p));
            if (p.Saved) buttons.Children.Add(Small("Saved \u2713", "AccentText"));
            if (p.RepliesInsight.Length > 0) buttons.Children.Add(Small("Analyzed \u2713", "AccentText"));
            if (p.YourLabel is not null) buttons.Children.Add(Small(p.YourLabel == "signal" ? "You: not noise" : "You: noise"));
            foreach (var t in buttons.Children.OfType<TextBlock>()) { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(8, 0, 0, 0); }
        }
        body.Children.Add(buttons);
        return new Border { Child = body, Padding = Res<Thickness>("PanelPadding"), CornerRadius = Res<CornerRadius>("SmallCorner"), Background = Res<Brush>("PanelBackground") };
    }

    /// <summary>Opens Grok on X (your own account, private, free) with a prompt about this post pre-filled.</summary>
    private string GrokLink(XItem p)
    {
        var prompt = $"Read this X post AND its replies: {p.Link} . Be brief, at most 6 bullets of under 20 words each: the gist; " +
                     $"credibility; how the replies reacted and the main pushback; the best insight from the replies (with @handle); " +
                     $"why it matters for someone focused on {_feed?.Interests ?? "startups, AI, Microsoft and product management"}; " +
                     "one suggested action if any.";
        return "https://x.com/i/grok?text=" + Uri.EscapeDataString(prompt);
    }

    private Button ActionsButton(XItem p)
    {
        var panel = new StackPanel { Spacing = 8, MinWidth = Res<double>("TouchTarget") * 4.5 };
        var flyout = new Flyout { Content = panel, Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Right };
        Button Big(string glyph, string text, string brush, Action onClick)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            content.Children.Add(new FontIcon { Glyph = glyph, FontSize = Font("FontIcon") });
            content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            var b = new Button
            {
                Content = content, FontSize = Font("FontBody"), Height = Res<double>("TouchTarget"), HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left, Padding = Res<Thickness>("PanelPadding"), CornerRadius = Res<CornerRadius>("SmallCorner"),
                Background = Res<Brush>(brush), Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            };
            b.Click += (_, _) => { flyout.Hide(); onClick(); };
            return b;
        }
        panel.Children.Add(Big(p.IsSignal ? "\uE7BA" : "\uE73E", p.IsSignal ? "Noise" : "Not noise", p.IsSignal ? "AlertBrush" : "TileEmerald", () => _feed!.Label(p.Id, !p.IsSignal)));
        panel.Children.Add(Big("\uE9D9", p.RepliesInsight.Length > 0 ? "Analysis" : $"Analyze ({p.Replies} replies)", "TileMauve", () => { _repliesFor = p.Id; RenderReplies(p); }));
        panel.Children.Add(Big("\uE8BD", "Ask Grok", "TileInk", () => Open(GrokLink(p))));
        panel.Children.Add(Big("\uE734", p.Saved ? "Saved \u2713" : "Save", "TileTeal", async () =>
        {
            if (p.Saved) return;
            var err = await Task.Run(() => _feed!.SaveAsync(p.Id));
            _note = err is null ? "Saved (bookmarked on X)" : $"Couldn't save: {err}";
            Render(true);
        }));
        panel.Children.Add(Big("\uE8F8", $"Unfollow @{p.Username}", "PanelBorder", () => { _confirmUnfollow = p.AuthorId; Render(true); }));
        panel.Children.Add(Big("\uE8A7", "Open on X", "PanelBorder", () => Open(p.Link)));

        var actions = new Button
        {
            Content = "Actions \u25BE", FontSize = Font("FontBody"), Height = Res<double>("TouchTarget"), Padding = new Thickness(28, 6, 28, 6),
            CornerRadius = Res<CornerRadius>("SmallCorner"), Background = Res<Brush>("TileCobalt"), Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            Flyout = flyout,
        };
        return actions;
    }

    private static void Open(string url) { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { } }

    private TextBlock Heading(string t) => new() { Text = t, FontSize = Font("FontTitle"), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private TextBlock Small(string t, string brush = "MutedText") => new() { Text = t, FontSize = Font("FontCaption"), Foreground = Res<Brush>(brush), TextWrapping = TextWrapping.Wrap };

    /// <summary>Briefing card + list builder.</summary>
    private void RenderSide()
    {
        SidePanel.Children.Clear();
        var s = _feed!.State;
        SidePanel.Children.Add(Heading("Briefing"));
        SidePanel.Children.Add(Small(s.PodcastStatus switch
        {
            "preparing" => "Preparing your briefing…",
            _ when s.PodcastStatus.StartsWith("failed") => $"Couldn't make it: {s.PodcastStatus[7..].Trim()}",
            "ready" when !s.PodcastCleared => $"{Math.Max(1, (int)Math.Round(s.PodcastSeconds / 60))} min · {s.PodcastPostIds.Count} posts · made {s.PodcastAt:ddd h:mm tt}. Tap Play briefing.",
            _ => _feed.PodcastCandidates > 0 ? $"{_feed.PodcastCandidates} signal posts. Play briefing makes one." : "Tap Update first.",
        }));

        SidePanel.Children.Add(Heading("List builder"));
        var cands = _feed.ListCandidates;
        var noisy = _feed.NoisyAccounts;
        SidePanel.Children.Add(Small($"{s.Accounts.Count} accounts seen in {s.Updates} update{(s.Updates == 1 ? "" : "s")}. " +
            $"{cands.Count} look like consistent signal; {noisy.Count} look like mostly noise. It gets sharper with each Update and your Noise / Not noise taps."));
        if (cands.Count > 0)
        {
            SidePanel.Children.Add(Small("For your SideCar list:", "AccentText"));
            foreach (var a in cands.Take(25))
                SidePanel.Children.Add(Small($"✓ {a.Name} @{a.Username}  ({a.Signal + a.YourSignal}/{a.Seen} signal)", "AccentText"));
            SidePanel.Children.Add(Small("Create list on X comes once there are enough (needs one more sign-in for the list permission)."));
        }
        if (noisy.Count > 0)
        {
            SidePanel.Children.Add(Small("Mostly noise (consider Unfollow):", "AlertBrush"));
            foreach (var a in noisy.Take(15))
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row.Children.Add(Small($"{a.Name} @{a.Username} ({a.Seen - a.Signal}/{a.Seen} noise)"));
                if (_confirmUnfollow == a.Id)
                {
                    row.Children.Add(Chip("Yes, unfollow", "AlertBrush", async () => { _confirmUnfollow = null; var err = await Task.Run(() => _feed.UnfollowAsync(a.Id)); _note = err is null ? $"Unfollowed @{a.Username}" : $"Couldn't unfollow: {err}"; Render(true); }));
                    row.Children.Add(Chip("Cancel", "PanelBorder", () => { _confirmUnfollow = null; Render(true); }));
                }
                else row.Children.Add(Chip("Unfollow", "PanelBorder", () => { _confirmUnfollow = a.Id; Render(true); }));
                SidePanel.Children.Add(row);
            }
        }
    }

    private async void RenderReplies(XItem p)
    {
        SidePanel.Children.Clear();
        SidePanel.Children.Add(Chip("\u2190 Back", "PanelBorder", () => { _repliesFor = null; RenderSide(); }));
        SidePanel.Children.Add(Heading($"Analysis: @{p.Username}"));
        SidePanel.Children.Add(new TextBlock { Text = p.Text, FontSize = Font("FontCaption"), Foreground = Res<Brush>("MutedText"), TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.WordEllipsis });
        var rows = new StackPanel { Spacing = 10 };
        SidePanel.Children.Add(rows);
        void Show(string text)
        {
            rows.Children.Clear();
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var colon = line.IndexOf(':');
                var label = colon is > 0 and < 16 ? line[..colon] : "";
                var tb = new TextBlock { FontSize = Font("FontBody"), TextWrapping = TextWrapping.Wrap };
                if (label.Length > 0)
                {
                    tb.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = label, FontWeight = FontWeights.SemiBold, Foreground = Res<Brush>("AccentText") });
                    tb.Inlines.Add(new Microsoft.UI.Xaml.Documents.LineBreak());
                    tb.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = line[(colon + 1)..].Trim() });
                }
                else tb.Text = line;
                rows.Children.Add(tb);
            }
        }
        if (p.RepliesInsight.Length > 0) { Show(p.RepliesInsight); SidePanel.Children.Add(Small($"Post and {p.Replies} replies analyzed {p.RepliesAt:ddd h:mm tt}")); return; }
        Show("Reading the post and up to 50 replies (about $0.25)\u2026");
        try { var text = await Task.Run(() => _feed!.AnalyzeRepliesAsync(p.Id)); if (_repliesFor == p.Id) Show(text); }
        catch (Exception ex) { Show($"Couldn't analyze: {ex.Message}"); }
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    // ---- Briefing bar (same behavior as LinkedIn) ----
    private Windows.Media.Playback.MediaPlayer? _player;
    private DateTime? _playerEpisode;
    private DispatcherTimer? _clock;
    private bool _listening;

    private Button BarButton(string glyph, string text, string brush, Action onClick)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = Font("FontIcon") });
        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button
        {
            Content = content, FontSize = Font("FontBody"), Height = Res<double>("TouchTarget"), Padding = Res<Thickness>("PanelPadding"),
            CornerRadius = Res<CornerRadius>("SmallCorner"), Background = Res<Brush>(brush), Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
        };
        AutomationProperties.SetName(b, text);
        b.Click += (_, _) => onClick();
        return b;
    }

    private bool EpisodeReady => _feed?.State is { PodcastStatus: "ready", PodcastCleared: false } && File.Exists(_feed!.PodcastPath);
    private bool Playing => _player?.PlaybackSession.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing;

    private void RenderBriefingBar()
    {
        BriefingBar.Children.Clear();
        if (_feed is null) return;
        var s = _feed.State;
        if (!(_player is not null && _playerEpisode == s.PodcastAt && EpisodeReady))
        {
            BriefingBar.Children.Add(s.PodcastStatus == "preparing" ? BarButton("\uE916", "Preparing\u2026", "PanelBorder", () => { })
                : s.PodcastStatus.StartsWith("failed") ? BarButton("\uE7BA", "Retry briefing", "AlertBrush", () => _ = PlayAsync())
                : BarButton("\uE768", "Play briefing", "TileCobalt", () => _ = PlayAsync()));
            if (s.PodcastStatus.StartsWith("failed")) SubtitleText.Text = $"Couldn't make the briefing: {s.PodcastStatus[7..].Trim()}";
            return;
        }
        BriefingBar.Children.Add(Playing ? BarButton("", "Pause", "TileCobalt", () => { _player!.Pause(); RenderBriefingBar(); })
                                         : BarButton("", "Play", "TileCobalt", () => { _player!.Play(); RenderBriefingBar(); }));
        BriefingBar.Children.Add(BarButton("", _listening ? "Listening…" : "Interrupt", "TileAmber", () => _ = InterruptAsync()));
        BriefingBar.Children.Add(BarButton("", "Done", "TileEmerald", () => { _player?.Pause(); _player?.Dispose(); _player = null; _clock?.Stop(); _note = $"Cleared {_feed.ClearPodcastPosts()} posts from the feed"; Render(false); }));
        var pos = _player!.PlaybackSession.Position; var len = _player.PlaybackSession.NaturalDuration;
        BriefingBar.Children.Add(new TextBlock { Text = $"{(int)pos.TotalMinutes}:{pos.Seconds:00} / {(int)len.TotalMinutes}:{len.Seconds:00}", FontSize = Font("FontCaption"), Foreground = Res<Brush>("MutedText"), VerticalAlignment = VerticalAlignment.Center });
    }

    private async Task PlayAsync()
    {
        if (_feed is null) return;
        if (!EpisodeReady || (_feed.PodcastHasNews && !(_player is not null && _playerEpisode == _feed.State.PodcastAt && _player.PlaybackSession.Position > TimeSpan.Zero)))
        {
            _feed.State.PodcastStatus = "preparing"; RenderBriefingBar();
            var err = await Task.Run(() => _feed.PreparePodcastAsync());
            if (err is not null) { _note = err; Render(true); return; }
        }
        if (_player is null || _playerEpisode != _feed.State.PodcastAt)
        {
            _player?.Dispose();
            _player = new Windows.Media.Playback.MediaPlayer { Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(_feed.PodcastPath)) };
            _player.MediaEnded += (_, _) => DispatcherQueue.TryEnqueue(RenderBriefingBar);
            _playerEpisode = _feed.State.PodcastAt;
        }
        _player.Play();
        _clock ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick -= Tick; _clock.Tick += Tick; _clock.Start();
        Render(true);
    }

    private void Tick(object? sender, object e) { if (Visibility == Visibility.Visible) RenderBriefingBar(); }

    private async Task InterruptAsync()
    {
        if (_player is null || _feed?.Voice is null || _listening) return;
        var position = _player.PlaybackSession.Position.TotalSeconds;
        _player.Pause(); _listening = true;
        _note = "Listening… say what to follow up on"; UpdateSubtitle(); RenderBriefingBar();
        try
        {
            var said = await _feed.Voice.ListenOnceAsync(partial => DispatcherQueue.TryEnqueue(() => { _note = $"“{partial}”"; UpdateSubtitle(); }), CancellationToken.None);
            _note = string.IsNullOrWhiteSpace(said) ? "Didn't catch that" : await _feed.InterruptAsync(said, position);
        }
        catch (Exception ex) { _note = $"Couldn't save that: {ex.Message}"; }
        _listening = false;
        _player.Play(); // resume right away
        Render(true);
    }
}
