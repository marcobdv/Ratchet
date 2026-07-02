using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CodeStack.Ratchet.Workflow;

/// <summary>
/// Loads a workflow YAML into a validated <see cref="WorkflowConfig"/>. The YAML is
/// a plain deserialiser target (DTOs below) — declarative only. All the "can't lie"
/// guarantees from the design live in <see cref="Validate"/>:
///
///   1. each work_type's phases is an ordered subset of the spine AND includes every floor;
///   2. every skill name resolves against the real registry (a typo fails loading);
///   3. every driver/advisor/judge model resolves to a tier;
///   4. advisor pairing — N/A here: we use the reimplemented consult_advisor tool
///      (transcript forwarding), which has no advisor≥driver restriction.
/// </summary>
public static class WorkflowLoader
{
    public static WorkflowConfig Load(string path, IReadOnlyCollection<string>? knownSkills = null)
    {
        if (!File.Exists(path)) throw new WorkflowConfigException($"No workflow file at '{path}'.");
        return Parse(File.ReadAllText(path), knownSkills);
    }

    public static WorkflowConfig Parse(string yaml, IReadOnlyCollection<string>? knownSkills = null)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        RootDto dto;
        try { dto = deserializer.Deserialize<RootDto>(yaml) ?? throw new WorkflowConfigException("Empty workflow file."); }
        catch (WorkflowConfigException) { throw; }
        catch (Exception ex) { throw new WorkflowConfigException($"YAML parse error: {ex.Message}"); }

        var config = Build(dto);
        var errors = Validate(config, dto, knownSkills);
        if (errors.Count > 0) throw new WorkflowConfigException(errors);
        return config;
    }

    // ---- DTO -> domain -----------------------------------------------------

    private static WorkflowConfig Build(RootDto dto)
    {
        var models = (dto.Models ?? new()).ToDictionary(
            kv => kv.Key,
            kv => new ModelTier(kv.Key, kv.Value?.Provider ?? "", kv.Value?.Model ?? ""),
            StringComparer.Ordinal);

        var defaultDriver = dto.Defaults?.Driver ?? "";
        var defaultAdvisor = BuildAdvisor(dto.Defaults?.Advisor, fallback: null);

        var spine = (dto.Spine ?? new()).Select(p => new PhaseSpec(
            Id: p.Id ?? "",
            Skippable: p.Skippable,
            Role: p.Role ?? "",
            DriverTier: string.IsNullOrWhiteSpace(p.Driver) ? defaultDriver : p.Driver!,
            Advisor: ResolvePhaseAdvisor(p.Advisor, defaultAdvisor),
            Tools: p.Tools ?? new List<string>(),
            Gate: BuildGate(p.Gate),
            Escalation: p.Escalation ?? new List<string>())).ToList();

        var workTypes = (dto.WorkTypes ?? new()).ToDictionary(
            kv => kv.Key,
            kv => new WorkTypeSpec(
                kv.Key,
                kv.Value?.Phases ?? new List<string>(),
                (kv.Value?.Skills ?? new()).ToDictionary(
                    s => s.Key,
                    s => (IReadOnlyList<string>)(s.Value ?? new List<string>()),
                    StringComparer.Ordinal),
                (kv.Value?.Models ?? new()).ToDictionary(
                    m => m.Key, m => m.Value ?? "", StringComparer.Ordinal),
                kv.Value?.Promote ?? true),
            StringComparer.Ordinal);

        var skillLoading = new SkillLoading(
            dto.SkillLoading?.Threshold ?? 4,
            dto.SkillLoading?.StrategyAbove ?? "progressive");

        // Promotion ladder (reactive layer). Unset = empty = no promotion (retry same tier).
        var ladder = dto.Defaults?.DriverLadder ?? new List<string>();

        return new WorkflowConfig(
            dto.Version, dto.Name ?? "workflow", dto.Classifier?.Record ?? true,
            models, defaultDriver, defaultAdvisor, skillLoading, spine, workTypes,
            ladder, dto.Defaults?.RecordPromotions ?? true);
    }

    private static AdvisorSpec? ResolvePhaseAdvisor(AdvisorDto? dto, AdvisorSpec? defaults)
    {
        if (dto is null) return null;                       // advisor: null / absent -> none
        if (!string.IsNullOrEmpty(dto.Inherit)) return defaults;  // { inherit: defaults }
        return BuildAdvisor(dto, defaults);
    }

    private static AdvisorSpec? BuildAdvisor(AdvisorDto? dto, AdvisorSpec? fallback)
    {
        if (dto is null) return fallback;
        var tier = string.IsNullOrWhiteSpace(dto.Model) ? fallback?.ModelTier : dto.Model;
        if (string.IsNullOrWhiteSpace(tier)) return null;
        return new AdvisorSpec(
            tier!,
            dto.ConsultWhen ?? fallback?.ConsultWhen ?? "",
            dto.MaxConsults ?? fallback?.MaxConsults ?? 3);
    }

    private static GateSpec BuildGate(GateDto? g)
    {
        if (g is null) return GateSpec.None;
        var kind = (g.Type ?? "none").ToLowerInvariant() switch
        {
            "command" => GateKind.Command,
            "judge" => GateKind.Judge,
            _ => GateKind.None,
        };
        if (kind == GateKind.None) return GateSpec.None;
        return new GateSpec(kind, g.Run, g.Agent, g.FreshContext, g.Model,
            string.IsNullOrWhiteSpace(g.OnFail) ? (kind == GateKind.Judge ? "loop" : "stop") : g.OnFail!,
            g.MaxLoops ?? 3);
    }

    // ---- validation --------------------------------------------------------

    private static List<string> Validate(WorkflowConfig c, RootDto dto, IReadOnlyCollection<string>? knownSkills)
    {
        var e = new List<string>();
        if (c.Version < 1) e.Add("version must be >= 1.");
        if (c.Spine.Count == 0) e.Add("spine is empty.");
        if (c.Models.Count == 0) e.Add("no model tiers defined.");
        if (c.WorkTypes.Count == 0) e.Add("no work_types defined.");

        foreach (var (name, tier) in c.Models)
        {
            if (string.IsNullOrWhiteSpace(tier.Provider)) e.Add($"model tier '{name}' has no provider.");
            if (string.IsNullOrWhiteSpace(tier.Model)) e.Add($"model tier '{name}' has no model.");
        }

        bool TierExists(string? t) => !string.IsNullOrEmpty(t) && c.Models.ContainsKey(t!);

        // The classifier and any phase without an explicit driver resolve through
        // defaults.driver — an absent/typoed value used to surface as a raw
        // KeyNotFoundException at classify time instead of a load error.
        if (!TierExists(c.DefaultDriverTier))
            e.Add(string.IsNullOrEmpty(c.DefaultDriverTier)
                ? "defaults.driver is missing."
                : $"defaults.driver '{c.DefaultDriverTier}' is not a defined model.");
        if (c.DefaultAdvisor is not null && !TierExists(c.DefaultAdvisor.ModelTier))
            e.Add($"defaults.advisor.model '{c.DefaultAdvisor.ModelTier}' is not a defined model.");

        var spineIds = new HashSet<string>(c.Spine.Select(p => p.Id), StringComparer.Ordinal);
        if (c.Spine.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != c.Spine.Count)
            e.Add("spine has duplicate phase ids.");

        // Rule 3 (tiers) + gate/escalation sanity, per phase.
        foreach (var p in c.Spine)
        {
            if (string.IsNullOrWhiteSpace(p.Id)) { e.Add("a spine phase has no id."); continue; }
            if (string.IsNullOrWhiteSpace(p.Role)) e.Add($"phase '{p.Id}' has no role.");
            if (!TierExists(p.DriverTier)) e.Add($"phase '{p.Id}' driver tier '{p.DriverTier}' is not a defined model.");
            if (p.Advisor is not null && !TierExists(p.Advisor.ModelTier))
                e.Add($"phase '{p.Id}' advisor tier '{p.Advisor.ModelTier}' is not a defined model.");

            switch (p.Gate.Type)
            {
                case GateKind.Command when string.IsNullOrWhiteSpace(p.Gate.Run):
                    e.Add($"phase '{p.Id}' command gate has no 'run'."); break;
                case GateKind.Judge when !TierExists(p.Gate.ModelTier):
                    e.Add($"phase '{p.Id}' judge gate model '{p.Gate.ModelTier}' is not a defined model."); break;
                case GateKind.Judge when string.IsNullOrWhiteSpace(p.Gate.JudgeAgent):
                    e.Add($"phase '{p.Id}' judge gate has no 'agent'."); break;
            }
            if (p.Gate.Type != GateKind.None)
            {
                var f = p.Gate.OnFail;
                if (f != "stop" && f != "loop" && !spineIds.Contains(f))
                    e.Add($"phase '{p.Id}' gate on_fail '{f}' is not 'stop', 'loop', or a spine phase.");
                if ((f == "loop" || p.Gate.Type == GateKind.Judge) && p.Gate.MaxLoops < 1)
                    e.Add($"phase '{p.Id}' gate needs max_loops >= 1.");
            }
            foreach (var t in p.Escalation)
                if (!spineIds.Contains(t)) e.Add($"phase '{p.Id}' escalation target '{t}' is not a spine phase.");
        }

        // Promotion ladder: every rung must resolve to a defined tier.
        foreach (var t in c.DriverLadder)
            if (!TierExists(t)) e.Add($"driver_ladder tier '{t}' is not a defined model.");

        var floors = new HashSet<string>(c.FloorPhases, StringComparer.Ordinal);

        // Rules 1 + 2, per work_type.
        foreach (var (name, wt) in c.WorkTypes)
        {
            // Rule 1a: every phase id is a spine id.
            foreach (var id in wt.Phases)
                if (!spineIds.Contains(id)) e.Add($"work_type '{name}' lists phase '{id}' which is not in the spine.");

            // Rule 1b: ordered subset — phases appear in spine order.
            var indices = wt.Phases.Where(spineIds.Contains).Select(c.SpineIndex).ToList();
            for (var i = 1; i < indices.Count; i++)
                if (indices[i] <= indices[i - 1])
                { e.Add($"work_type '{name}' phases are not in spine order."); break; }

            // Rule 1c: includes every floor.
            foreach (var floor in floors)
                if (!wt.Phases.Contains(floor)) e.Add($"work_type '{name}' must include floor phase '{floor}'.");

            // Rule 2: skill keys are phases of this work_type; skill names resolve.
            foreach (var (phaseId, skills) in wt.Skills)
            {
                if (!wt.Phases.Contains(phaseId))
                    e.Add($"work_type '{name}' lists skills for phase '{phaseId}' which it does not run.");
                if (knownSkills is not null)
                    foreach (var s in skills)
                        if (!knownSkills.Contains(s))
                            e.Add($"work_type '{name}' phase '{phaseId}' references unknown skill '{s}'.");
            }

            // Predictive tier overrides: model keys are phases this work_type runs; values resolve.
            foreach (var (phaseId, tier) in wt.Models)
            {
                if (!wt.Phases.Contains(phaseId))
                    e.Add($"work_type '{name}' sets a model for phase '{phaseId}' which it does not run.");
                if (!TierExists(tier))
                    e.Add($"work_type '{name}' phase '{phaseId}' model '{tier}' is not a defined model.");
            }

            // A gate's on_fail must route to a phase this work_type actually runs — otherwise a
            // red gate would splice an omitted phase back in, violating the ordered-subset rule.
            // (Escalation targets are intentionally allowed to be omitted phases — that's the
            // documented "this proved bigger, re-enter an earlier phase" lever.)
            foreach (var phaseId in wt.Phases)
            {
                var of = c.Phase(phaseId)?.Gate.OnFail;
                if (of is not null && of != "loop" && of != "stop" && spineIds.Contains(of) && !wt.Phases.Contains(of))
                    e.Add($"work_type '{name}' phase '{phaseId}' gate on_fail routes to '{of}', which this work_type does not run.");
            }

            // Reactive promotion can only climb if each runnable phase's starting tier is a rung on
            // the ladder; otherwise a red gate would loop at the same model and fail silently.
            if (wt.Promote && c.DriverLadder.Count > 0)
                foreach (var phaseId in wt.Phases.Where(spineIds.Contains))
                {
                    var tier = c.StartingTier(wt, phaseId);
                    if (TierExists(tier) && !c.DriverLadder.Contains(tier))
                        e.Add($"work_type '{name}' phase '{phaseId}' starts on tier '{tier}', which is not on " +
                              $"driver_ladder [{string.Join(", ", c.DriverLadder)}] — reactive promotion could never " +
                              "climb from it. Add it to driver_ladder, change the starting tier, or set promote: false.");
                }
        }
        return e;
    }

    // ---- YAML DTOs (private; never escape the loader) -----------------------

    private sealed class RootDto
    {
        public int Version { get; set; }
        public string? Name { get; set; }
        public ClassifierDto? Classifier { get; set; }
        public Dictionary<string, ModelDto>? Models { get; set; }
        public DefaultsDto? Defaults { get; set; }
        public SkillLoadingDto? SkillLoading { get; set; }
        public List<PhaseDto>? Spine { get; set; }
        public Dictionary<string, WorkTypeDto>? WorkTypes { get; set; }
    }

    private sealed class ClassifierDto { public bool Record { get; set; } = true; }
    private sealed class ModelDto { public string? Provider { get; set; } public string? Model { get; set; } }
    private sealed class DefaultsDto
    {
        public string? Driver { get; set; }
        public AdvisorDto? Advisor { get; set; }
        public List<string>? DriverLadder { get; set; }
        public bool? RecordPromotions { get; set; }
    }

    private sealed class AdvisorDto
    {
        public string? Inherit { get; set; }
        public string? Model { get; set; }
        public string? ConsultWhen { get; set; }
        public int? MaxConsults { get; set; }
    }

    private sealed class SkillLoadingDto { public int Threshold { get; set; } = 4; public string? StrategyAbove { get; set; } }

    private sealed class PhaseDto
    {
        public string? Id { get; set; }
        public bool Skippable { get; set; }
        public string? Role { get; set; }
        public string? Driver { get; set; }
        public AdvisorDto? Advisor { get; set; }
        public List<string>? Tools { get; set; }
        public GateDto? Gate { get; set; }
        public List<string>? Escalation { get; set; }
    }

    private sealed class GateDto
    {
        public string? Type { get; set; }
        public string? Run { get; set; }
        public string? Agent { get; set; }
        public bool FreshContext { get; set; }
        public string? Model { get; set; }
        public string? OnFail { get; set; }
        public int? MaxLoops { get; set; }
    }

    private sealed class WorkTypeDto
    {
        public List<string>? Phases { get; set; }
        public Dictionary<string, List<string>>? Skills { get; set; }
        public Dictionary<string, string>? Models { get; set; }   // phase -> starting tier
        public bool? Promote { get; set; }                        // cap: false = never auto-promote
    }
}
