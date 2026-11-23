using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SideCarMonitor.Models;
using SideCarMonitor.Services;

namespace SideCarMonitor.Controls;

/// <summary>
/// The Activity page: the whole-house feed (camera events, locks/doors, automations, SideCar's actions) with
/// filters on the left, and the tapped event's pictures and details on the right.
/// </summary>
public sealed partial class ActivityPage : UserControl
{
    private ObservableCollection<MotionEvent>? _all;
    private readonly ObservableCollection<MotionEvent> _shown = [];
    private string _filter = "all";
    private MotionEvent? _selected;

    public event Action? HomeRequested;
    /// <summary>Camera index to show live.</summary>
    public event Action<int>? WatchLiveRequested;

    public ActivityPage()
    {
        InitializeComponent();
        FeedList.ItemsSource = _shown;
    }

    public void Initialize(ObservableCollection<MotionEvent> events)
    {
        _all = events;
        events.CollectionChanged += OnEventsChanged;
        Rebuild();
    }

    public void Show() => Rebuild();

    private void OnEventsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // New events arrive at the top; keep it cheap and keep the selection.
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewStartingIndex == 0 && e.NewItems is { } items)
        {
            foreach (MotionEvent ev in items) if (Matches(ev)) _shown.Insert(0, ev);
            UpdateSubtitle();
        }
        else Rebuild();
    }

    private bool Matches(MotionEvent ev) => _filter switch
    {
        "camera" => ev.IsCamera,
        "door" => ev.Source == "door",
        "house" => ev.Source is "house" or "sidecar",
        "system" => ev.Source is "system" or "sidecar",
        _ => true,
    };

    private void Rebuild()
    {
        _shown.Clear();
        foreach (var ev in _all ?? []) if (Matches(ev)) _shown.Add(ev);
        foreach (var b in Filters.Children.OfType<Button>())
            b.Background = (Brush)Application.Current.Resources[(string)b.Tag == _filter ? "TileCobalt" : "PanelBackground"];
        UpdateSubtitle();
    }

    private void UpdateSubtitle()
    {
        EmptyText.Visibility = _shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var today = (_all ?? []).Count(e => e.Start.Date == DateTime.Today);
        var alerts = (_all ?? []).Count(e => e.Start.Date == DateTime.Today && e.Alert);
        SubtitleText.Text = $"{today} today" + (alerts > 0 ? $" · {alerts} worth a look" : "");
    }

    private void Filter_Click(object sender, RoutedEventArgs e)
    {
        _filter = (string)((Button)sender).Tag;
        Rebuild();
    }

    private void Home_Click(object sender, RoutedEventArgs e) => HomeRequested?.Invoke();

    private void WatchLive_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is { IsCamera: true, CameraIndex: >= 0 } ev) WatchLiveRequested?.Invoke(ev.CameraIndex);
    }

    private void FeedList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not MotionEvent ev) return;
        _selected = ev;
        PickText.Visibility = Visibility.Collapsed;
        DetailScroll.Visibility = Visibility.Visible;
        DetailTitle.Text = $"{ev.Label} · {ev.CameraName}";
        DetailWhen.Text = ev.IsCamera
            ? $"{ev.Start:dddd h:mm:ss tt} · lasted {ev.DurationText}"
            : $"{ev.Start:dddd h:mm:ss tt}" + (ev.Detail.Length > 0 ? $" · {ev.Detail}" : "");
        DetailAi.Text = ev.IsCamera ? (ev.VerdictText.Length > 0 ? $"AI: {ev.VerdictText}" : "") : (ev.Alert ? "Worth a look" : "");
        WatchLiveButton.Visibility = ev.IsCamera && ev.CameraIndex >= 0 ? Visibility.Visible : Visibility.Collapsed;

        DetailPictures.Children.Clear();
        foreach (var n in ev.Snapshots)
        {
            if (EventStore.SnapshotPath(ev.Id, n) is not { } path) continue;
            DetailPictures.Children.Add(new Image
            {
                Source = new BitmapImage(new Uri(path)) { DecodePixelWidth = 1280 },
                Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left,
            });
            DetailPictures.Children.Add(new TextBlock
            {
                Text = n == 1 ? "When it started" : $"When it was seen as a {ev.Label.ToLowerInvariant()}",
                FontSize = (double)Application.Current.Resources["FontCaption"],
                Foreground = (Brush)Application.Current.Resources["MutedText"],
            });
        }
        if (ev.IsCamera && ev.Snapshots.Count == 0)
            DetailPictures.Children.Add(new TextBlock { Text = "No picture was saved for this event", Foreground = (Brush)Application.Current.Resources["MutedText"] });
    }
}
