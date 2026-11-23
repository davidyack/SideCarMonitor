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

        // LinkedIn setup: "--linkedin-login" opens the dedicated Chrome profile for you to sign in once;
        // "--linkedin-test <out.json>" reads the feed once and writes what it found (for checking the reader).
        var cl = Environment.GetCommandLineArgs();
        if (cl.Contains("--linkedin-login", StringComparer.OrdinalIgnoreCase) || cl.Contains("--linkedin-test", StringComparer.OrdinalIgnoreCase))
        {
            _ = Task.Run(async () =>
            {
                var log = Path.Combine(Path.GetDirectoryName(TasksPath)!, "linkedin.log");
                try
                {
                    if (cl.Contains("--linkedin-login", StringComparer.OrdinalIgnoreCase))
                        File.AppendAllText(log, $"{DateTimeOffset.Now:u} login {(await Services.LinkedInReader.LoginAsync() ? "ok" : "not completed")}\n");
                    else
                    {
                        var posts = await Services.LinkedInReader.ReadFeedAsync();
                        var outPath = cl.SkipWhile(a => !a.Equals("--linkedin-test", StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault()
                                      ?? Path.Combine(Path.GetTempPath(), "linkedin-test.json");
                        File.WriteAllText(outPath, System.Text.Json.JsonSerializer.Serialize(posts, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                        File.AppendAllText(log, $"{DateTimeOffset.Now:u} test read {posts.Count} posts\n");
                    }
                }
                catch (Exception ex)
                {
                    File.AppendAllText(log, $"{DateTimeOffset.Now:u} {ex.GetType().Name}: {ex.Message}\n");
                }
                Environment.Exit(0);
            });
            return;
        }

        var (settings, createdNew) = AppSettings.Load();
        UiScale.Apply(Resources, settings.Display.UiScale);
        _window = new MainWindow(settings, createdNew);
        _window.Activate();
    }
}
