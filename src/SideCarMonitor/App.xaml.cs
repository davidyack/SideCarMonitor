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

        // X sign-in ("--x-login") and a one-off read for checking ("--x-test <out.json>", 20 posts, about $0.10).
        if (cl.Contains("--x-login", StringComparer.OrdinalIgnoreCase) || cl.Contains("--x-test", StringComparer.OrdinalIgnoreCase) || cl.Contains("--x-following-count", StringComparer.OrdinalIgnoreCase))
        {
            _ = Task.Run(async () =>
            {
                var log = Path.Combine(Path.GetDirectoryName(TasksPath)!, "x.log");
                try
                {
                    var x = Services.XClient.Create() ?? throw new InvalidOperationException("No x-client-id.txt");
                    if (cl.Contains("--x-following-count", StringComparer.OrdinalIgnoreCase))
                        File.AppendAllText(log, $"{DateTimeOffset.Now:u} following count {await x.FollowingCountAsync()}\n");
                    else if (cl.Contains("--x-login", StringComparer.OrdinalIgnoreCase))
                        File.AppendAllText(log, $"{DateTimeOffset.Now:u} signed in as {await x.SignInAsync()}\n");
                    else
                    {
                        var posts = await x.TimelineAsync(20);
                        var outPath = cl.SkipWhile(a => !a.Equals("--x-test", StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault()
                                      ?? Path.Combine(Path.GetTempPath(), "x-test.json");
                        File.WriteAllText(outPath, System.Text.Json.JsonSerializer.Serialize(posts, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                        File.AppendAllText(log, $"{DateTimeOffset.Now:u} test read {posts.Count} posts\n");
                    }
                }
                catch (Exception ex) { File.AppendAllText(log, $"{DateTimeOffset.Now:u} {ex.GetType().Name}: {ex.Message}\n"); }
                Environment.Exit(0);
            });
            return;
        }

        // PMCollab MCP sign-in ("--pmcollab-login") and a tool listing for checking ("--pmcollab-tools <out.json>").
        // "--pmcollab-call <tool> <args.json> <out.json>": one tool call, for checking.
        if (cl.Contains("--pmcollab-call", StringComparer.OrdinalIgnoreCase))
        {
            var rest = cl.SkipWhile(x => !x.Equals("--pmcollab-call", StringComparison.OrdinalIgnoreCase)).Skip(1).ToList();
            _ = Task.Run(async () =>
            {
                var log = Path.Combine(Path.GetDirectoryName(TasksPath)!, "pmcollab.log");
                try
                {
                    var mcp = new Services.PmCollabMcp("https://dev1.pmcollab.ai/mcp/pmc");
                    var args = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(rest[1]))!.AsObject();
                    var result = await mcp.CallToolAsync(rest[0], args);
                    File.WriteAllText(rest[2], result?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) ?? "null");
                    File.AppendAllText(log, $"{DateTimeOffset.Now:u} called {rest[0]}\n");
                }
                catch (Exception ex) { File.AppendAllText(log, $"{DateTimeOffset.Now:u} {ex.GetType().Name}: {ex.Message}\n"); }
                Environment.Exit(0);
            });
            return;
        }

        if (cl.Contains("--pmcollab-login", StringComparer.OrdinalIgnoreCase) || cl.Contains("--pmcollab-tools", StringComparer.OrdinalIgnoreCase))
        {
            _ = Task.Run(async () =>
            {
                var log = Path.Combine(Path.GetDirectoryName(TasksPath)!, "pmcollab.log");
                try
                {
                    var mcp = new Services.PmCollabMcp("https://dev1.pmcollab.ai/mcp/pmc");
                    if (cl.Contains("--pmcollab-login", StringComparer.OrdinalIgnoreCase))
                        File.AppendAllText(log, $"{DateTimeOffset.Now:u} {await mcp.SignInAsync()}\n");
                    var outPath = cl.SkipWhile(a => !a.Equals("--pmcollab-tools", StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault()
                                  ?? Path.Combine(Path.GetTempPath(), "pmcollab-tools.json");
                    var tools = await mcp.ListToolsAsync();
                    File.WriteAllText(outPath, tools.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                    File.AppendAllText(log, $"{DateTimeOffset.Now:u} {tools.Count} tools listed to {outPath}\n");
                }
                catch (Exception ex) { File.AppendAllText(log, $"{DateTimeOffset.Now:u} {ex.GetType().Name}: {ex.Message}\n"); }
                Environment.Exit(0);
            });
            return;
        }

        // Calendar sign-in: "--calendar-login CTC" opens the browser to sign in that M365 account once.
        if (cl.Contains("--calendar-login", StringComparer.OrdinalIgnoreCase))
        {
            var label = cl.SkipWhile(a => !a.Equals("--calendar-login", StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault() ?? "Calendar";
            _ = Task.Run(async () =>
            {
                var log = Path.Combine(Path.GetDirectoryName(TasksPath)!, "calendar.log");
                try { File.AppendAllText(log, $"{DateTimeOffset.Now:u} {label} signed in as {await new Services.CalendarService().SignInAsync(label)}\n"); }
                catch (Exception ex) { File.AppendAllText(log, $"{DateTimeOffset.Now:u} {label} sign-in failed: {ex.Message}\n"); }
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
