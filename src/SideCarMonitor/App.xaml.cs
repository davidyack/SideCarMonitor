using Microsoft.UI.Xaml;
using SideCarMonitor.Models;

namespace SideCarMonitor;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        LibVLCSharp.Shared.Core.Initialize();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var (settings, createdNew) = AppSettings.Load();
        UiScale.Apply(Resources, settings.Display.UiScale);
        _window = new MainWindow(settings, createdNew);
        _window.Activate();
    }
}
