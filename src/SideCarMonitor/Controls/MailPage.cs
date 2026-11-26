using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SideCarMonitor.Mail;
using SideCarMonitor.Services;

namespace SideCarMonitor.Controls;

/// <summary>
/// The Mail tile's page (built in code): what was kept in the Inbox for you, then each "#  SideCar" folder with a
/// one-line summary per message, Open (Outlook on the web) and Back to Inbox. Play briefing voices the digest.
/// </summary>
public sealed partial class MailPage : UserControl
{
    private static T Res<T>(string key) => (T)Application.Current.Resources[key];
    /// <summary>Size factor: 1 on the kiosk; smaller in the desk window on the main display (resources are sized for the 7" screen).</summary>
    private readonly double _k;
    private double Font(string key) => Res<double>(key) * _k;
    private Thickness Pad(string key) { var p = Res<Thickness>(key); return new Thickness(p.Left * _k, p.Top * _k, p.Right * _k, p.Bottom * _k); }

    private MailTriage? _mail;
    private readonly StackPanel _list = new() { Spacing = 8 };
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _subtitle = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly StackPanel _bar = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly Dictionary<string, string> _status = [];
    private Windows.Media.Playback.MediaPlayer? _player;
    private DateTime? _playerEpisode;
    private DispatcherTimer? _clock;
    private string _note = "";

    public event Action? HomeRequested;

    public MailPage() : this(1) { }

    /// <summary>Set when the page lives in its own window (Home becomes Close).</summary>
    public bool InWindow { get; set; }

    public MailPage(double scale)
    {
        _k = scale;
        _subtitle.FontSize = Font("FontCaption");
        _subtitle.Foreground = Res<Brush>("MutedText");
        var root = new Grid { Background = Res<Brush>("KioskBackground"), Padding = Pad("PagePadding"), RowSpacing = Res<double>("Gap") * _k };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Grid { ColumnSpacing = Res<double>("Gap") * _k };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(Btn("", "Home", "PanelBorder", () => { StopPlayer(); HomeRequested?.Invoke(); }));
        Loaded += (_, _) =>
        {
            if (!InWindow) return;
            header.Children[0].Visibility = Visibility.Collapsed; // the window has its own close button
            _list.MaxWidth = 2600; _list.HorizontalAlignment = HorizontalAlignment.Center; // readable line length on a big monitor
            _bar.MaxWidth = 2600; header.MaxWidth = 2600;
        };
        Unloaded += (_, _) => StopPlayer();
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = "Mail", FontSize = Font("FontTitle"), FontWeight = FontWeights.SemiBold });
        titles.Children.Add(_subtitle);
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);
        root.Children.Add(header);

        Grid.SetRow(_bar, 1);
        root.Children.Add(_bar);

        _scroll = new ScrollViewer { Content = _list };
        Grid.SetRow(_scroll, 2);
        root.Children.Add(_scroll);
        Content = root;
    }

    public void Initialize(MailTriage? mail) => _mail = mail;

    public void Show() => Render(keepScroll: false);

    private string? _playingId;
    private string? _undoId;

    public void Render(bool keepScroll = true)
    {
        var offset = _scroll.VerticalOffset;
        _list.Children.Clear();
        RenderBar();
        if (_mail is null)
        {
            _subtitle.Text = "Mail isn't set up: sign in with SideCarMonitor.exe --mail-login CTC";
            return;
        }
        var d = _mail.Data;
        _subtitle.Text = (d.Trial ? "TRIAL: nothing is moved yet · " : "") +
                         (_mail.Busy ? (_mail.Progress.Length > 0 ? _mail.Progress : "Sorting new mail\u2026") : d.LastRun is { } at ? $"Checked {at:h:mm tt} · {d.LastResult}" : "Not run yet") +
                         " · CTC, hourly · one digest per run · " + _mail.CatchUpStatus;
        if (_note.Length > 0)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(Caption(_note, "AccentText"));
            if (_undoId is { } undo)
                row.Children.Add(Btn("\uE7A7", "Undo", "PanelBorder", () => { _mail.Archive(undo, false); _undoId = null; _note = ""; Render(); }));
            else if (_undoAll is { } all)
                row.Children.Add(Btn("\uE7A7", "Undo", "PanelBorder", () => { _mail.Unarchive(all); _undoAll = null; _note = ""; Render(); }));
            _list.Children.Add(row);
        }
        var digests = _mail.OpenDigests();
        if (digests.Count == 0) _list.Children.Add(Caption("All caught up: no digests waiting. A new one is made after each hourly run that sorts mail.", "MutedText"));
        // Newest digest open; the rest collapsed to one line each (tap to open).
        if (!digests.Any(g => _open.Contains(g.Id)) && digests.Count > 0) _open.Add(digests[0].Id);
        foreach (var g in digests) _list.Children.Add(DigestCard(g, _open.Contains(g.Id)));
        if (keepScroll) _scroll.ChangeView(null, offset, null, disableAnimation: true);
    }

    private readonly HashSet<string> _open = [];
    private List<string>? _undoAll;

    private Border DigestCard(MailTriage.Digest g, bool open)
    {
        var body = new StackPanel { Spacing = 6 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        head.Children.Add(new FontIcon { Glyph = open ? "\uE70D" : "\uE76C", FontSize = Font("FontCaption"), VerticalAlignment = VerticalAlignment.Center });
        head.Tapped += (_, _) => { if (!_open.Remove(g.Id)) _open.Add(g.Id); Render(); };
        head.Children.Add(new TextBlock
        {
            Text = (g.Title.Length > 0 ? g.Title + "  ·  " : "") + $"{g.At.LocalDateTime:ddd h:mm tt}  ·  {g.Sorted} sorted",
            FontSize = Font("FontBody"), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
        });
        body.Children.Add(head);
        if (!open) body.Children.Add(Caption(_mail!.FolderCounts(g), "MutedText"));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var mine = _playingId == g.Id && _player is not null;
        if (g.Status == "writing") buttons.Children.Add(Caption("Writing the digest\u2026", "MutedText"));
        else if (g.Status.StartsWith("failed"))
            buttons.Children.Add(Btn("\uE72C", "Retry digest", "AlertBrush", () => { var m = _mail!; _ = Task.Run(() => m.RetryDigestAsync(g.Id)); }));
        else if (mine)
        {
            buttons.Children.Add(Playing ? Btn("\uE769", "Pause", "TileCobalt", () => { _player!.Pause(); Render(); })
                                         : Btn("\uE768", "Play", "TileCobalt", () => { _player!.Play(); Render(); }));
            var pos = _player!.PlaybackSession.Position; var len = _player.PlaybackSession.NaturalDuration;
            buttons.Children.Add(_timeText = Caption($"{(int)pos.TotalMinutes}:{pos.Seconds:00} / {(int)len.TotalMinutes}:{len.Seconds:00}", "MutedText"));
        }
        else if (_mail!.AudioReady(g)) buttons.Children.Add(Btn("\uE768", "Play", "TileCobalt", () => Play(g)));
        buttons.Children.Add(Btn("\uE7B8", "Archive", "TileEmerald", () =>
        {
            if (_playingId == g.Id) StopPlayer();
            _mail!.Archive(g.Id);
            _undoAll = null; _undoId = g.Id; _note = $"Archived the {g.At.LocalDateTime:h:mm tt} digest.";
            Render();
        }));
        body.Children.Add(buttons);

        // The digest as section cards (a colored stripe and heading per folder, one tappable point per line); two across in
        // the desk window, balanced by length. Kept-for-you parts (older digests) are skipped: that mail is still in the Inbox.
        var cards = new List<(Border Card, int Weight)>();
        var heading = "Kept for you";
        foreach (var block in (open ? g.Text : "").Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var m = System.Text.RegularExpressions.Regex.Match(lines[0], @"^(Kept for you|Dev|Lists|Newsletters|Updates|Invites|Orders(?: & Receipts| and receipts)?|Notifications|Sales|Suspicious)\s*[.:]\s*", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var nl = System.Text.RegularExpressions.Regex.Match(lines[0], @"^Newsletter:\s*(.+?)\.?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (nl.Success) { heading = "Newsletter \u00b7 " + nl.Groups[1].Value; lines[0] = ""; m = nl; } // each newsletter its own card
            else if (m.Success) { heading = m.Groups[1].Value; lines[0] = lines[0][m.Length..]; }
            if (heading.Equals("Kept for you", StringComparison.OrdinalIgnoreCase)) continue;
            var points = new List<(string Text, List<MailTriage.Item> Items)>();
            foreach (var line in lines.Where(l => l.Length > 0))
            {
                if (line.StartsWith('[')) points.Add(_mail!.ParseLine(g, line));
                else points.AddRange(System.Text.RegularExpressions.Regex.Split(line, @"(?<=[.!?])\s+(?=[A-Z0-9""])")
                    .Where(s => s.Trim().Length > 0).Select(s => (s.Trim(), new List<MailTriage.Item>()))); // older digests: untagged sentences
            }
            if (points.Count == 0) continue;
            cards.Add((SectionCard(heading, !m.Success, points), block.Length + 200));
        }
        if (InWindow && cards.Count > 1)
        {
            var grid = new Grid { ColumnSpacing = Res<double>("Gap") * _k };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var cols = new[] { new StackPanel { Spacing = 8 * _k }, new StackPanel { Spacing = 8 * _k } };
            var load = new int[2];
            foreach (var (card, weight) in cards) { var c = load[0] <= load[1] ? 0 : 1; cols[c].Children.Add(card); load[c] += weight; }
            Grid.SetColumn(cols[1], 1);
            grid.Children.Add(cols[0]); grid.Children.Add(cols[1]);
            body.Children.Add(grid);
        }
        else foreach (var (card, _) in cards) body.Children.Add(card);

        return new Border { Background = Res<Brush>("PanelBackground"), Padding = Pad("PanelPadding"), CornerRadius = Res<CornerRadius>("SmallCorner"), Child = body };
    }

    private static readonly Dictionary<string, string> SectionBrush = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Kept for you"] = "TileAmber", ["Dev"] = "TileCobalt", ["Lists"] = "TileTeal", ["Newsletters"] = "TileMauve", ["Updates"] = "TileSteel",
        ["Invites"] = "TileEmerald", ["Notifications"] = "TileSky", ["Sales"] = "TileCrimson", ["Suspicious"] = "AlertBrush",
    };

    private Border SectionCard(string heading, bool continued, List<(string Text, List<MailTriage.Item> Items)> points)
    {
        var brush = Res<Brush>(SectionBrush.GetValueOrDefault(heading, heading.StartsWith("Orders", StringComparison.OrdinalIgnoreCase) ? "TileEmerald"
            : heading.StartsWith("Newsletter", StringComparison.OrdinalIgnoreCase) ? "TileMauve" : "TileSteel"));
        var size = Font("FontCaption");
        var stack = new StackPanel { Spacing = size * 0.5 }; // with 1.5 line height: items sit 1.5 lines apart
        if (!continued)
            stack.Children.Add(new TextBlock { Text = heading.ToUpperInvariant(), FontSize = size, FontWeight = FontWeights.Bold, Foreground = brush, CharacterSpacing = 60 });
        foreach (var (text, items) in points)
        {
            var row = new Grid { ColumnSpacing = 10 * _k, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Padding = new Thickness(4 * _k, 2 * _k, 4 * _k, 2 * _k), CornerRadius = new CornerRadius(4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock { Text = "\u2022", FontSize = size, Foreground = brush });
            var tb = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, LineHeight = size * 1.5, Opacity = 0.92 };
            if (items.Count > 1) tb.Text += $"  ({items.Count})";
            Grid.SetColumn(tb, 1);
            row.Children.Add(tb);
            var links = items.Where(i => i.WebLink.Length > 0).ToList();
            if (links.Count > 0)
            {
                // Tap a line to open its message; a merged line offers its messages to pick from.
                ToolTipService.SetToolTip(row, links.Count == 1 ? $"{links[0].From}: {links[0].Subject}" : $"{links.Count} messages");
                var hover = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 255, 255, 255));
                row.PointerEntered += (_, _) => { row.Background = hover; tb.Opacity = 1; };
                row.PointerExited += (_, _) => { row.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent); tb.Opacity = 0.92; };
                row.Tapped += (_, _) =>
                {
                    if (links.Count == 1) { _ = Windows.System.Launcher.LaunchUriAsync(new Uri(links[0].WebLink)); return; }
                    var menu = new MenuFlyout();
                    foreach (var i in links)
                    {
                        var mi = new MenuFlyoutItem { Text = $"{i.From}: {i.Subject}", FontSize = size };
                        mi.Click += (_, _) => _ = Windows.System.Launcher.LaunchUriAsync(new Uri(i.WebLink));
                        menu.Items.Add(mi);
                    }
                    menu.ShowAt(row);
                };
            }
            stack.Children.Add(row);
        }
        return new Border
        {
            Background = Res<Brush>("KioskBackground"), BorderBrush = brush, BorderThickness = new Thickness(5 * _k + 2, 0, 0, 0),
            CornerRadius = Res<CornerRadius>("SmallCorner"), Padding = Pad("PanelPadding"), Child = stack,
        };
    }

    private Border Card(MailTriage.Item i)
    {
        var body = new StackPanel { Spacing = 3 };
        body.Children.Add(new TextBlock
        {
            Text = $"{i.From}  ·  {i.Received.LocalDateTime:ddd h:mm tt}" + (i.Flag ? "  ·  flagged" : "") + (i.ReturnedToInbox ? "  ·  back in Inbox" : "") +
                   (i.Outcome == "Inbox" ? $"  ·  {i.Why}" : ""),
            FontSize = Font("FontCaption"), Foreground = Res<Brush>(i.Flag ? "TileAmber" : "MutedText"), TextTrimming = TextTrimming.CharacterEllipsis,
        });
        body.Children.Add(new TextBlock { Text = i.Subject, FontSize = Font("FontBody"), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        body.Children.Add(new TextBlock { Text = i.Summary.Length > 0 ? i.Summary : i.Why, FontSize = Font("FontCaption"), TextWrapping = TextWrapping.Wrap });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        if (i.WebLink.Length > 0)
            buttons.Children.Add(Btn("\uE8A7", "Open", "TileCobalt", () => _ = Windows.System.Launcher.LaunchUriAsync(new Uri(i.WebLink))));
        if (i.Outcome != "Inbox" && !i.ReturnedToInbox && _mail is { Data.Trial: false })
            buttons.Children.Add(Btn("\uE72B", "Back to Inbox", "TileEmerald", () =>
            {
                var mail = _mail; var id = i.Id;
                _status[id] = "Moving\u2026"; Render();
                _ = Task.Run(async () =>
                {
                    var error = await mail.ReturnToInboxAsync(id);
                    DispatcherQueue.TryEnqueue(() => { _status.Remove(id); if (error is not null) _note = "Couldn't move it back: " + error; Render(); });
                });
            }));
        if (_status.TryGetValue(i.Id, out var s)) buttons.Children.Add(Caption(s, "MutedText"));
        if (buttons.Children.Count > 0) body.Children.Add(buttons);
        return new Border { Background = Res<Brush>("KioskBackground"), Padding = Pad("PanelPadding"), CornerRadius = Res<CornerRadius>("SmallCorner"), Child = body };
    }

    // ---- Header: Run now, Go live ----

    private bool Playing => _player?.PlaybackSession.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing;

    private void RenderBar()
    {
        _bar.Children.Clear();
        if (_mail is null) return;
        _bar.Children.Add(Btn("\uE72C", _mail.Busy ? (_mail.Progress.Length > 0 ? _mail.Progress : "Sorting\u2026") : "Run now", "PanelBorder", () =>
        {
            if (_mail.Busy) return;
            var mail = _mail;
            _ = Task.Run(() => mail.RunAsync(onDemand: true));
            Render();
        }));
        if (_mail.CatchingUp)
            _bar.Children.Add(Btn("\uE71A", "Stop catch-up", "AlertBrush", () => { _mail.StopCatchUp(); Render(); }));
        var openCount = _mail.OpenDigests().Count;
        if (openCount > 1)
            _bar.Children.Add(Btn("\uE7B8", $"Archive all ({openCount})", "TileEmerald", () =>
            {
                StopPlayer();
                _undoAll = _mail.ArchiveAll(); _undoId = null;
                _note = $"Archived {_undoAll.Count} digests.";
                Render();
            }));
        if (_mail.Data.Trial)
            _bar.Children.Add(Btn("\uE73E", "Go live: start moving mail", "TileEmerald", () =>
            {
                _mail.GoLive();
                _undoId = null;
                _note = "Live: new mail is moved into #  SideCar from now on (the next pass runs within a couple of minutes).";
                Render();
            }));
    }

    private void Play(MailTriage.Digest g)
    {
        if (_mail is null || !_mail.AudioReady(g)) return;
        StopPlayer();
        var key = $"mail:{g.Id}";
        _player = new Windows.Media.Playback.MediaPlayer { Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(_mail.AudioPath(g))) };
        _player.MediaEnded += (_, _) => DispatcherQueue.TryEnqueue(() => { StopPlayer(); Render(); });
        _playingId = g.Id;
        PlaybackMemory.Track(_player, key);
        if (PlaybackMemory.Get(key) is { } at) { _note = $"Picking up where you left off ({(int)at.TotalMinutes}:{at.Seconds:00})"; _undoId = null; }
        _player.Play();
        _clock ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick -= Tick; _clock.Tick += Tick; _clock.Start();
        Render();
    }

    private TextBlock? _timeText;

    /// <summary>Once a second while playing: just the time (re-rendering the page would jump the list).</summary>
    private void Tick(object? sender, object e)
    {
        if (_player is null) { _clock?.Stop(); return; }
        var pos = _player.PlaybackSession.Position; var len = _player.PlaybackSession.NaturalDuration;
        if (_timeText is not null) _timeText.Text = $"{(int)pos.TotalMinutes}:{pos.Seconds:00} / {(int)len.TotalMinutes}:{len.Seconds:00}";
    }

    private void StopPlayer()
    {
        _player?.Pause();
        _player?.Dispose();
        _player = null;
        _playingId = null;
        _clock?.Stop();
    }

    // ---- Small builders ----

    private TextBlock Section(string text) => new()
    {
        Text = text, FontSize = Font("FontBody"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0), Foreground = Res<Brush>("AccentText"),
    };

    private TextBlock Caption(string text, string brush) => new()
    {
        Text = text, FontSize = Font("FontCaption"), Foreground = Res<Brush>(brush), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
    };

    private Button Btn(string glyph, string text, string brush, Action onClick)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = Font("FontIcon") });
        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button
        {
            Content = content, FontSize = Font("FontBody"), Height = Res<double>("TouchTarget") * _k, Padding = Pad("PanelPadding"),
            CornerRadius = Res<CornerRadius>("SmallCorner"), Background = Res<Brush>(brush), Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
        };
        AutomationProperties.SetName(b, text);
        b.Click += (_, _) => onClick();
        return b;
    }
}
