using CodeStack.Ratchet.Core;
using CodeStack.Ratchet.Tests.Support;
using CodeStack.Ratchet.Workflow;
using Xunit;

namespace CodeStack.Ratchet.Tests;

/// <summary>
/// The feedback circuits: a red gate's reason must reach the retry (the loop-back is
/// otherwise blind), and a judge must see the ground truth, not only the driver's
/// self-authored summary.
/// </summary>
public sealed class WorkflowSchedulerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ratchet-wf-" + Guid.NewGuid().ToString("N"));

    public WorkflowSchedulerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task JudgeGateFailureReason_ReachesTheRetryPrompt()
    {
        var config = WorkflowLoader.Parse("""
            version: 1
            name: t
            models:
              cheap: { provider: local, model: c }
              judge: { provider: local, model: j }
            defaults: { driver: cheap }
            spine:
              - id: review
                skippable: false
                role: judge it
                gate: { type: judge, agent: code-review, fresh_context: true, model: judge, on_fail: loop, max_loops: 2 }
            work_types:
              t: { phases: [review], promote: false }
            """);

        // Driver tier serves: classify, phase attempt 1, handover 1, phase attempt 2, handover 2.
        var driver = new ScriptedLlmClient()
            .Enqueue(ScriptedLlmClient.Text("""{"work_type":"t","reasoning":"only option"}"""))
            .Enqueue(ScriptedLlmClient.Text("first attempt"))
            .Enqueue(ScriptedLlmClient.Text("handover one"))
            .Enqueue(ScriptedLlmClient.Text("second attempt"))
            .Enqueue(ScriptedLlmClient.Text("handover two"));
        var judge = new ScriptedLlmClient()
            .Enqueue(ScriptedLlmClient.Text("Missing frobnicate coverage.\nVERDICT: fail"))
            .Enqueue(ScriptedLlmClient.Text("VERDICT: pass"));

        var scheduler = new WorkflowScheduler(
            config,
            clientFactory: tier => tier.Name == "judge" ? judge : driver,
            baseTools: _ => null,
            store: new FileSessionStore(_dir),
            shell: ShellSpec.FromName(null),
            runId: "wf-test-1");

        var run = await scheduler.RunAsync("do the thing", CancellationToken.None);

        Assert.Equal(RunStatus.Completed, run.Status);

        // The second phase attempt's system prompt must carry the judge's rejection.
        // Driver system prompts: [classifier, phase1, handover1, phase2, handover2].
        var phasePrompts = driver.SystemPrompts.Where(p => p.Contains("`review` phase")).ToList();
        Assert.Equal(2, phasePrompts.Count);
        Assert.DoesNotContain("frobnicate", phasePrompts[0]);   // first attempt: no feedback yet
        Assert.Contains("frobnicate", phasePrompts[1]);         // retry sees WHY it was rejected
        Assert.Contains("rejected", phasePrompts[1]);
    }

    [Fact]
    public async Task Judge_SeesTheDiff_WhenOneIsProvided()
    {
        var judge = new ScriptedLlmClient().Enqueue(ScriptedLlmClient.Text("VERDICT: pass"));

        await Gates.RunJudgeAsync(
            judge, "code-review", freshContext: true,
            task: "the task", workingSet: "driver's own summary", transcript: null,
            CancellationToken.None,
            diff: "M src/Foo.cs\n+++ the actual change +++");

        var userMessage = ((TextBlock)judge.CallTranscripts[0][0].Content[0]).Text;
        Assert.Contains("the actual change", userMessage);       // ground truth is in front of the judge
        Assert.Contains("driver's own summary", userMessage);
    }

    [Fact]
    public async Task ResumedRunWithARenamedPhase_FailsGracefully_NotWithAnNre()
    {
        var config = WorkflowLoader.Parse("""
            version: 1
            name: t
            models:
              cheap: { provider: local, model: c }
            defaults: { driver: cheap }
            spine:
              - { id: verify, skippable: false, role: v }
            work_types:
              t: { phases: [verify] }
            """);

        var scheduler = new WorkflowScheduler(
            config,
            clientFactory: _ => new ScriptedLlmClient(),
            baseTools: _ => null,
            store: new FileSessionStore(_dir),
            shell: ShellSpec.FromName(null),
            runId: "wf-test-2");

        // A snapshot whose plan references a phase this (edited) config no longer defines.
        var stale = new RunSnapshot
        {
            RunId = "wf-test-2", Task = "t", WorkType = "t",
            Plan = new List<string> { "implement", "verify" }, Idx = 0,
        };

        var run = await scheduler.RunAsync("t", CancellationToken.None, stale);

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("implement", run.FailReason);
    }
}
