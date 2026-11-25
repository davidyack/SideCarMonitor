using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace SideCarMonitor.Services;

/// <summary>
/// Makes phone retries safe: a POST carrying an Idempotency-Key runs once; a repeat of the same key (the phone's
/// outbox resending after a dropped connection) gets the first answer back instead of doing it again. Keys are
/// kept 72 hours in %LOCALAPPDATA%\SideCarMonitor\idempotency.json, so a kiosk restart doesn't forget them.
/// </summary>
public static class IdempotencyCache
{
    private sealed record Entry(int Status, string ContentType, string Body, DateTimeOffset At);

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SideCarMonitor", "idempotency.json");
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();
    private static readonly object Gate = new();
    private static Dictionary<string, Entry>? _entries;

    public static async Task Middleware(HttpContext ctx, Func<Task> next)
    {
        var key = ctx.Request.Headers["Idempotency-Key"].ToString();
        if (!HttpMethods.IsPost(ctx.Request.Method) || key.Length is 0 or > 100) { await next(); return; }

        var gate = Locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(); // the same key twice at once: the second waits for the first's answer
        try
        {
            Entry? seen;
            lock (Gate) Load().TryGetValue(key, out seen);
            if (seen is not null)
            {
                ctx.Response.StatusCode = seen.Status;
                ctx.Response.ContentType = seen.ContentType;
                ctx.Response.Headers["Idempotent-Replay"] = "true";
                await ctx.Response.WriteAsync(seen.Body);
                return;
            }

            var original = ctx.Response.Body;
            using var buffer = new MemoryStream();
            ctx.Response.Body = buffer;
            try { await next(); }
            finally { ctx.Response.Body = original; }
            buffer.Position = 0;
            var body = await new StreamReader(buffer).ReadToEndAsync();
            // Only remember answers that mean "it happened" or "it never will" (not 5xx: those may succeed on retry).
            if (ctx.Response.StatusCode < 500)
                lock (Gate)
                {
                    Load()[key] = new Entry(ctx.Response.StatusCode, ctx.Response.ContentType ?? "application/json", body, DateTimeOffset.Now);
                    Save();
                }
            buffer.Position = 0;
            await buffer.CopyToAsync(original);
        }
        finally
        {
            gate.Release();
            if (gate.CurrentCount == 1) Locks.TryRemove(key, out _);
        }
    }

    private static Dictionary<string, Entry> Load()
    {
        if (_entries is not null) return _entries;
        try { _entries = File.Exists(FilePath) ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath)) : null; }
        catch { _entries = null; }
        return _entries ??= [];
    }

    private static void Save()
    {
        try
        {
            foreach (var old in _entries!.Where(e => e.Value.At < DateTimeOffset.Now.AddHours(-72)).Select(e => e.Key).ToList()) _entries.Remove(old);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_entries));
        }
        catch { /* best effort */ }
    }
}
