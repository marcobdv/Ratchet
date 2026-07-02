using CodeStack.Ratchet.Workflow;
using Xunit;

namespace CodeStack.Ratchet.Tests;

/// <summary>
/// The loader's "can't lie" validation rules from docs/workflow-orchestration.md:
/// ordered-subset + floors, skill resolution, tier resolution — plus the
/// driver-ladder rules from docs/model-routing.md.
/// </summary>
public sealed class WorkflowLoaderTests
{
    private const string ValidYaml = """
        version: 1
        name: test
        models:
          cheap:    { provider: local,      model: small }
          frontier: { provider: anthropic,  model: big }
        defaults:
          driver: cheap
        spine:
          - { id: plan,      skippable: true,  role: plan it }
          - { id: implement, skippable: true,  role: build it }
          - id: verify
            skippable: false
            role: prove it
            gate: { type: command, run: "dotnet build", on_fail: loop, max_loops: 2 }
          - id: review
            skippable: false
            role: judge it
            gate: { type: judge, agent: reviewer, model: frontier, on_fail: loop, max_loops: 2 }
        work_types:
          trivial: { phases: [verify, review] }
          feature: { phases: [plan, implement, verify, review] }
        """;

    private static void AssertRejects(string yaml, string errorFragment)
    {
        var ex = Assert.Throws<WorkflowConfigException>(() => WorkflowLoader.Parse(yaml));
        Assert.Contains(ex.Errors, e => e.Contains(errorFragment, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidConfig_Parses_WithResolvedTiersAndFloors()
    {
        var c = WorkflowLoader.Parse(ValidYaml);
        Assert.Equal(new[] { "verify", "review" }, c.FloorPhases);
        Assert.Equal("cheap", c.Phase("plan")!.DriverTier);        // defaults.driver inherited
        Assert.Equal(GateKind.Command, c.Phase("verify")!.Gate.Type);
        Assert.Equal(GateKind.Judge, c.Phase("review")!.Gate.Type);
    }

    // ---- rule 1: ordered subset of the spine, floors included ----------------

    [Fact]
    public void WorkTypePhaseNotInSpine_IsRejected() =>
        AssertRejects(ValidYaml.Replace("[verify, review]", "[deploy, verify, review]"),
            "not in the spine");

    [Fact]
    public void WorkTypePhasesOutOfSpineOrder_AreRejected() =>
        AssertRejects(ValidYaml.Replace("[plan, implement, verify, review]", "[implement, plan, verify, review]"),
            "not in spine order");

    [Fact]
    public void WorkTypeMissingAFloor_IsRejected() =>
        AssertRejects(ValidYaml.Replace("trivial: { phases: [verify, review] }", "trivial: { phases: [verify] }"),
            "must include floor phase 'review'");

    // ---- rule 2: skills resolve against the registry -------------------------

    [Fact]
    public void UnknownSkill_IsRejected_WhenRegistryProvided()
    {
        var yaml = ValidYaml.Replace(
            "feature: { phases: [plan, implement, verify, review] }",
            "feature: { phases: [plan, implement, verify, review], skills: { plan: [no-such-skill] } }");
        var ex = Assert.Throws<WorkflowConfigException>(
            () => WorkflowLoader.Parse(yaml, knownSkills: new[] { "real-skill" }));
        Assert.Contains(ex.Errors, e => e.Contains("unknown skill 'no-such-skill'"));
    }

    [Fact]
    public void SkillsForAPhaseTheWorkTypeDoesNotRun_AreRejected() =>
        AssertRejects(ValidYaml.Replace(
                "trivial: { phases: [verify, review] }",
                "trivial: { phases: [verify, review], skills: { plan: [] } }"),
            "which it does not run");

    // ---- rule 3: every tier reference resolves --------------------------------

    [Fact]
    public void UnknownDriverTier_IsRejected() =>
        AssertRejects(ValidYaml.Replace("role: build it }", "role: build it, driver: warp }"),
            "driver tier 'warp' is not a defined model");

    [Fact]
    public void UnknownJudgeGateModel_IsRejected() =>
        AssertRejects(ValidYaml.Replace("model: frontier, on_fail: loop", "model: galaxy, on_fail: loop"),
            "judge gate model 'galaxy' is not a defined model");

    [Fact]
    public void UnknownWorkTypeModelOverride_IsRejected() =>
        AssertRejects(ValidYaml.Replace(
                "trivial: { phases: [verify, review] }",
                "trivial: { phases: [verify, review], models: { verify: nope } }"),
            "model 'nope' is not a defined model");

    // ---- gate sanity ----------------------------------------------------------

    [Fact]
    public void CommandGateWithoutRun_IsRejected() =>
        AssertRejects(ValidYaml.Replace("""gate: { type: command, run: "dotnet build", on_fail: loop, max_loops: 2 }""",
                "gate: { type: command, on_fail: loop, max_loops: 2 }"),
            "command gate has no 'run'");

    [Fact]
    public void GateOnFailToUnknownPhase_IsRejected() =>
        AssertRejects(ValidYaml.Replace("""run: "dotnet build", on_fail: loop""", """run: "dotnet build", on_fail: nowhere"""),
            "not 'stop', 'loop', or a spine phase");

    [Fact]
    public void GateOnFailRoutingToAPhaseTheWorkTypeOmits_IsRejected()
    {
        // trivial omits implement, but verify's red gate routes there — a red gate
        // would splice an omitted phase back in, violating the ordered-subset rule.
        AssertRejects(ValidYaml.Replace("""run: "dotnet build", on_fail: loop""", """run: "dotnet build", on_fail: implement"""),
            "routes to 'implement', which this work_type does not run");
    }

    // ---- driver ladder (docs/model-routing.md) --------------------------------

    private const string LadderYaml = """
        version: 1
        name: ladder
        models:
          cheap:    { provider: local,     model: small }
          mid:      { provider: openai,    model: medium }
          frontier: { provider: anthropic, model: big }
        defaults:
          driver: cheap
          driver_ladder: [cheap, mid, frontier]
        spine:
          - { id: verify, skippable: false, role: v }
          - { id: review, skippable: false, role: w }
        work_types:
          trivial: { phases: [verify, review] }
        """;

    [Fact]
    public void LadderRungNotATier_IsRejected() =>
        AssertRejects(LadderYaml.Replace("[cheap, mid, frontier]", "[cheap, mid, warp]"),
            "driver_ladder tier 'warp' is not a defined model");

    [Fact]
    public void StartingTierOffTheLadder_WithPromotion_IsRejected() =>
        AssertRejects(LadderYaml.Replace("[cheap, mid, frontier]", "[mid, frontier]"),
            "reactive promotion could never climb");

    [Fact]
    public void StartingTierOffTheLadder_WithPromoteFalse_IsAccepted()
    {
        var c = WorkflowLoader.Parse(LadderYaml
            .Replace("[cheap, mid, frontier]", "[mid, frontier]")
            .Replace("trivial: { phases: [verify, review] }",
                     "trivial: { phases: [verify, review], promote: false }"));
        Assert.False(c.WorkTypes["trivial"].Promote);
    }

    [Fact]
    public void PromoteTier_ClimbsOneRung_AndSticksAtTheTop()
    {
        var c = WorkflowLoader.Parse(LadderYaml);
        Assert.Equal("mid", c.PromoteTier("cheap"));
        Assert.Equal("frontier", c.PromoteTier("mid"));
        Assert.Equal("frontier", c.PromoteTier("frontier")); // top rung: keep looping there
        Assert.Equal("off-ladder", c.PromoteTier("off-ladder")); // unknown: unchanged
    }

    [Fact]
    public void StartingTier_ResolvesMostSpecificFirst()
    {
        var c = WorkflowLoader.Parse("""
            version: 1
            name: t
            models:
              cheap:    { provider: local, model: s }
              mid:      { provider: local, model: m }
              frontier: { provider: local, model: b }
            defaults: { driver: cheap }
            spine:
              - { id: verify, skippable: false, role: v, driver: mid }
              - { id: review, skippable: false, role: w }
            work_types:
              feature: { phases: [verify, review], models: { verify: frontier } }
            """);
        var wt = c.WorkTypes["feature"];
        Assert.Equal("frontier", c.StartingTier(wt, "verify")); // work_type override wins
        Assert.Equal("cheap", c.StartingTier(wt, "review"));    // falls to defaults.driver
    }

    // ---- structural basics -----------------------------------------------------

    [Fact]
    public void EmptySpine_DuplicatePhaseIds_AndMissingModels_AreRejected()
    {
        AssertRejects("version: 1\nname: x\nspine: []\n", "spine is empty");
        AssertRejects(ValidYaml.Replace("id: plan", "id: implement"), "duplicate phase ids");
        AssertRejects("""
            version: 1
            name: x
            models: {}
            spine: [ { id: verify, skippable: false, role: v } ]
            work_types: { t: { phases: [verify] } }
            """, "no model tiers defined");
    }

    [Fact]
    public void GarbageYaml_ThrowsWorkflowConfigException_NotYamlDotNetInternals() =>
        Assert.Throws<WorkflowConfigException>(() => WorkflowLoader.Parse("spine: ["));

    [Fact]
    public void MissingDefaultsDriver_IsRejectedAtLoadTime()
    {
        // Even with every phase driver explicit, the classifier resolves through
        // defaults.driver — absent used to be a KeyNotFoundException at classify time.
        AssertRejects("""
            version: 1
            name: t
            models:
              cheap: { provider: local, model: s }
            spine:
              - { id: verify, skippable: false, role: v, driver: cheap }
            work_types:
              t: { phases: [verify] }
            """, "defaults.driver");
    }

    [Fact]
    public void TypoedDefaultsAdvisorModel_IsRejectedAtLoadTime()
    {
        AssertRejects(ValidYaml.Replace("defaults:\n  driver: cheap",
            "defaults:\n  driver: cheap\n  advisor: { model: warp9 }"),
            "defaults.advisor.model 'warp9'");
    }

    [Fact]
    public void ShippedWorkflow_RatchetDevYaml_StillValidates()
    {
        // Guards the repo's own workflow file against schema drift.
        var path = Path.Combine(FindRepoRoot(), "workflows", "ratchet-dev.yaml");
        var c = WorkflowLoader.Load(path);
        Assert.NotEmpty(c.Spine);
        Assert.NotEmpty(c.WorkTypes);
        Assert.Contains("verify", c.FloorPhases);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Ratchet.sln")))
            dir = dir.Parent!;
        return dir?.FullName ?? throw new InvalidOperationException("Ratchet.sln not found above test bin dir.");
    }
}
