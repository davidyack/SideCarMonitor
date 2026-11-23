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

    public static string TasksPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SideCarMonitor", "tasks.json");

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Headless daily jobs (Windows Task Scheduler runs "SideCarMonitor.exe --jobs"): rollover + brief, then exit.
        if (Environment.GetCommandLineArgs().Contains("--jobs", StringComparer.OrdinalIgnoreCase))
        {
            var log = Path.Combine(Path.GetDirectoryName(TasksPath)!, "jobs.log");
            try
            {
                var service = new SideCarTasks.TaskService(new SideCarTasks.TaskStore(TasksPath));
                var report = new SideCarTasks.TaskJobs(service).RunDue();
                File.AppendAllText(log, $"{DateTimeOffset.Now:u} jobs ok: moved {report.Moved}, brief {(report.Brief is null ? "none" : "written")}\n");
            }
            catch (Exception ex)
            {
                File.AppendAllText(log, $"{DateTimeOffset.Now:u} jobs failed: {ex.Message}\n");
            }
            Exit();
            return;
        }

        var (settings, createdNew) = AppSettings.Load();
        UiScale.Apply(Resources, settings.Display.UiScale);
        _window = new MainWindow(settings, createdNew);
        _window.Activate();
    }
}
