using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// The agent's long-term memory, stored as plain files in its sandboxed state folder so the user can read and edit them:
///   working.md      — working memory: notes for the task in progress; cleared when a conversation ends
///   episodes.jsonl  — episodic memory: one dated summary per conversation (or notable event)
///   facts.json      — semantic memory: lasting facts about the user, the home and preferences
/// Procedural memory (skills) lives in <see cref="SkillStore"/>.
/// Search is simple keyword scoring; good enough for a personal kiosk, no embedding model needed.
/// </summary>
sealed class MemoryStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _dir;
    private readonly Lock _gate = new();

    public MemoryStore(string stateDir)
    {
        _dir = Path.Combine(stateDir, "memory");
        Directory.CreateDirectory(_dir);
    }

    private string WorkingPath => Path.Combine(_dir, "working.md");
    private string EpisodesPath => Path.Combine(_dir, "episodes.jsonl");
    private string FactsPath => Path.Combine(_dir, "facts.json");

    // ---- Working memory ---------------------------------------------------------------

    public string GetWorking()
    {
        lock (_gate) return File.Exists(WorkingPath) ? File.ReadAllText(WorkingPath) : "";
    }

    public void SetWorking(string notes)
    {
        lock (_gate) File.WriteAllText(WorkingPath, notes);
    }

    public void ClearWorking()
    {
        lock (_gate) if (File.Exists(WorkingPath)) File.Delete(WorkingPath);
    }

    // ---- Episodic memory --------------------------------------------------------------

    public sealed record Episode(DateTime Time, string Summary);

    public void RecordEpisode(string summary)
    {
        var line = JsonSerializer.Serialize(new Episode(DateTime.Now, summary.Trim()));
        lock (_gate) File.AppendAllText(EpisodesPath, line + "\n");
    }

    public IReadOnlyList<Episode> Episodes()
    {
        lock (_gate)
        {
            if (!File.Exists(EpisodesPath)) return [];
            return File.ReadAllLines(EpisodesPath)
                .Where(l => l.Length > 0)
                .Select(l => { try { return JsonSerializer.Deserialize<Episode>(l); } catch (JsonException) { return null; } })
                .OfType<Episode>()
                .ToList();
        }
    }

    // ---- Semantic memory --------------------------------------------------------------

    public sealed class Fact
    {
        public string Id { get; set; } = "";
        public string Text { get; set; } = "";
        public string Category { get; set; } = "general";
        public DateTime Updated { get; set; }
    }

    public List<Fact> Facts()
    {
        lock (_gate)
        {
            if (!File.Exists(FactsPath)) return [];
            try { return JsonSerializer.Deserialize<List<Fact>>(File.ReadAllText(FactsPath)) ?? []; }
            catch (JsonException) { return []; }
        }
    }

    private void SaveFacts(List<Fact> facts)
    {
        lock (_gate) File.WriteAllText(FactsPath, JsonSerializer.Serialize(facts, Json));
    }

    /// <summary>Adds a fact, or replaces the fact with the given id.</summary>
    public Fact Remember(string text, string category, string? replaceId)
    {
        var facts = Facts();
        var fact = replaceId is not null ? facts.FirstOrDefault(f => f.Id == replaceId) : null;
        if (fact is null)
        {
            fact = new Fact { Id = "f" + Guid.NewGuid().ToString("N")[..6] };
            facts.Add(fact);
        }
        fact.Text = text.Trim();
        fact.Category = string.IsNullOrWhiteSpace(category) ? "general" : category.Trim().ToLowerInvariant();
        fact.Updated = DateTime.Now;
        SaveFacts(facts);
        return fact;
    }

    public bool Forget(string id)
    {
        var facts = Facts();
        var removed = facts.RemoveAll(f => f.Id == id) > 0;
        if (removed) SaveFacts(facts);
        return removed;
    }

    // ---- Recall -----------------------------------------------------------------------

    /// <summary>Keyword search across facts and episodes; recent episodes rank a little higher.</summary>
    public object Recall(string query, int? days)
    {
        var terms = Tokens(query);
        var since = days is > 0 ? DateTime.Now.AddDays(-days.Value) : DateTime.MinValue;

        var facts = Facts()
            .Select(f => (f, score: Score(terms, f.Text + " " + f.Category)))
            .Where(x => x.score > 0 || terms.Count == 0)
            .OrderByDescending(x => x.score).Take(10)
            .Select(x => new { x.f.Id, x.f.Text, x.f.Category });

        var episodes = Episodes()
            .Where(e => e.Time >= since)
            .Select(e => (e, score: Score(terms, e.Summary) + RecencyBoost(e.Time)))
            .Where(x => terms.Count == 0 || x.score > RecencyBoost(x.e.Time))
            .OrderByDescending(x => x.score).Take(10)
            .Select(x => new { when = x.e.Time.ToString("ddd MMM d, h:mm tt"), x.e.Summary });

        return new { facts, episodes };
    }

    private static double RecencyBoost(DateTime t) => Math.Max(0, 0.5 - (DateTime.Now - t).TotalDays / 60);

    private static HashSet<string> Tokens(string text) =>
        Regex.Matches(text.ToLowerInvariant(), "[a-z0-9]{3,}").Select(m => m.Value).ToHashSet();

    private static double Score(HashSet<string> terms, string text)
    {
        if (terms.Count == 0) return 0;
        var words = Tokens(text);
        return terms.Count(words.Contains);
    }

    /// <summary>Short memory digest for the start of a session: facts plus the latest few episodes.</summary>
    public string Digest()
    {
        var facts = Facts();
        var recent = Episodes().TakeLast(5).ToList();
        var lines = new List<string>();
        if (facts.Count > 0)
        {
            lines.Add("Things you know about the user and home (semantic memory):");
            lines.AddRange(facts.OrderBy(f => f.Category).Take(40).Select(f => $"- [{f.Id}] ({f.Category}) {f.Text}"));
        }
        if (recent.Count > 0)
        {
            lines.Add("Recent conversations (episodic memory):");
            lines.AddRange(recent.Select(e => $"- {e.Time:ddd MMM d h:mm tt}: {e.Summary}"));
        }
        return string.Join("\n", lines);
    }
}
