using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SideCarMonitor.Services;

namespace SideCarMonitor.Controls;

/// <summary>The Ideas tile's page: one ranked list of PMCollab opportunities and wild ideas.</summary>
public sealed partial class IdeasPage : UserControl
{
    private IdeaRadar? _radar;

    public event Action? HomeRequested;

    public IdeasPage() => InitializeComponent();

    public void Initialize(IdeaRadar? radar) => _radar = radar;

    public void Show() => Render(keepScroll: false);

    private void Render(bool keepScroll)
    {
        var offset = ListScroll.VerticalOffset;
        IdeasPanel.Children.Clear();
        IdeaCards.Render(IdeasPanel, _radar, () => Render(keepScroll: true));
        if (keepScroll) ListScroll.ChangeView(null, offset, null, disableAnimation: true);
    }

    private void Home_Click(object sender, RoutedEventArgs e) => HomeRequested?.Invoke();
}
