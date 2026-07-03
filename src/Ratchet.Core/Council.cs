using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodeStack.Ratchet.Core;

/// <summary>
/// Council mode (the top tier of the delegation family) — a deliberation harness for
/// architectural decisions with no prior art. Independent persona agents argue from distinct
/// perspectives in <b>cold, separate</b> context, a synthesizer (the "clerk") mechanically
/// organizes their <b>locked</b> outputs into an Analysis Brief, and a Decision Record template
/// is dropped for a human to complete. Unlike a <see cref="TeamTool"/>, a council does not merge
/// into an answer — it organizes disagreement and emits a <i>decision</i> for the human.
///
/// Two invariants protect independence, both by construction here: the synthesizer runs only
/// after every perspective is collected (locked), and the personas never see the Brief. This is
/// Phase 1 ("clerk"): the Brief carries no recommendation. The design reserves a Phase 2 where a
/// recommendation renders <i>after</i> the contradictions (so the human forms a view first — the
/// same anchoring guard that protects the personas from each other, now protecting the human).
/// </summary>
public sealed class CouncilTool : ITool
{
    private readonly Func<IReadOnlyList<string>?, IReadOnlyList<ITool>> _roster;
    private readonly bool _adHoc;
    private readonly ILlmClient _clerk;
    private readonly string _outputDir;

    /// <summary>Fixed-roster council (defined in a file): the members are locked at build time.</summary>
    public CouncilTool(string name, string description, IReadOnlyList<ITool> personas,
        ILlmClient clerk, string workspaceDir)
        : this(name, description, _ => personas, adHoc: false, clerk, workspaceDir) { }

    /// <summary>
    /// Ad-hoc council: the roster is chosen per call. <paramref name="roster"/> maps the call-time
    /// <c>members</c> names (or null when omitted) to persona tools — so the caller can convene a
    /// council on the spot without a definition file.
    /// </summary>
    public CouncilTool(string name, string description,
        Func<IReadOnlyList<string>?, IReadOnlyList<ITool>> roster, bool adHoc,
        ILlmClient clerk, string workspaceDir)
    {
        Name = name;
        Description = description;
        _roster = roster;
        _adHoc = adHoc;
        _clerk = clerk;
        _outputDir = Path.Combine(workspaceDir, ".ratchet", "council");
    }

    public string Name { get; }
    public string Description { get; }

    public string InputSchemaJson => _adHoc
        ? """
          {"type":"object","properties":{"decision":{"type":"string","description":"The architectural decision to deliberate, with all context (constraints, options, what makes it novel). Each persona sees only this."},"members":{"type":"array","items":{"type":"string"},"description":"The roster for this deliberation — names of defined agents and/or built-in personas (architect, skeptic, developer, domain). Omit to use the default four personas."}},"required":["decision"]}
          """
        : """
          {"type":"object","properties":{"decision":{"type":"string","description":"The architectural decision to deliberate, with all context (constraints, options, what makes it novel). Each persona sees only this."}},"required":["decision"]}
          """;

    public async Task<string> ExecuteAsync(string inputJson, CancellationToken ct)
    {
        var decision = Json.GetStringOrNull(inputJson, "decision") ?? Json.GetStringOrNull(inputJson, "task") ?? "";
        if (string.IsNullOrWhiteSpace(decision)) return "council: no 'decision' provided.";
        if (Delegation.AtLimit) return $"(council refused: nesting limit {Delegation.MaxDepth} reached)";

        // Ad-hoc councils read the roster from the call; fixed ones ignore it.
        IReadOnlyList<string>? memberNames = null;
        if (_adHoc)
        {
            using var doc = JsonDocument.Parse(inputJson);
            if (doc.RootElement.TryGetProperty("members", out var m) && m.ValueKind == JsonValueKind.Array)
                memberNames = m.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();
        }

        var personas = _roster(memberNames);
        if (personas.Count == 0)
            return "council: no members resolved — name defined agents or built-in personas " +
                   "(architect, skeptic, developer, domain).";

        using var _ = Delegation.Enter();

        // 1. Dispatch the personas cold, in parallel, then LOCK (nothing sees another's view).
        var perspectives = await SubAgents.DispatchParallelAsync(personas, decision, ct);

        var perspectivesText = new StringBuilder();
        foreach (var (persona, view) in perspectives)
            perspectivesText.Append("## ").Append(persona).Append('\n').Append(view).Append("\n\n");

        // 2. Synthesis pass (clerk): organize the locked perspectives into the Analysis Brief.
        //    No recommendation — the clerk organizes, the human decides (Phase 1).
        var convo = new Conversation();
        convo.Add(Message.UserText(
            $"Decision under deliberation:\n{decision}\n\n" +
            $"The council members each argued independently below (they did not see each other):\n\n{perspectivesText}"));
        var brief = "";
        try
        {
            var resp = await _clerk.CompleteAsync(ClerkPrompt, convo, Array.Empty<ITool>(), _ => { }, ct).ConfigureAwait(false);
            brief = string.Concat(resp.AssistantMessage.Content.OfType<TextBlock>().Select(b => b.Text)).Trim();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { brief = $"(clerk synthesis failed: {ex.Message} — the locked perspectives are below, unorganized)"; }

        // 3. Drop the Decision Record template (Brief + blank record) for the human.
        var id = SessionId.NewId();
        var recordPath = WriteRecord(id, decision, brief, perspectivesText.ToString());

        return new StringBuilder()
            .Append("Council deliberation complete (").Append(perspectives.Count).Append(" perspectives).\n\n")
            .Append(brief).Append("\n\n")
            .Append("A Decision Record template (this brief + the locked perspectives) was written to:\n  ")
            .Append(recordPath).Append('\n')
            .Append("The decision is the human's — fill in the Decision Record; the council only organizes.")
            .ToString();
    }

    private string WriteRecord(string id, string decision, string brief, string perspectives)
    {
        Directory.CreateDirectory(_outputDir);
        var path = Path.Combine(_outputDir, $"council-{id}.md");
        var title = decision.ReplaceLineEndings(" ").Trim();
        if (title.Length > 80) title = title[..80] + "…";

        var doc = new StringBuilder()
            .Append("<!-- ratchet:council id=").Append(id)
            .Append(" created=").Append(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)).Append(" -->\n\n")
            .Append("# Decision Record — ").Append(title).Append("\n\n")
            .Append("> Written by the human. The Analysis Brief below organizes the council's locked\n")
            .Append("> perspectives; you hold the decision. Keep this under ~30 lines — a thinking tool.\n\n")
            .Append("## Decision\n(one sentence)\n\n")
            .Append("## Rationale\n- \n\n")
            .Append("## Ruled out\n- \n\n")
            .Append("## Risks accepted\n- \n\n")
            .Append("## Open questions\n- \n\n")
            .Append("---\n\n")
            .Append("# Analysis Brief (council clerk — organized, not decided)\n\n")
            .Append(brief).Append("\n\n")
            .Append("---\n\n")
            .Append("# Perspectives (locked, independent)\n\n")
            .Append(perspectives)
            .ToString();

        File.WriteAllText(path, doc);
        return path;
    }

    internal const string ClerkPrompt =
        "You are the council CLERK. You did not participate in the deliberation; you organize it. " +
        "Below are independent, LOCKED perspectives on an architectural decision — each author argued " +
        "without seeing the others. Produce an Analysis Brief with EXACTLY these five sections and " +
        "nothing else. Do NOT add a recommendation or decision of your own; organizing is your entire job.\n\n" +
        "## Consensus\nWhere perspectives independently agreed.\n\n" +
        "## Contradictions\nDirect conflicts, stated as opposing claims (name who holds each side).\n\n" +
        "## Partial coverage\nPoints only one perspective raised that the others never addressed.\n\n" +
        "## Unique insights\nA novel angle worth elevating.\n\n" +
        "## Blind spots\nWhat NO perspective covered — gaps the decision still needs to face.";
}

/// <summary>
/// The built-in council personas (Marco's default roster). Each is a cold, independent voice with
/// a distinct lens. A project can override any of them by defining an agent of the same name
/// (e.g. to pin it to a specific model for the "Council of Reeds" multi-model path).
/// </summary>
public static class CouncilPersonas
{
    public sealed record Persona(string Name, string Lens, string Prompt);

    public static readonly IReadOnlyList<Persona> Default = new[]
    {
        Make("architect", "structure, boundaries, and long-term maintainability"),
        Make("skeptic", "simplicity, hidden costs, and what has failed here before"),
        Make("developer", "implementation reality, PR shape, and the next developer's legibility"),
        Make("domain", "domain semantics, naming, and cross-product alignment"),
    };

    public static bool IsBuiltin(string name) =>
        Default.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public static Persona? Find(string name) =>
        Default.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Build a built-in persona as a cold, tool-less <see cref="DelegateTool"/> on the given
    /// model, or null if the name isn't a built-in persona.</summary>
    public static DelegateTool? BuildDelegate(string name, ILlmClient llm)
    {
        var p = Find(name);
        return p is null ? null : new DelegateTool(p.Name, $"Council persona: {p.Lens}.", p.Prompt, llm, Array.Empty<ITool>());
    }

    /// <summary>
    /// A roster resolver for an ad-hoc council: given the call-time member names (or null for the
    /// default four personas), resolve each to a tool — a defined agent by name first, else a
    /// built-in persona on <paramref name="personaLlm"/>. Names that resolve to neither are dropped.
    /// </summary>
    public static Func<IReadOnlyList<string>?, IReadOnlyList<ITool>> Roster(
        Func<string, ITool?> resolveAgent, ILlmClient personaLlm) =>
        names =>
        {
            var wanted = names is { Count: > 0 } ? names : Default.Select(p => p.Name).ToList();
            var tools = new List<ITool>();
            foreach (var n in wanted)
            {
                var tool = resolveAgent(n) ?? BuildDelegate(n, personaLlm);
                if (tool is not null) tools.Add(tool);
            }
            return tools;
        };

    private static Persona Make(string name, string lens) => new(name, lens,
        $"You are the **{name}** on a deliberation council weighing an architectural decision that has no " +
        $"reference implementation to copy. Your lens is {lens}.\n\n" +
        "You are arguing INDEPENDENTLY: you have not seen any other council member's view, and you must not " +
        "soften toward a consensus that may not exist — a genuine disagreement is more useful than false " +
        "agreement. Take a clear stance. Give: your position in one line, the strongest reasons for it, the " +
        "tradeoffs you would accept, and what you would refuse outright. Be concrete; cite specifics over " +
        "generalities. Do not try to write the final decision — that is the human's job; argue your corner well.");
}
