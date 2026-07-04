using CodeStack.Ratchet.Core;
using CodeStack.Ratchet.Tests.Support;
using Xunit;

namespace CodeStack.Ratchet.Tests;

/// <summary>
/// Tier 2: the parallel team coordinator — fan-out to members, optional lead synthesis,
/// plus the delegation safety rails (depth + iteration budget).
/// </summary>
public sealed class TeamToolTests
{
    /// <summary>A member tool that echoes a fixed answer and records that it ran.</summary>
    private sealed class Member : ITool
    {
        private readonly string _answer;
        private readonly int _delayMs;
        public int Ran;
        public Member(string name, string answer, int delayMs = 0) { Name = name; _answer = answer; _delayMs = delayMs; }
        public string Name { get; }
        public string Description => "member";
        public string InputSchemaJson => """{"type":"object","properties":{"task":{"type":"string"}},"required":["task"]}""";
        public async Task<string> ExecuteAsync(string inputJson, CancellationToken ct)
        {
            Interlocked.Increment(ref Ran);
            if (_delayMs > 0) await Task.Delay(_delayMs, ct);
            return _answer + " (for: " + Json.GetString(inputJson, "task") + ")";
        }
    }

    private static Task<string> Run(TeamTool team, string task) =>
        team.ExecuteAsync(System.Text.Json.JsonSerializer.Serialize(new { task }), CancellationToken.None);

    [Fact]
    public async Task NoLead_ConcatenatesLabelledMemberOutputs()
    {
        var a = new Member("architect", "structure matters");
        var b = new Member("skeptic", "keep it simple");
        var team = new TeamTool("board", "desc", new ITool[] { a, b });

        var result = await Run(team, "how to design X");

        Assert.Equal(1, a.Ran);
        Assert.Equal(1, b.Ran);
        Assert.Contains("## architect", result);
        Assert.Contains("structure matters", result);
        Assert.Contains("## skeptic", result);
        Assert.Contains("how to design X", result);   // each member saw the task
    }

    [Fact]
    public async Task WithLead_RunsOneSynthesisPassOverTheMembers()
    {
        var a = new Member("a", "point A");
        var b = new Member("b", "point B");
        // The lead's transcript must contain both members' outputs.
        var lead = new ScriptedLlmClient().Enqueue(ScriptedLlmClient.Text("Merged: A and B agree."));
        var team = new TeamTool("board", "desc", new ITool[] { a, b }, lead, synthesisPrompt: "You are the lead.");

        var result = await Run(team, "decide");

        Assert.Equal("Merged: A and B agree.", result);
        var leadPrompt = ((TextBlock)lead.CallTranscripts[0][0].Content[0]).Text;
        Assert.Contains("point A", leadPrompt);
        Assert.Contains("point B", leadPrompt);
        Assert.Equal("You are the lead.", lead.SystemPrompts[0]);
    }

    [Fact]
    public async Task MembersRunConcurrently_NotSequentially()
    {
        // Deterministic concurrency proof — no wall clock. Every member blocks until ALL
        // of them have started: parallel dispatch releases the rendezvous, sequential
        // dispatch leaves the first member waiting alone and trips the timeout. (The old
        // form asserted 3×100ms delays finish <250ms, which flaked on loaded CI runners.)
        const int n = 3;
        var arrived = 0;
        var everyoneArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var members = Enumerable.Range(0, n).Select(i => (ITool)new RendezvousMember($"m{i}", () =>
        {
            if (Interlocked.Increment(ref arrived) == n) everyoneArrived.TrySetResult();
            return everyoneArrived.Task;
        })).ToList();
        var team = new TeamTool("t", "d", members);

        var run = Run(team, "go");
        var winner = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.True(winner == run,
            $"members never rendezvoused — {Volatile.Read(ref arrived)}/{n} started; dispatch looks sequential");
        await run;
    }

    /// <summary>A member that waits at a caller-supplied rendezvous before answering.</summary>
    private sealed class RendezvousMember : ITool
    {
        private readonly Func<Task> _rendezvous;
        public RendezvousMember(string name, Func<Task> rendezvous) { Name = name; _rendezvous = rendezvous; }
        public string Name { get; }
        public string Description => "member";
        public string InputSchemaJson => """{"type":"object","properties":{"task":{"type":"string"}},"required":["task"]}""";
        public async Task<string> ExecuteAsync(string inputJson, CancellationToken ct)
        {
            await _rendezvous();
            return "ok";
        }
    }

    [Fact]
    public async Task AMemberThatThrows_DoesNotSinkTheTeam()
    {
        var ok = new Member("ok", "fine");
        var boom = new ThrowingMember("boom");
        var team = new TeamTool("t", "d", new ITool[] { ok, boom });

        var result = await Run(team, "go");

        Assert.Contains("fine", result);
        Assert.Contains("member 'boom' failed", result);
    }

    private sealed class ThrowingMember : ITool
    {
        public ThrowingMember(string name) => Name = name;
        public string Name { get; }
        public string Description => "m";
        public string InputSchemaJson => """{"type":"object","properties":{}}""";
        public Task<string> ExecuteAsync(string inputJson, CancellationToken ct) =>
            throw new InvalidOperationException("kaboom");
    }
}

public sealed class DelegationSafetyTests
{
    [Fact]
    public async Task IterationBudget_CutsOffARunawayDelegate()
    {
        // A delegate whose model calls a tool forever must be stopped by its turn budget,
        // not loop indefinitely. Scripted to always request a tool.
        var loopingLlm = new ScriptedLlmClient();
        for (var i = 0; i < 100; i++)
            loopingLlm.Enqueue(ScriptedLlmClient.ToolCall($"t{i}", "noop", "{}"));

        var noop = new RecordingTool("noop", _ => "ok");
        var delegateTool = new DelegateTool("looper", "loops", "sys", loopingLlm, new[] { noop }, maxTurns: 3);

        var result = await delegateTool.ExecuteAsync(
            """{"task":"go"}""", CancellationToken.None);

        // It ran at most its budget of model turns, then returned — not all 100.
        Assert.True(loopingLlm.CallTranscripts.Count <= 4, $"budget not enforced: {loopingLlm.CallTranscripts.Count} calls");
        Assert.NotNull(result);
    }

    [Fact]
    public async Task NestingLimit_RefusesRunawayDelegation()
    {
        // A delegate whose tool is another delegate, recursively, must hit the depth cap.
        var original = Delegation.MaxDepth;
        Delegation.MaxDepth = 2;
        try
        {
            // innermost delegate: given a tool that is yet another delegate call.
            var leafLlm = new ScriptedLlmClient().Enqueue(ScriptedLlmClient.Text("leaf done"));
            var leaf = new DelegateTool("leaf", "leaf", "sys", leafLlm, Array.Empty<ITool>());

            // middle delegate calls leaf.
            var midLlm = new ScriptedLlmClient()
                .Enqueue(ScriptedLlmClient.ToolCall("t1", "leaf", """{"task":"x"}"""))
                .Enqueue(ScriptedLlmClient.Text("mid done"));
            var mid = new DelegateTool("mid", "mid", "sys", midLlm, new ITool[] { leaf });

            // top delegate calls mid.
            var topLlm = new ScriptedLlmClient()
                .Enqueue(ScriptedLlmClient.ToolCall("t1", "mid", """{"task":"y"}"""))
                .Enqueue(ScriptedLlmClient.Text("top done"));
            var top = new DelegateTool("top", "top", "sys", topLlm, new ITool[] { mid });

            await top.ExecuteAsync("""{"task":"start"}""", CancellationToken.None);

            // top enters depth 1, mid enters depth 2 (== limit), so mid's call to leaf is refused:
            // leaf never runs.
            Assert.Empty(leafLlm.CallTranscripts);
        }
        finally { Delegation.MaxDepth = original; }
    }
}
