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

        // "--mail-count": Inbox messages per day for the last 30 days (and the folder totals) to mail-count.txt, for checking catch-up.
        if (cl.Contains("--mail-count", StringComparer.OrdinalIgnoreCase))
        {
            _ = Task.Run(async () =>
            {
                var outPath = Path.Combine(Path.GetDirectoryName(TasksPath)!, "mail-count.txt");
                var lines = new List<string>();
                try
                {
                    var cal = new Services.CalendarService();
                    var inbox = await cal.MailGraphAsync("CTC", HttpMethod.Get, "me/mailFolders/inbox?$select=totalItemCount,unreadItemCount,childFolderCount");
                    lines.Add($"inbox total {inbox?["totalItemCount"]}, unread {inbox?["unreadItemCount"]}, child folders {inbox?["childFolderCount"]}");
                    var since = DateTimeOffset.UtcNow.AddDays(-30);
                    var days = new SortedDictionary<string, int>();
                    string? url = $"me/mailFolders/inbox/messages?$filter=receivedDateTime ge {since:yyyy-MM-ddTHH:mm:ssZ}&$select=receivedDateTime&$top=500";
                    var total = 0;
                    while (url is not null)
                    {
                        var res = await cal.MailGraphAsync("CTC", HttpMethod.Get, url);
                        foreach (var m in res?["value"]?.AsArray() ?? [])
                        {
                            var d = DateTimeOffset.Parse(m!["receivedDateTime"]!.GetValue<string>()).ToLocalTime().ToString("MM-dd");
                            days[d] = days.GetValueOrDefault(d) + 1; total++;
                        }
                        url = res?["@odata.nextLink"]?.GetValue<string>()?.Replace("https://graph.microsoft.com/v1.0/", "");
                    }
                    lines.Add($"last 30 days in Inbox: {total}");
                    lines.AddRange(days.Select(kv => $"{kv.Key} {kv.Value}"));
                }
                catch (Exception ex) { lines.Add(ex.Message); }
                File.WriteAllLines(outPath, lines);
                Environment.Exit(0);
            });
            return;
        }

        // "--mail-lists": writes the To/Cc addresses of recent Inbox mail (with counts) to mail-lists.txt, to find list addresses.
        if (cl.Contains("--mail-lists", StringComparer.OrdinalIgnoreCase))
        {
            _ = Task.Run(async () =>
            {
                var outPath = Path.Combine(Path.GetDirectoryName(TasksPath)!, "mail-lists.txt");
                try
                {
                    var cal = new Services.CalendarService();
                    var counts = new Dictionary<string, (int N, string Sample)>();
                    string? url = "me/mailFolders/inbox/messages?$top=100&$select=subject,toRecipients,ccRecipients";
                    for (var page = 0; page < 3 && url is not null; page++)
                    {
                        var res = await cal.MailGraphAsync("CTC", HttpMethod.Get, url);
                        foreach (var m in res?["value"]?.AsArray() ?? [])
                            foreach (var r in (m?["toRecipients"]?.AsArray() ?? []).Concat(m?["ccRecipients"]?.AsArray() ?? []))
                            {
                                var key = $"{r?["emailAddress"]?["name"]} <{r?["emailAddress"]?["address"]}>";
                                counts[key] = (counts.GetValueOrDefault(key).N + 1, counts.GetValueOrDefault(key).Sample ?? m?["subject"]?.ToString() ?? "");
                            }
                        url = res?["@odata.nextLink"]?.GetValue<string>()?.Replace("https://graph.microsoft.com/v1.0/", "");
                    }
                    File.WriteAllLines(outPath, counts.OrderByDescending(c => c.Value.N).Take(60).Select(c => $"{c.Value.N}\t{c.Key}\t{c.Value.Sample}"));
                }
                catch (Exception ex) { File.WriteAllText(outPath, ex.Message); }
                Environment.Exit(0);
            });
            return;
        }

        // Mail sign-in: "--mail-login CTC" adds mail permission to that calendar account (for mail triage).
        if (cl.Contains("--mail-login", StringComparer.OrdinalIgnoreCase))
        {
            var label = cl.SkipWhile(a => !a.Equals("--mail-login", StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault() ?? "CTC";
            _ = Task.Run(async () =>
            {
                var log = Path.Combine(Path.GetDirectoryName(TasksPath)!, "mail.log");
                try
                {
                    var who = await new Services.CalendarService().SignInAsync(label, withMail: true);
                    var me = await new Services.CalendarService().MailGraphAsync(label, HttpMethod.Get, "me/mailFolders/inbox?$select=totalItemCount,unreadItemCount");
                    File.AppendAllText(log, $"{DateTimeOffset.Now:u} {label} mail signed in as {who}; inbox {me?["totalItemCount"]} items\n");
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
