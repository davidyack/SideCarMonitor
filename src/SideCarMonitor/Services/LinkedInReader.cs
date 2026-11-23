using System.Text.Json.Serialization;
using Microsoft.Playwright;

namespace SideCarMonitor.Services;

/// <summary>One post as read from your LinkedIn feed.</summary>
public sealed record FeedPost
{
    [JsonPropertyName("urn")] public string Urn { get; init; } = "";
    [JsonPropertyName("author")] public string Author { get; init; } = "";
    [JsonPropertyName("authorUrl")] public string AuthorUrl { get; init; } = "";
    [JsonPropertyName("headline")] public string Headline { get; init; } = "";
    [JsonPropertyName("age")] public string Age { get; init; } = "";
    [JsonPropertyName("text")] public string Text { get; init; } = "";
    /// <summary>Line above the post, e.g. "Jane Doe likes this" or "Suggested".</summary>
    [JsonPropertyName("context")] public string Context { get; init; } = "";
    [JsonPropertyName("promoted")] public bool Promoted { get; init; }
    /// <summary>The post's visible text, as a fallback when LinkedIn changes its page structure.</summary>
    [JsonPropertyName("raw")] public string Raw { get; init; } = "";
    /// <summary>The post's own link when the page shows one, else the author's profile.</summary>
    [JsonPropertyName("link")] public string PostLink { get; init; } = "";

    public string OpenUrl() => PostLink.Length > 0 ? PostLink : AuthorUrl;
}

/// <summary>LinkedIn asked for a login, captcha or verification; stop and let the user handle it.</summary>
public sealed class LinkedInChallengeException(string message) : Exception(message);

/// <summary>
/// Reads your LinkedIn feed with Playwright, driving your installed Edge in a dedicated profile you log into once
/// (%LOCALAPPDATA%\SideCarMonitor\linkedin-edge). Read-only: it opens the feed, scrolls a little like a person,
/// copies author, text and link of the posts, and closes. It never clicks, likes, comments or messages.
/// If LinkedIn shows a login or verification page it stops (no retries) so you can deal with it.
/// Runs in the kiosk, not the sandboxed agent; the agent only ever sees the extracted text.
/// </summary>
public static class LinkedInReader
{
    public static string ProfileDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SideCarMonitor", "linkedin-edge");

    private static async Task<IBrowserContext> OpenAsync(IPlaywright pw, bool minimized) =>
        await pw.Chromium.LaunchPersistentContextAsync(ProfileDirectory, new BrowserTypeLaunchPersistentContextOptions
        {
            Channel = "msedge",       // your installed Edge, not a bundled test browser
            Headless = false,
            ViewportSize = ViewportSize.NoViewport,
            // A minimized window never loads more posts (the feed only loads what's "visible"), so scheduled reads
            // open a normal window placed off-screen: it renders like any window but doesn't cover your desktop.
            Args = minimized ? ["--window-position=12000,0", "--window-size=1280,1400"] : ["--start-maximized"],
        });

    /// <summary>Opens the LinkedIn login page in the dedicated profile and waits (up to 10 minutes) until you reach the feed.</summary>
    public static async Task<bool> LoginAsync()
    {
        using var pw = await Playwright.CreateAsync();
        await using var context = await OpenAsync(pw, minimized: false);
        var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();
        await page.GotoAsync("https://www.linkedin.com/login");
        for (var i = 0; i < 600; i++)
        {
            if (page.IsClosed) return false;
            if (page.Url.Contains("/feed")) { await Task.Delay(2000); return true; }
            await Task.Delay(1000);
        }
        return false;
    }

    /// <summary>Reads roughly <paramref name="maxPosts"/> posts from the top of your feed.</summary>
    public static async Task<List<FeedPost>> ReadFeedAsync(int maxPosts = 40, CancellationToken ct = default)
    {
        using var pw = await Playwright.CreateAsync();
        await using var context = await OpenAsync(pw, minimized: true);
        var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();
        await page.GotoAsync("https://www.linkedin.com/feed/", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60_000 });
        CheckForChallenge(page.Url);

        var random = new Random();
        await Task.Delay(random.Next(3000, 6000), ct);
        var posts = new Dictionary<string, FeedPost>();
        for (var scroll = 0; scroll < 12 && posts.Count < maxPosts; scroll++)
        {
            CheckForChallenge(page.Url);
            foreach (var p in await page.EvaluateAsync<FeedPost[]>(ExtractScript) ?? [])
                if (p.Urn.Length > 0 && !posts.ContainsKey(p.Urn)) posts[p.Urn] = p;
            // Scroll about a screen, at a human pace.
            // The feed scrolls an inner panel, not the window: bring the last loaded post into view so more load.
            await page.EvaluateAsync("""
                () => {
                  const p = document.querySelectorAll('[data-view-name="feed-full-update"]');
                  let sc = p.length ? p[p.length - 1] : null;
                  while (sc && !(sc.scrollHeight > sc.clientHeight + 50 && /(auto|scroll)/.test(getComputedStyle(sc).overflowY))) sc = sc.parentElement;
                  if (sc) sc.scrollTop += 1200 + Math.floor(Math.random() * 500); else window.scrollBy(0, 1200);
                }
                """);
            await Task.Delay(random.Next(1800, 4200), ct);
        }
        return posts.Values.Take(maxPosts).ToList();
    }

    /// <summary>Reads a person's (or company's) recent posts from their activity page. Only when you tap Refresh.</summary>
    public static async Task<List<FeedPost>> ReadActivityAsync(string profileUrl, int maxPosts = 15, CancellationToken ct = default)
    {
        var url = profileUrl.TrimEnd('/') + (profileUrl.Contains("/company/") ? "/posts/" : "/recent-activity/all/");
        using var pw = await Playwright.CreateAsync();
        await using var context = await OpenAsync(pw, minimized: true);
        var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();
        await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60_000 });
        CheckForChallenge(page.Url);
        var random = new Random();
        await Task.Delay(random.Next(3000, 5000), ct);
        var posts = new Dictionary<string, FeedPost>();
        for (var scroll = 0; scroll < 4 && posts.Count < maxPosts; scroll++)
        {
            foreach (var p in await page.EvaluateAsync<FeedPost[]>(ExtractScript) ?? [])
                if (p.Urn.Length > 0 && !posts.ContainsKey(p.Urn)) posts[p.Urn] = p;
            await page.EvaluateAsync("() => window.scrollBy(0, 1200)");
            await Task.Delay(random.Next(1800, 3500), ct);
        }
        return posts.Values.Take(maxPosts).ToList();
    }

    private static void CheckForChallenge(string url)
    {
        if (url.Contains("/login") || url.Contains("/checkpoint") || url.Contains("/authwall") || url.Contains("/uas/"))
            throw new LinkedInChallengeException("LinkedIn wants you to sign in or verify. Open LinkedIn setup on the kiosk.");
    }

    // LinkedIn's markup (Nov 2026): posts are [data-view-name="feed-full-update"] with feed-actor (author link),
    // feed-commentary (text), feed-original-share-* (reshares) and feed-header-text ("X likes this").
    // Posts carry no id, so the id is a hash of author + text. "raw" keeps the visible text if this drifts.
    private const string ExtractScript = """
        () => {
          const v = (root, name) => root.querySelector('[data-view-name="' + name + '"]');
          const txt = e => e ? (e.innerText || '').trim() : '';
          const hash = s => { let h = 0; for (let i = 0; i < s.length; i++) h = (h * 31 + s.charCodeAt(i)) | 0; return (h >>> 0).toString(36); };
          const out = [];
          for (const n of document.querySelectorAll('[data-view-name="feed-full-update"]')) {
            const actor = [...n.querySelectorAll('[data-view-name="feed-actor"]')].find(e => txt(e));
            const actorLink = n.querySelector('a[data-view-name="feed-actor"][href]') || (actor && actor.closest('a[href]'));
            const author = txt(actor).split('\n')[0].split(' • ')[0].trim();
            const raw = txt(n);
            const lines = raw.split('\n').map(l => l.trim()).filter(l => l && l !== 'Feed post');
            const ai = lines.findIndex(l => l.startsWith(author));
            // The line(s) after the name: skip the connection degree ("• 2nd"), the age and follower counts.
            const after = ai >= 0 ? lines.slice(ai + 1, ai + 4).filter(l => !l.startsWith('•') && !/^\d+(m|h|d|w|mo|yr)\b/.test(l) && !/followers$/.test(l) && !/^(Follow|Following|Connect|Promoted|View my services|Visit my website)$/.test(l)) : [];
            const headline = after[0] || '';
            const age = (lines.find(l => /^\d+(m|h|d|w|mo|yr)\b/.test(l)) || '').replace(/\s*•.*$/, '');
            let text = txt(v(n, 'feed-commentary'));
            const shareActor = txt(v(n, 'feed-original-share-actor')).split('\n')[0].split(' • ')[0].trim();
            const shareText = txt(v(n, 'feed-original-share-description'));
            if (shareActor) text += (text ? '\n\n' : '') + 'Reshared from ' + shareActor + ': ' + shareText;
            const postLink = [...n.querySelectorAll('a[href*="/feed/update/"], a[href*="/posts/"]')].map(a => a.href.split('?')[0])[0] || '';
            out.push({
              urn: hash(author + '|' + text.slice(0, 300)),
              author, authorUrl: actorLink ? actorLink.href.split('?')[0] : '',
              headline, age,
              text: text.slice(0, 4000),
              // e.g. "Jon-Michael Horst likes this": the header element holds only the name, its parent the whole line.
              context: (() => { const h = v(n, 'feed-header-text'); return h ? txt(h.parentElement || h).replace(/\s+/g, ' ') : ''; })(),
              promoted: /\bPromoted\b/.test(raw.slice(0, 400)),
              link: postLink,
              raw: raw.slice(0, 2000),
            });
          }
          return out.filter(p => p.author || p.text);
        }
        """;
}
