using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Procedural memory: skills in a folder, one per subfolder:
///   skills/&lt;name&gt;/SKILL.md   frontmatter (name, description, optional script) + step-by-step instructions
///   skills/&lt;name&gt;/run.ps1|run.py  optional script
/// The user can add skills by dropping folders in; the agent can create them too.
/// Scripts run as child processes of the agent, so they inherit its MXC sandbox (files, network, UI limits).
/// </summary>
sealed class SkillStore
{
    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromSeconds(60);
    private const int MaxOutput = 6000;

    private readonly string _dir;
    private readonly string _scratch;

    public SkillStore(string stateDir)
    {
        _dir = Path.Combine(stateDir, "skills");
        _scratch = Path.Combine(stateDir, "scratch");
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(_scratch);
    }

    public sealed record Skill(string Name, string Description, string? Script, string Instructions, string Folder);

    public IReadOnlyList<Skill> List() =>
        Directory.EnumerateDirectories(_dir)
            .Select(d => Load(Path.GetFileName(d)))
            .OfType<Skill>()
            .OrderBy(s => s.Name)
            .ToList();

    public Skill? Load(string name)
    {
        var folder = Path.Combine(_dir, SafeName(name));
        var file = Path.Combine(folder, "SKILL.md");
        if (!File.Exists(file)) return null;

        var text = File.ReadAllText(file);
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var body = text;
        var m = Regex.Match(text, @"^---\s*\n(.*?)\n---\s*\n?(.*)$", RegexOptions.Singleline);
        if (m.Success)
        {
            foreach (var line in m.Groups[1].Value.Split('\n'))
            {
                var i = line.IndexOf(':');
                if (i > 0) meta[line[..i].Trim()] = line[(i + 1)..].Trim();
            }
            body = m.Groups[2].Value;
        }
        var script = meta.GetValueOrDefault("script");
        return new Skill(
            meta.GetValueOrDefault("name") ?? Path.GetFileName(folder),
            meta.GetValueOrDefault("description") ?? "",
            string.IsNullOrWhiteSpace(script) ? null : script,
            body.Trim(),
            folder);
    }

    /// <summary>Creates or replaces a skill. scriptLanguage: "powershell" or "python" (or null for no script).</summary>
    public Skill Save(string name, string description, string instructions, string? script, string? scriptLanguage)
    {
        var safe = SafeName(name);
        if (safe.Length == 0) throw new ArgumentException("Skill name must contain letters or digits.");
        var folder = Path.Combine(_dir, safe);
        Directory.CreateDirectory(folder);

        string? scriptFile = null;
        foreach (var old in Directory.EnumerateFiles(folder, "run.*")) File.Delete(old);
        if (!string.IsNullOrWhiteSpace(script))
        {
            scriptFile = scriptLanguage?.ToLowerInvariant() is "python" or "py" ? "run.py" : "run.ps1";
            File.WriteAllText(Path.Combine(folder, scriptFile), script);
        }

        var md = new StringBuilder()
            .Append("---\n")
            .Append($"name: {safe}\n")
            .Append($"description: {description.Replace('\n', ' ').Trim()}\n");
        if (scriptFile is not null) md.Append($"script: {scriptFile}\n");
        md.Append($"created: {DateTime.Now:yyyy-MM-dd}\n---\n\n").Append(instructions.Trim()).Append('\n');
        File.WriteAllText(Path.Combine(folder, "SKILL.md"), md.ToString());
        return Load(safe)!;
    }

    /// <summary>Runs a skill's script with optional arguments and returns its exit code and (trimmed) output.</summary>
    public async Task<object> RunScriptAsync(string name, string? args)
    {
        var skill = Load(name);
        if (skill is null) return new { error = $"No skill named '{name}'." };
        if (skill.Script is null) return new { error = $"Skill '{skill.Name}' has no script; follow its instructions instead." };

        var path = Path.Combine(skill.Folder, skill.Script);
        if (!File.Exists(path)) return new { error = $"Script file '{skill.Script}' is missing." };

        var isPython = path.EndsWith(".py", StringComparison.OrdinalIgnoreCase);
        var psi = isPython
            ? new ProcessStartInfo(FindPython() ?? "python.exe")
            : new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"));
        if (isPython) psi.ArgumentList.Add(path);
        else { foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", path }) psi.ArgumentList.Add(a); }
        foreach (var a in SplitArgs(args)) psi.ArgumentList.Add(a);

        psi.WorkingDirectory = _scratch;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.Environment["SKILL_DIR"] = skill.Folder;
        psi.Environment["SCRATCH_DIR"] = _scratch;

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(ScriptTimeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            p.Kill(entireProcessTree: true);
            return new { error = $"Script timed out after {ScriptTimeout.TotalSeconds:0} seconds." };
        }
        return new { exitCode = p.ExitCode, output = Trim(await stdout), errors = Trim(await stderr) };
    }

    /// <summary>One line per skill, for the system prompt.</summary>
    public string Catalog()
    {
        var skills = List();
        if (skills.Count == 0) return "You have no skills saved yet.";
        return "Your saved skills (use read_skill before following one):\n" +
               string.Join("\n", skills.Select(s => $"- {s.Name}: {s.Description}{(s.Script is null ? "" : " [has script]")}"));
    }

    private static string? FindPython()
    {
        var configured = Environment.GetEnvironmentVariable("SIDECAR_PYTHON");
        return !string.IsNullOrWhiteSpace(configured) && File.Exists(configured) ? configured : null;
    }

    private static string SafeName(string name) =>
        Regex.Replace(name.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');

    private static string Trim(string s) => s.Length <= MaxOutput ? s : s[..MaxOutput] + "\n…(truncated)";

    private static IEnumerable<string> SplitArgs(string? args) =>
        string.IsNullOrWhiteSpace(args)
            ? []
            : Regex.Matches(args, "\"([^\"]*)\"|(\\S+)").Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
}
