using System.Text;

namespace CodeStack.Ratchet.Core;

/// <summary>
/// A loaded agent definition — the Claude-Code-compatible subagent shape: a Markdown file
/// with YAML frontmatter (<c>name</c>, <c>description</c>, optional <c>tools</c> and
/// <c>model</c>) whose body is the agent's system prompt. Each becomes a named
/// <see cref="DelegateTool"/> the top-level agent can dispatch to — unless it lists
/// <c>members</c>, which makes it a <see cref="TeamTool"/> (the team tier).
/// </summary>
public sealed record AgentDefinition(
    string Name,
    string Description,
    IReadOnlyList<string>? Tools,   // null = the default investigative (read-only) set
    string? Model,                  // null / "inherit" = use the parent's model
    string SystemPrompt,
    IReadOnlyList<string>? Members = null,   // non-empty = a team or council, not a solo agent
    string? Mode = null,                     // "council" = deliberation protocol; else a merging team
    string? Provider = null)                 // null = the top-level provider; else this agent's own backend
{
    public bool HasMembers => Members is { Count: > 0 };
    public bool IsCouncil => HasMembers && string.Equals(Mode, "council", StringComparison.OrdinalIgnoreCase);
    public bool IsTeam => HasMembers && !IsCouncil;
}

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

            // provider: an explicit backend for this agent (local, openrouter, …), or a
            // "provider:model" prefix on the model value (openrouter model ids contain '/',
            // so the first ':' unambiguously splits provider from model).
            var provider = meta.TryGetValue("provider", out var pv) && pv.Length > 0 ? pv.ToLowerInvariant() : null;
            if (provider is null && model is not null)
            {
                var colon = model.IndexOf(':');
                if (colon > 0) { provider = model[..colon].Trim().ToLowerInvariant(); model = model[(colon + 1)..].Trim(); }
            }

            IReadOnlyList<string>? tools = null;
            if (meta.TryGetValue("tools", out var t) && t.Trim().Length > 0)
                tools = t.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                         .Select(x => x.ToLowerInvariant())
                         .ToList();

            IReadOnlyList<string>? members = null;
            if (meta.TryGetValue("members", out var mem) && mem.Trim().Length > 0)
                members = mem.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var mode = meta.TryGetValue("mode", out var md) && md.Length > 0 ? md.ToLowerInvariant() : null;

            // A team/council may have an empty body (defaults apply); a solo agent needs a prompt.
            if (members is null && string.IsNullOrWhiteSpace(body)) return null;
            return new AgentDefinition(name, description, tools, model, body.Trim(), members, mode, provider);
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
    /// <param name="resolveClient">Maps an agent's (provider, model) — either may be null to
    /// inherit the top-level default — to an <see cref="ILlmClient"/>. This is where per-agent
    /// multi-provider selection happens (a local persona alongside an OpenRouter one).</param>
    public static IEnumerable<ITool> BuildFromCatalog(
        AgentCatalog catalog,
        Func<string, ITool?> resolveTool,
        Func<string?, string?, ILlmClient> resolveClient,
        ILlmClient defaultLlm,
        IToolGate parentGate,
        ISet<string> reservedNames,
        string workspaceDir,
        Action<string>? log = null)
    {
        var defaultToolNames = new[] { "read", "search", "recall" };
        var built = new List<ITool>();
        var byName = new Dictionary<string, ITool>(StringComparer.Ordinal);

        // Pass 1: solo agents. Teams and councils resolve their members from these + the base
        // tools, so the solos must exist first.
        foreach (var def in catalog.Agents.Where(a => !a.HasMembers))
        {
            var toolName = Sanitize(def.Name);
            if (!reservedNames.Add(toolName))
            {
                log?.Invoke($"agent '{def.Name}': name collides with an existing tool — skipped.");
                continue;
            }

            var tools = (def.Tools ?? defaultToolNames).Select(resolveTool).OfType<ITool>().ToList();
            var readOnly = tools.Count > 0 && tools.All(t => ReadOnlyGate.AllowedTools.Contains(t.Name));
            IToolGate gate = readOnly ? new ReadOnlyGate() : parentGate;

            var tool = new DelegateTool(
                toolName,
                string.IsNullOrWhiteSpace(def.Description)
                    ? $"Delegate a task to the '{def.Name}' sub-agent (runs in its own context, returns findings)."
                    : def.Description,
                def.SystemPrompt, resolveClient(def.Provider, def.Model), tools, gate);
            built.Add(tool);
            byName[toolName] = tool;
        }

        // Pass 2: teams and councils. A member resolves against the pass-1 agents, then the base
        // tools, then the built-in council personas (so a council works out of the box). A council
        // member gets its own model when defined as an agent (Council of Reeds); a built-in
        // fallback persona runs on the coordinator's model.
        ITool BuiltinPersona(string n, ILlmClient llm)
        {
            var p = CouncilPersonas.Find(n)!;
            return new DelegateTool(Sanitize(p.Name), $"Council persona: {p.Lens}.", p.Prompt, llm, Array.Empty<ITool>());
        }
        ITool? ResolveMember(string n, ILlmClient fallbackLlm) =>
            byName.GetValueOrDefault(Sanitize(n)) ?? byName.GetValueOrDefault(n) ?? resolveTool(n)
            ?? (CouncilPersonas.IsBuiltin(n) ? BuiltinPersona(n, fallbackLlm) : null);

        foreach (var def in catalog.Agents.Where(a => a.HasMembers))
        {
            var toolName = Sanitize(def.Name);
            var kind = def.IsCouncil ? "council" : "team";
            if (!reservedNames.Add(toolName))
            {
                log?.Invoke($"{kind} '{def.Name}': name collides with an existing tool — skipped.");
                continue;
            }

            var coordinator = resolveClient(def.Provider, def.Model);
            var members = def.Members!.Select(m => ResolveMember(m, coordinator)).OfType<ITool>().ToList();
            if (members.Count == 0)
            {
                log?.Invoke($"{kind} '{def.Name}': none of its members [{string.Join(", ", def.Members!)}] resolved — skipped.");
                continue;
            }

            if (def.IsCouncil)
            {
                built.Add(new CouncilTool(
                    toolName,
                    string.IsNullOrWhiteSpace(def.Description)
                        ? $"Deliberate an architectural decision with the '{def.Name}' council ({members.Count} independent personas) and emit an Analysis Brief + Decision Record."
                        : def.Description,
                    members, coordinator, workspaceDir));
            }
            else
            {
                built.Add(new TeamTool(
                    toolName,
                    string.IsNullOrWhiteSpace(def.Description)
                        ? $"Dispatch a task to the '{def.Name}' team ({members.Count} members, in parallel) and return a merged result."
                        : def.Description,
                    members,
                    lead: coordinator,
                    synthesisPrompt: string.IsNullOrWhiteSpace(def.SystemPrompt) ? null : def.SystemPrompt));
            }
        }

        return built;
    }

    internal static string Sanitize(string name)
    {
        var cleaned = new string(name.Trim().Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());
        return cleaned.Length == 0 ? "agent" : (cleaned.Length > 64 ? cleaned[..64] : cleaned);
    }
}

/// <summary>
/// A team (the middle tier of the delegation family): dispatches a task to several member
/// agents <b>in parallel</b>, each in its own cold context, then either concatenates their
/// labelled outputs or — when a lead model is configured — runs one synthesis pass that
/// merges them. The parallel fan-out lives here inside one tool because Ratchet's loop runs
/// tools sequentially; a team is one tool call that fans out internally.
/// </summary>
public sealed class TeamTool : ITool
{
    private readonly IReadOnlyList<ITool> _members;
    private readonly ILlmClient? _lead;
    private readonly string? _synthesisPrompt;

    public TeamTool(string name, string description, IReadOnlyList<ITool> members,
        ILlmClient? lead = null, string? synthesisPrompt = null)
    {
        Name = name;
        Description = description;
        _members = members;
        _lead = lead;
        _synthesisPrompt = synthesisPrompt;
    }

    public string Name { get; }
    public string Description { get; }

    public string InputSchemaJson => """
        {"type":"object","properties":{"task":{"type":"string","description":"The task to dispatch to every team member in parallel, with all context they need (each runs cold)."}},"required":["task"]}
        """;

    public async Task<string> ExecuteAsync(string inputJson, CancellationToken ct)
    {
        var task = Json.GetString(inputJson, "task");
        if (Delegation.AtLimit)
            return $"(team refused: nesting limit {Delegation.MaxDepth} reached)";
        using var _ = Delegation.Enter();

        var results = await SubAgents.DispatchParallelAsync(_members, task, ct);

        var labelled = new StringBuilder();
        foreach (var (member, output) in results)
            labelled.Append("## ").Append(member).Append('\n').Append(output).Append("\n\n");

        if (_lead is null)
            return labelled.ToString().TrimEnd();   // no lead: return the members' outputs verbatim

        // Lead synthesis: one call that merges the members' outputs, no tools.
        var convo = new Conversation();
        convo.Add(Message.UserText(
            $"Original task:\n{task}\n\nYour team members each responded independently below. " +
            "Synthesize their responses into one coherent result — reconcile disagreements, keep what's " +
            "strongest, and note anything they missed.\n\n" + labelled));

        var system = _synthesisPrompt ?? "You are the lead of a team of agents. Merge their independent responses into one clear, non-redundant answer.";
        var resp = await _lead.CompleteAsync(system, convo, Array.Empty<ITool>(), _ => { }, ct).ConfigureAwait(false);
        var text = string.Concat(resp.AssistantMessage.Content.OfType<TextBlock>().Select(b => b.Text)).Trim();
        return text.Length == 0 ? labelled.ToString().TrimEnd() : text;
    }
}
