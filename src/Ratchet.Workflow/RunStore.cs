using System.Text.Json;
using CodeStack.Ratchet.Core;

namespace CodeStack.Ratchet.Workflow;

/// <summary>One handed-over working-set doc, persisted for resume.</summary>
public sealed class HandoffEntry
{
    public string Phase { get; set; } = "";
    public string Doc { get; set; } = "";
}

/// <summary>
/// The durable record of a workflow run — and its resume checkpoint in one. It carries
/// both the audit trace (classification + reasoning, event log, per-tier cost) AND the
/// scheduler's loop state (plan, index, loop/attempt counters, accumulated handoff,
/// prior session), so a run that was interrupted mid-flight can be continued from the
/// last completed phase. Persisting this is what finally makes "a bad skip is diffable
/// after the fact" true, instead of a console line that scrolls away.
/// </summary>
public sealed class RunSnapshot
{
    public string RunId { get; set; } = "";
    public string Task { get; set; } = "";
    public string WorkflowFile { get; set; } = "";
    public string Status { get; set; } = "running";   // running | completed | failed
    public string FailReason { get; set; } = "";
    public string? WorkType { get; set; }
    public string? ClassifierReasoning { get; set; }
    public string UpdatedUtc { get; set; } = "";

    // ---- scheduler state (for resume) ----
    public List<string> Plan { get; set; } = new();
    public int Idx { get; set; }
    public Dictionary<string, int> LoopCounts { get; set; } = new();
    public Dictionary<string, int> Attempt { get; set; } = new();
    public int Escalations { get; set; }
    public List<HandoffEntry> Handoff { get; set; } = new();
    public string? PriorSession { get; set; }
    public Dictionary<string, string> CurrentTier { get; set; } = new();   // reactive-layer promotions
    public Dictionary<string, string> GateFeedback { get; set; } = new();  // phase -> why its last gate went red

    // ---- recording ----
    public List<RunEvent> Events { get; set; } = new();
    public CostTally Cost { get; set; } = new();

    public bool IsResumable => Status == "running";
}

/// <summary>Persists run snapshots. Mirrors <c>ISessionStore</c>: a swappable backend seam.</summary>
public interface IRunStore
{
    void Save(RunSnapshot snapshot);
    RunSnapshot? Load(string runId);
    IReadOnlyList<RunSnapshot> List();   // newest first; full snapshots (runs are small)
}

/// <summary>One JSON file per run under {baseDir}/.ratchet/runs/{runId}.json.</summary>
public sealed class FileRunStore : IRunStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _dir;

    public FileRunStore(string baseDir)
    {
        _dir = Path.Combine(baseDir, ".ratchet", "runs");
        Directory.CreateDirectory(_dir);
    }

    public void Save(RunSnapshot snapshot)
    {
        snapshot.UpdatedUtc = DateTime.UtcNow.ToString("o");
        var path = Path.Combine(_dir, SessionId.Validate(snapshot.RunId) + ".json");
        // Write to a temp file then atomically replace, so a crash mid-write can't truncate
        // the only resume checkpoint (which is the whole point of writing it before each phase).
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, Json));
        File.Move(tmp, path, overwrite: true);
    }

    public RunSnapshot? Load(string runId)
    {
        if (!SessionId.IsValid(runId)) return null;
        var path = Path.Combine(_dir, runId + ".json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<RunSnapshot>(File.ReadAllText(path), Json); }
        catch { return null; }
    }

    public IReadOnlyList<RunSnapshot> List()
    {
        var runs = new List<RunSnapshot>();
        foreach (var path in Directory.EnumerateFiles(_dir, "*.json"))
        {
            try
            {
                var snap = JsonSerializer.Deserialize<RunSnapshot>(File.ReadAllText(path), Json);
                if (snap is not null) runs.Add(snap);
            }
            catch { /* skip a malformed run file */ }
        }
        runs.Sort((a, b) => string.CompareOrdinal(b.UpdatedUtc, a.UpdatedUtc));
        return runs;
    }
}
