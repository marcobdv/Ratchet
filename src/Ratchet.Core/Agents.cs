namespace CodeStack.Ratchet.Core;

/// <summary>
/// A loaded agent definition — the Claude-Code-compatible subagent shape: a Markdown file
/// with YAML frontmatter (<c>name</c>, <c>description</c>, optional <c>tools</c> and
/// <c>model</c>) whose body is the agent's system prompt. Each becomes a named
/// <see cref="DelegateTool"/> the top-level agent can dispatch to.
/// </summary>
public sealed record AgentDefinition(
    string Name,
    string Description,
    IReadOnlyList<string>? Tools,   // null = the default investigative (read-only) set
    string? Model,                  // null / "inherit" = use the parent's model
    string SystemPrompt);

/// <summary>
/// Discovers agent definitions the same way <see cref="SkillCatalog"/> discovers skills:
/// <c>.ratchet/agents</c> and <c>.claude/agents</c> in the workspace, then the user-level
/// equivalents. One <c>*.md</c> file per agent (Claude Code's convention). Reading
/// <c>.claude/agents</c> lets a repo's existing Claude Code subagents load unchanged.
/// </summary>
public sealed class AgentCatalog
{
    private readonly Dictionary<string, AgentDefinition> _agents = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<AgentDefinition> Agents => _agents.Values;
    public AgentDefinition? Find(string name) => _agents.GetValueOrDefault(name);

    /// <summary>Add a definition directly (used by tests and by callers that build defs in code).</summary>
    internal void Add(AgentDefinition def) => _agents[def.Name] = def;

    public static AgentCatalog Discover(string workingDirectory)
    {
        var catalog = new AgentCatalog();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] roots =
        [
            Path.Combine(workingDirectory, ".ratchet", "agents"),
            Path.Combine(workingDirectory, ".claude", "agents"),
            Path.Combine(home, ".ratchet", "agents"),
            Path.Combine(home, ".claude", "agents"),
        ];

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var file in Directory.EnumerateFiles(root, "*.md"))
            {
                var def = Parse(file);
                if (def is not null) catalog._agents.TryAdd(def.Name, def);   // earlier roots win
            }
        }
        return catalog;
    }

    internal static AgentDefinition? Parse(string file)
    {
        try
        {
            var (meta, body) = Frontmatter.Split(File.ReadAllText(file));
            var name = meta.TryGetValue("name", out var n) && n.Length > 0 ? n : Path.GetFileNameWithoutExtension(file);
            var description = meta.GetValueOrDefault("description", "");
            var model = meta.TryGetValue("model", out var m) && m.Length > 0 && !m.Equals("inherit", StringComparison.OrdinalIgnoreCase)
                ? m : null;

            IReadOnlyList<string>? tools = null;
            if (meta.TryGetValue("tools", out var t) && t.Trim().Length > 0)
                tools = t.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                         .Select(x => x.ToLowerInvariant())
                         .ToList();

            if (string.IsNullOrWhiteSpace(body)) return null;   // no prompt body = not a usable agent
            return new AgentDefinition(name, description, tools, model, body.Trim());
        }
        catch
        {
            return null;   // a malformed agent file shouldn't break discovery
        }
    }
}

public static partial class SubAgents
{
    /// <summary>
    /// Turn a catalog of agent definitions into named <see cref="DelegateTool"/>s. Each agent's
    /// tool subset is resolved from the host's base tools by name; a definition with no <c>tools</c>
    /// gets the default investigative set. The gate is inferred: an all-read-only tool set runs under
    /// a <see cref="ReadOnlyGate"/> (scoped by structure, ADR-0009); anything else under the parent's
    /// gate. The model is resolved per definition, falling back to <paramref name="defaultLlm"/>.
    /// Names colliding with an existing tool are skipped (the registry forbids duplicates).
    /// </summary>
    public static IEnumerable<ITool> BuildFromCatalog(
        AgentCatalog catalog,
        Func<string, ITool?> resolveTool,
        Func<string?, ILlmClient> resolveClient,
        ILlmClient defaultLlm,
        IToolGate parentGate,
        ISet<string> reservedNames,
        Action<string>? log = null)
    {
        var defaultToolNames = new[] { "read", "search", "recall" };

        foreach (var def in catalog.Agents)
        {
            var toolName = Sanitize(def.Name);
            if (!reservedNames.Add(toolName))
            {
                log?.Invoke($"agent '{def.Name}': name collides with an existing tool — skipped.");
                continue;
            }

            var wanted = def.Tools ?? defaultToolNames;
            var tools = wanted.Select(resolveTool).OfType<ITool>().ToList();

            var readOnly = tools.All(t => ReadOnlyGate.AllowedTools.Contains(t.Name));
            IToolGate gate = readOnly ? new ReadOnlyGate() : parentGate;
            var llm = resolveClient(def.Model);

            var description = string.IsNullOrWhiteSpace(def.Description)
                ? $"Delegate a task to the '{def.Name}' sub-agent (it runs in its own context and returns findings)."
                : def.Description;

            yield return new DelegateTool(toolName, description, def.SystemPrompt, llm, tools, gate);
        }
    }

    internal static string Sanitize(string name)
    {
        var cleaned = new string(name.Trim().Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());
        return cleaned.Length == 0 ? "agent" : (cleaned.Length > 64 ? cleaned[..64] : cleaned);
    }
}
