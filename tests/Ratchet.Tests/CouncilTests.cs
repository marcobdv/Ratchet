using CodeStack.Ratchet.Core;
using CodeStack.Ratchet.Tests.Support;
using Xunit;

namespace CodeStack.Ratchet.Tests;

/// <summary>
/// Tier 3: council mode — independent cold personas, a clerk that organizes (never decides),
/// and a Decision Record dropped for the human. The invariants: the clerk sees the perspectives
/// but personas never see the clerk's Brief, and the Brief carries no recommendation (Phase 1).
/// </summary>
public sealed class CouncilToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ratchet-council-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class Persona : ITool
    {
        private readonly string _view;
        public string? SawTask;
        public Persona(string name, string view) { Name = name; _view = view; }
        public string Name { get; }
        public string Description => "persona";
        public string InputSchemaJson => """{"type":"object","properties":{"task":{"type":"string"}},"required":["task"]}""";
        public Task<string> ExecuteAsync(string inputJson, CancellationToken ct)
        {
            SawTask = Json.GetString(inputJson, "task");
            return Task.FromResult(_view);
        }
    }

    private Task<string> Run(CouncilTool council, string decision) =>
        council.ExecuteAsync(System.Text.Json.JsonSerializer.Serialize(new { decision }), CancellationToken.None);

    [Fact]
    public async Task DispatchesPersonasColdWithTheDecision_ThenClerkOrganizes()
    {
        var architect = new Persona("architect", "Build a modular monolith.");
        var skeptic = new Persona("skeptic", "Microservices will sink us in ops cost.");
        // The clerk must receive both perspectives; it returns an organized brief.
        var clerk = new ScriptedLlmClient().Enqueue(ScriptedLlmClient.Text(
            "## Consensus\nStart simple.\n## Contradictions\nMonolith vs services.\n## Blind spots\nData migration."));

        var council = new CouncilTool("arch-council", "desc", new ITool[] { architect, skeptic }, clerk, _dir);
        var result = await Run(council, "monolith or microservices for the new platform?");

        // Each persona saw the decision (cold — only the decision, nothing else).
        Assert.Equal("monolith or microservices for the new platform?", architect.SawTask);
        Assert.Equal("monolith or microservices for the new platform?", skeptic.SawTask);

        // The clerk's transcript carried both locked perspectives.
        var clerkPrompt = ((TextBlock)clerk.CallTranscripts[0][0].Content[0]).Text;
        Assert.Contains("Build a modular monolith", clerkPrompt);
        Assert.Contains("Microservices will sink us", clerkPrompt);
        Assert.Equal(CouncilTool.ClerkPrompt, clerk.SystemPrompts[0]);

        Assert.Contains("Consensus", result);
        Assert.Contains("Decision Record", result);
    }

    [Fact]
    public async Task WritesADecisionRecordTemplate_WithBriefAndLockedPerspectives()
    {
        var p = new Persona("architect", "My locked view: prefer boring tech.");
        var clerk = new ScriptedLlmClient().Enqueue(ScriptedLlmClient.Text("## Consensus\nBoring tech wins."));
        var council = new CouncilTool("c", "d", new ITool[] { p }, clerk, _dir);

        await Run(council, "which datastore?");

        var recordDir = Path.Combine(_dir, ".ratchet", "council");
        var file = Assert.Single(Directory.GetFiles(recordDir, "council-*.md"));
        var content = File.ReadAllText(file);

        // The record is a human template: blank Decision fields + the clerk brief + locked views.
        Assert.Contains("# Decision Record", content);
        Assert.Contains("## Decision", content);
        Assert.Contains("## Risks accepted", content);
        Assert.Contains("# Analysis Brief", content);
        Assert.Contains("Boring tech wins", content);
        Assert.Contains("# Perspectives (locked, independent)", content);
        Assert.Contains("My locked view: prefer boring tech", content);
    }

    [Fact]
    public async Task ClerkFailure_StillWritesTheRecord_WithTheRawPerspectives()
    {
        var p = new Persona("architect", "raw perspective survives");
        var clerk = new ScriptedLlmClient().EnqueueThrow(new InvalidOperationException("clerk down"));
        var council = new CouncilTool("c", "d", new ITool[] { p }, clerk, _dir);

        var result = await Run(council, "x");

        Assert.Contains("clerk synthesis failed", result);
        var file = Assert.Single(Directory.GetFiles(Path.Combine(_dir, ".ratchet", "council"), "council-*.md"));
        Assert.Contains("raw perspective survives", File.ReadAllText(file));   // nothing lost
    }
}

public sealed class CouncilBuilderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ratchet-councilbuild-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void CouncilDefinition_WithBuiltinPersonas_BuildsACouncilTool_OutOfTheBox()
    {
        var cat = new AgentCatalog();
        cat.Add(new AgentDefinition(
            "arch-council", "Deliberate.", Tools: null, Model: null, SystemPrompt: "",
            Members: new[] { "architect", "skeptic", "developer", "domain" }, Mode: "council"));

        var tools = SubAgents.BuildFromCatalog(
            cat, _ => null, _ => new ScriptedLlmClient(), new ScriptedLlmClient(),
            AllowAllGate.Instance, new HashSet<string>(StringComparer.Ordinal), _dir).ToList();

        var council = Assert.Single(tools);
        Assert.IsType<CouncilTool>(council);
        Assert.Equal("arch-council", council.Name);
    }

    [Fact]
    public void TeamVsCouncil_ChosenByMode()
    {
        var cat = new AgentCatalog();
        cat.Add(new AgentDefinition("board", "team", null, null, "merge them",
            Members: new[] { "architect", "skeptic" }, Mode: null));
        cat.Add(new AgentDefinition("council", "council", null, null, "",
            Members: new[] { "architect", "skeptic" }, Mode: "council"));

        var tools = SubAgents.BuildFromCatalog(
            cat, _ => null, _ => new ScriptedLlmClient(), new ScriptedLlmClient(),
            AllowAllGate.Instance, new HashSet<string>(StringComparer.Ordinal), _dir).ToList();

        Assert.IsType<TeamTool>(tools.Single(t => t.Name == "board"));
        Assert.IsType<CouncilTool>(tools.Single(t => t.Name == "council"));
    }
}
