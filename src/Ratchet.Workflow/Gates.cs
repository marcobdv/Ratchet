using System.Diagnostics;
using System.Text;
using CodeStack.Ratchet.Core;

namespace CodeStack.Ratchet.Workflow;

/// <summary>The result of evaluating a gate: did it pass, and why.</summary>
public sealed record GateOutcome(bool Pass, string Reason);

/// <summary>
/// Gate evaluation. Two kinds, per the design:
///   - <b>command</b>: run a process, route on exit code. The cheapest, strongest
///     judge — <c>verify</c> uses this and needs no model at all.
///   - <b>judge</b>: one frontier completion for judgment an exit code can't express,
///     with the fresh-eyes property when <c>fresh_context: true</c> (it sees only the
///     authored working-set, not the driver's accumulated premises — which is exactly
///     how it catches a framing error the shared-context advisor structurally cannot).
/// </summary>
public static class Gates
{
    public static async Task<GateOutcome> RunCommandAsync(string command, ShellSpec shell, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = shell.FileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(shell.CommandFlag);
        psi.ArgumentList.Add(command);

        using var proc = new Process { StartInfo = psi };
        var output = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };

        try { proc.Start(); }
        catch (Exception ex) { return new GateOutcome(false, $"could not run gate command: {ex.Message}"); }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        await proc.WaitForExitAsync(ct);

        var tail = Tail(output.ToString(), 600);
        return new GateOutcome(proc.ExitCode == 0, $"`{command}` exit {proc.ExitCode}{(tail.Length > 0 ? "\n" + tail : "")}");
    }

    /// <summary>
    /// A judge completion. With <paramref name="freshContext"/> the judge sees only the
    /// task and the authored working-set doc — fresh eyes. Otherwise it also sees the
    /// driver's transcript. It must end with a VERDICT line; an unparseable verdict is
    /// treated as a fail (the gate's job is to be hard to pass, not easy).
    /// </summary>
    public static async Task<GateOutcome> RunJudgeAsync(
        ILlmClient judge, string judgeAgent, bool freshContext,
        string task, string workingSet, string? transcript, CancellationToken ct)
    {
        var system = JudgePrompts.For(judgeAgent);
        var user = new StringBuilder();
        user.Append("Task under review:\n").Append(task).Append("\n\n");
        user.Append("Authored working-set / artifact to judge:\n").Append(workingSet).Append("\n\n");
        if (!freshContext && !string.IsNullOrWhiteSpace(transcript))
            user.Append("Full work transcript (for reference):\n").Append(transcript).Append("\n\n");
        user.Append("Decide. End your reply with exactly one line: 'VERDICT: pass' or 'VERDICT: fail', " +
                    "and if fail, give one concrete reason just above it.");

        var convo = new Conversation();
        convo.Add(Message.UserText(user.ToString()));

        string text;
        try
        {
            var resp = await judge.CompleteAsync(system, convo, Array.Empty<ITool>(), _ => { }, ct);
            text = string.Concat(resp.AssistantMessage.Content.OfType<TextBlock>().Select(t => t.Text)).Trim();
        }
        catch (Exception ex)
        {
            return new GateOutcome(false, $"judge call failed: {ex.Message}");
        }

        var pass = ParseVerdict(text);
        return new GateOutcome(pass, Summarize(text));
    }

    private static bool ParseVerdict(string text)
    {
        // Last VERDICT line wins; default fail if none.
        bool? verdict = null;
        foreach (var line in text.Split('\n'))
        {
            var t = line.Trim();
            var i = t.IndexOf("VERDICT:", StringComparison.OrdinalIgnoreCase);
            if (i < 0) continue;
            var rest = t[(i + "VERDICT:".Length)..].Trim().ToLowerInvariant();
            if (rest.StartsWith("pass")) verdict = true;
            else if (rest.StartsWith("fail")) verdict = false;
        }
        return verdict ?? false;
    }

    private static string Summarize(string text)
    {
        var t = text.ReplaceLineEndings(" ").Trim();
        return t.Length > 300 ? t[..300] + "…" : t;
    }

    private static string Tail(string s, int chars)
    {
        s = s.TrimEnd();
        return s.Length <= chars ? s : "…" + s[^chars..];
    }
}

/// <summary>
/// The (editable) judge role prompts, keyed by the gate's <c>agent</c> name. These are
/// the *soft* half of "structure the control flow, not the thinking": the spine
/// guarantees you reach review; this prompt shapes how you review.
/// </summary>
internal static class JudgePrompts
{
    public static string For(string agent) => agent.ToLowerInvariant() switch
    {
        "spec-review" =>
            "You are reviewing a change PLAN / spec before any code is written. Pass only if it is " +
            "unambiguous, scoped, and implementable: the change is clearly stated, the affected areas are " +
            "named, edge cases and the test approach are covered, and nothing critical is hand-waved. " +
            "Fail if it is vague, missing the test strategy, or larger than it claims.",
        "code-review" =>
            "You are reviewing a completed change for merge-readiness. Pass only if it matches the stated " +
            "goal, follows the codebase's conventions, has no obvious correctness or regression risk, and " +
            "its tests actually exercise the change. Fail on convention violations, missing tests, or scope creep.",
        _ =>
            "You are a strict reviewer. Pass only if the work fully and correctly satisfies its stated goal " +
            "with no obvious defects; otherwise fail with a concrete reason.",
    };
}
