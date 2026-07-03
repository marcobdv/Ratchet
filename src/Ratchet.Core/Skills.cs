using System.Text;

namespace CodeStack.Ratchet.Core;

/// <summary>A discovered Agent Skill: a folder with a SKILL.md whose YAML frontmatter has name + description.</summary>
public sealed record Skill(string Name, string Description, string Directory, string SkillFile);

/// <summary>
/// Discovers Agent Skills (the SKILL.md convention) under <c>.ratchet/skills</c> and
/// <c>.claude/skills</c> in the workspace, then user-level <c>~/.ratchet/skills</c>. Only the
/// name + description are advertised in the system prompt; the full body is paged in on demand via
/// the <c>load_skill</c> tool — progressive disclosure, the same idea as Ratchet's handover/recall.
/// </summary>
public sealed class SkillCatalog
{
    private readonly Dictionary<string, Skill> _skills = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<Skill> Skills => _skills.Values;

    public static SkillCatalog Discover(string workingDirectory)
    {
        var catalog = new SkillCatalog();
        string[] roots =
        [
            Path.Combine(workingDirectory, ".ratchet", "skills"),
            Path.Combine(workingDirectory, ".claude", "skills"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ratchet", "skills"),
        ];

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var skillFile = Path.Combine(dir, "SKILL.md");
                if (!File.Exists(skillFile)) continue;
                var (name, description) = ParseFrontmatter(skillFile, Path.GetFileName(dir));
                catalog._skills.TryAdd(name, new Skill(name, description, dir, skillFile));
            }
        }
        return catalog;
    }

    public Skill? Find(string name) => _skills.GetValueOrDefault(name);

    /// <summary>Skill list for the system prompt, or empty if none. Each description is
    /// flattened to one line and capped: a skill body is repo-provided, so a newline (or a
    /// `\n- fake-skill: …`) in a description must not forge extra entries in this list.</summary>
    public string Describe()
    {
        if (_skills.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var skill in _skills.Values)
            sb.Append("  - ").Append(Sanitize(skill.Name, 60)).Append(": ").AppendLine(Sanitize(skill.Description, 200));
        return sb.ToString().TrimEnd();
    }

    /// <summary>Collapse to a single trimmed line and cap the length — prompt-injection hygiene
    /// for repo-provided text that lands in the system prompt.</summary>
    private static string Sanitize(string s, int max)
    {
        var oneLine = System.Text.RegularExpressions.Regex.Replace(s.ReplaceLineEndings(" "), @"\s+", " ").Trim();
        return oneLine.Length > max ? oneLine[..max] + "…" : oneLine;
    }

    private static (string Name, string Description) ParseFrontmatter(string skillFile, string fallbackName)
    {
        var name = fallbackName;
        var description = "";
        try
        {
            var values = Frontmatter.ParseMeta(File.ReadAllLines(skillFile));
            if (values.TryGetValue("name", out var n) && n.Length > 0) name = n;
            if (values.TryGetValue("description", out var d)) description = d;
        }
        catch
        {
            // fall back to defaults on any IO/parse error
        }
        return (name, description);
    }
}

/// <summary>The <c>load_skill</c> tool: returns a named skill's full SKILL.md plus its bundled files.</summary>
public sealed class SkillTool : ITool
{
    private readonly SkillCatalog _catalog;

    public SkillTool(SkillCatalog catalog) => _catalog = catalog;

    public string Name => "load_skill";
    public string Description =>
        "Load the full instructions for a named skill (its SKILL.md). Call this when a skill's advertised " +
        "description matches the task, to get the detailed steps before acting.";
    public string InputSchemaJson => """
        {"type":"object","properties":{"name":{"type":"string","description":"The skill name, as listed in the available skills."}},"required":["name"]}
        """;

    public Task<string> ExecuteAsync(string inputJson, CancellationToken ct)
    {
        var name = Json.GetString(inputJson, "name");
        var skill = _catalog.Find(name);
        if (skill is null)
        {
            var available = _catalog.Skills.Count == 0 ? "(none)" : string.Join(", ", _catalog.Skills.Select(s => s.Name));
            return Task.FromResult($"No skill named '{name}'. Available: {available}");
        }

        var body = File.ReadAllText(skill.SkillFile);
        var resources = Directory.EnumerateFiles(skill.Directory, "*", SearchOption.AllDirectories)
            .Where(p => !string.Equals(Path.GetFileName(p), "SKILL.md", StringComparison.OrdinalIgnoreCase))
            .Select(p => Path.GetRelativePath(skill.Directory, p))
            .Take(50)
            .ToList();

        var sb = new StringBuilder();
        sb.Append("# Skill: ").AppendLine(skill.Name);
        sb.Append("# Directory: ").AppendLine(skill.Directory);
        if (resources.Count > 0)
            sb.Append("# Bundled files (read with the read tool as needed): ").AppendLine(string.Join(", ", resources));
        sb.AppendLine().Append(body);
        return Task.FromResult(sb.ToString());
    }
}
