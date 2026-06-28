using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeStack.Ratchet.Core;

namespace CodeStack.Ratchet.Workflow;

/// <summary>
/// The intake classifier: ONE llm call that sizes the task into a <c>work_type</c>,
/// which drives both the phase subset and the per-phase skills. It is the
/// highest-leverage judgment in the pipeline (the one call that can silently lower
/// quality by skipping a needed phase), so:
///   - the choice + reasoning is recorded on the run (auditable skip);
///   - a misclassification degrades gracefully — an unparseable answer falls back to
///     the *most thorough* work_type (most phases) rather than hard-failing or skipping.
/// It's a hint that sets defaults, not a lock: escalation can still re-scope later.
/// </summary>
public sealed class Classifier
{
    private readonly ILlmClient _llm;
    public Classifier(ILlmClient llm) => _llm = llm;

    public async Task<(string workType, string reasoning)> ClassifyAsync(
        string task, WorkflowConfig config, CancellationToken ct)
    {
        var menu = new StringBuilder();
        foreach (var (name, w) in config.WorkTypes)
            menu.Append("- ").Append(name).Append(": runs phases [")
                .Append(string.Join(", ", w.Phases)).Append("]\n");

        var convo = new Conversation();
        convo.Add(Message.UserText(
            "Classify the following coding task into exactly one work_type. Pick the SMALLEST that still " +
            "runs every phase the task genuinely needs — but when unsure, prefer the more thorough one " +
            "(a needless phase is cheap; a skipped necessary phase ships bugs).\n\n" +
            "Available work_types:\n" + menu + "\n" +
            "Task:\n" + task + "\n\n" +
            "Respond with ONLY a JSON object: {\"work_type\":\"<one of the names>\",\"reasoning\":\"<one sentence>\"}"));

        string raw;
        try
        {
            var resp = await _llm.CompleteAsync(
                "You are a precise task-sizing classifier. Output only the requested JSON.",
                convo, Array.Empty<ITool>(), _ => { }, ct);
            raw = string.Concat(resp.AssistantMessage.Content.OfType<TextBlock>().Select(t => t.Text)).Trim();
        }
        catch (Exception ex)
        {
            return (MostThorough(config), $"(fallback: classifier call failed: {ex.Message})");
        }

        if (TryParse(raw, config, out var wt, out var reason))
            return (wt, reason);

        // Degrade gracefully: a misread sizes UP to the most thorough pipeline.
        return (MostThorough(config), "(fallback: unparseable classifier output — defaulted to most thorough)");
    }

    private static bool TryParse(string raw, WorkflowConfig config, out string workType, out string reasoning)
    {
        workType = ""; reasoning = "";
        var json = ExtractJsonObject(raw);
        if (json is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var wt = doc.RootElement.TryGetProperty("work_type", out var w) ? w.GetString() : null;
                reasoning = doc.RootElement.TryGetProperty("reasoning", out var r) ? r.GetString() ?? "" : "";
                if (wt is not null && config.WorkTypes.ContainsKey(wt)) { workType = wt; return true; }
            }
            catch { /* fall through to token scan */ }
        }

        // Last resort: did the model name a work_type in prose? Match on word boundaries (so
        // "fix" doesn't hit inside "bugfix") and only accept an UNambiguous single match — if
        // zero or several names appear, return false so the caller sizes up to MostThorough.
        var named = config.WorkTypes.Keys
            .Where(name => Regex.IsMatch(raw, $@"\b{Regex.Escape(name)}\b", RegexOptions.IgnoreCase))
            .ToList();
        if (named.Count == 1) { workType = named[0]; reasoning = "(parsed work_type from prose)"; return true; }
        return false;
    }

    private static string? ExtractJsonObject(string s)
    {
        var start = s.IndexOf('{');
        var end = s.LastIndexOf('}');
        return start >= 0 && end > start ? s[start..(end + 1)] : null;
    }

    private static string MostThorough(WorkflowConfig config) =>
        config.WorkTypes.OrderByDescending(kv => kv.Value.Phases.Count).First().Key;
}
