using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SideCarMonitor.Services;

namespace SideCarMonitor.Controls;

/// <summary>The Ideas tab (shared by the LinkedIn and X pages): All / PMCollab / Wild, best first, with Make to-do and Not useful.</summary>
public static class IdeaCards
{
    private static T Res<T>(string key) => (T)Application.Current.Resources[key];
    private static double Font(string key) => Res<double>(key);

    public static string Filter { get; set; } = "all";
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Status = new();

    public static int Count(IdeaRadar? radar) => radar?.Ranked().Count(i => i.Status == "new") ?? 0;

    public static void Render(StackPanel panel, IdeaRadar? radar, Action rerender)
    {
        if (radar is null)
        {
            panel.Children.Add(Note("The idea radar is off: set assistant.pmcollabProfile in settings."));
            return;
        }
        panel.Children.Add(Note(radar.Busy ? "Reading your feeds for ideas…"
            : radar.Data.LastRun is { } at ? $"Last looked {at:ddd h:mm tt} · {radar.Data.LastResult}. New feed reads are checked automatically."
            : "Looking through your stored posts for the first time…"));

        var ideas = radar.Ranked().Take(40).ToList();
        if (ideas.Count == 0 && !radar.Busy) panel.Children.Add(Note("No ideas yet."));
        var rank = 0;
        foreach (var idea in ideas) panel.Children.Add(Card(radar, idea, ++rank, rerender));
    }

    private static Border Card(IdeaRadar radar, IdeaRadar.Idea idea, int rank, Action rerender)
    {
        var body = new StackPanel { Spacing = 4 };
        body.Children.Add(new TextBlock
        {
            Text = $"#{rank}  {idea.KindLabel}  ·  score {idea.Score}" + (idea.Effort.Length > 0 ? $"  ·  effort {idea.Effort}" : "") +
                   $"  ·  {idea.Evidence.Select(e => e.Author).Distinct().Count()} source(s)" + (idea.Status == "todo" ? "  ·  on your to-do list" : ""),
            FontSize = Font("FontCaption"), Foreground = Res<Brush>(idea.IsPmCollab ? "AccentText" : "TileAmber"),
        });
        body.Children.Add(new TextBlock { Text = idea.Title, FontSize = Font("FontBody"), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = idea.Pitch, FontSize = Font("FontCaption"), TextWrapping = TextWrapping.Wrap });
        void Line(string label, string text)
        {
            if (text.Length == 0) return;
            var t = new TextBlock { FontSize = Font("FontCaption"), TextWrapping = TextWrapping.Wrap };
            t.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = label + ": ", Foreground = Res<Brush>("MutedText") });
            t.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = text });
            body.Children.Add(t);
        }
        Line(idea.IsPmCollab ? "The pain" : "Why now", idea.Why);
        Line("Who'd pay", idea.WhoPays);
        Line("Smallest version", idea.Smallest);
        foreach (var e in idea.Evidence.Take(3))
            body.Children.Add(new TextBlock
            {
                Text = $"“{e.Quote}” — {e.Author} ({(e.Source == "x" ? "X" : "LinkedIn")}, {e.At:MMM d})",
                FontSize = Font("FontCaption"), Foreground = Res<Brush>("MutedText"), TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.WordEllipsis,
            });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        if (idea.Status != "todo")
            buttons.Children.Add(Button("Make to-do", "TileEmerald", () => { radar.MakeTask(idea.Id); rerender(); }));
        if (radar.PmCollab is not null)
        {
            if (idea.SignalId is null)
                buttons.Children.Add(Button("Send to PMCollab", "TileCobalt", () =>
                {
                    Status[idea.Id] = "Sending\u2026"; rerender();
                    _ = Task.Run(async () =>
                    {
                        var error = await radar.SendToPmCollabAsync(idea.Id);
                        Status[idea.Id] = error ?? "In your PMCollab Brain";
                    });
                }));
            else
                body.Children.Add(new TextBlock
                {
                    Text = idea.SignalId == "unknown" ? "PMCollab: not sure it arrived; check your Brain" : idea.SignalId == "sending" ? "Sending to PMCollab\u2026" : $"In your PMCollab Brain \u2713 ({idea.SignalAt:MMM d})",
                    FontSize = Font("FontCaption"), Foreground = Res<Brush>("AccentText"),
                });
        }
        if (Status.TryGetValue(idea.Id, out var status) && idea.SignalId is null)
            body.Children.Add(new TextBlock { Text = status, FontSize = Font("FontCaption"), Foreground = Res<Brush>("AlertBrush"), TextWrapping = TextWrapping.Wrap });
        buttons.Children.Add(Button("Not useful", "PanelBorder", () => { radar.Dismiss(idea.Id); rerender(); }));
        if (idea.Evidence.FirstOrDefault(e => e.Link.Length > 0) is { } src)
            buttons.Children.Add(Button("Source post", "PanelBorder", () =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(src.Link) { UseShellExecute = true }); } catch { }
            }));
        body.Children.Add(buttons);

        return new Border
        {
            Child = body, Padding = Res<Thickness>("PanelPadding"), CornerRadius = Res<CornerRadius>("SmallCorner"),
            Background = Res<Brush>("PanelBackground"),
        };
    }

    private static TextBlock Note(string text) =>
        new() { Text = text, FontSize = Font("FontCaption"), Foreground = Res<Brush>("MutedText"), TextWrapping = TextWrapping.Wrap };

    private static Button Button(string text, string brush, Action onClick)
    {
        var b = new Button
        {
            Content = text, FontSize = Font("FontCaption"), MinHeight = Res<double>("TouchTarget"), Padding = new Thickness(18, 6, 18, 6),
            CornerRadius = Res<CornerRadius>("SmallCorner"), Background = Res<Brush>(brush), Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
        };
        b.Click += (_, _) => onClick();
        return b;
    }
}
