using System.Text;
using System.Text.Json;

namespace CodeStack.Ratchet.Core;

/// <summary>
/// A lightweight planning tool. Long-horizon tasks drift when the model holds the
/// plan only in prose; <c>update_plan</c> gives it an explicit, revisable checklist
/// it re-sends in full each time (the same shape as Claude Code's todo list). The
/// plan is session state the model owns — Ratchet just stores the latest version
/// and renders it back, so the current plan is always visible in the transcript
/// and to the user via the observer.
/// </summary>
public sealed class PlanTool : ITool
{
    public sealed record Step(string Text, string Status);

    private IReadOnlyList<Step> _steps = Array.Empty<Step>();

    /// <summary>The plan as last set by the model (for a CLI to render if it wants).</summary>
    public IReadOnlyList<Step> Steps => _steps;

    public string Name => "update_plan";

    public string Description =>
        "Record or update your task plan as an ordered checklist. Pass the FULL list every time " +
        "(it replaces the previous plan). Mark exactly one step in_progress while you work on it, " +
        "and flip steps to done as you complete them. Use this for any task of more than a couple " +
        "of steps so the plan stays explicit and revisable.";

    public string InputSchemaJson => """
        {"type":"object","properties":{"steps":{"type":"array","description":"The full ordered plan.","items":{"type":"object","properties":{"step":{"type":"string","description":"What this step does."},"status":{"type":"string","enum":["pending","in_progress","done"],"description":"Step status."}},"required":["step","status"]}}},"required":["steps"]}
        """;

    public Task<string> ExecuteAsync(string inputJson, CancellationToken ct)
    {
        var steps = new List<Step>();
        using (var doc = JsonDocument.Parse(inputJson))
        {
            if (!doc.RootElement.TryGetProperty("steps", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return Task.FromResult("No steps provided; nothing changed.");
            foreach (var s in arr.EnumerateArray())
            {
                var text = s.TryGetProperty("step", out var t) ? t.GetString() ?? "" : "";
                var status = s.TryGetProperty("status", out var st) ? st.GetString() ?? "pending" : "pending";
                if (text.Length > 0) steps.Add(new Step(text, Normalize(status)));
            }
        }

        _steps = steps;
        return Task.FromResult(Render(steps));
    }

    private static string Normalize(string status) => status.ToLowerInvariant() switch
    {
        "in_progress" or "in-progress" or "doing" => "in_progress",
        "done" or "complete" or "completed" => "done",
        _ => "pending",
    };

    /// <summary>A compact checklist; the leading box mirrors the status.</summary>
    public static string Render(IReadOnlyList<Step> steps)
    {
        if (steps.Count == 0) return "(plan cleared)";
        var sb = new StringBuilder("Plan:\n");
        foreach (var s in steps)
        {
            var box = s.Status switch { "done" => "[x]", "in_progress" => "[~]", _ => "[ ]" };
            sb.Append(box).Append(' ').AppendLine(s.Text);
        }
        var done = steps.Count(s => s.Status == "done");
        sb.Append('(').Append(done).Append('/').Append(steps.Count).Append(" done)");
        return sb.ToString();
    }
}
