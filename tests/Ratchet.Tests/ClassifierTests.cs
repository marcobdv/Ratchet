using CodeStack.Ratchet.Tests.Support;
using CodeStack.Ratchet.Workflow;
using Xunit;

namespace CodeStack.Ratchet.Tests;

/// <summary>
/// The intake classifier's graceful-degradation contract: a parseable answer is used,
/// anything unparseable or ambiguous sizes UP to the most thorough work_type.
/// </summary>
public sealed class ClassifierTests
{
    // trivial runs 2 phases, bugfix 3, feature 4 -> "feature" is most thorough.
    private static readonly WorkflowConfig Config = WorkflowLoader.Parse("""
        version: 1
        name: test
        models:
          cheap: { provider: local, model: test-model }
        defaults:
          driver: cheap
        spine:
          - { id: research, skippable: true,  role: r }
          - { id: implement, skippable: true, role: i }
          - { id: verify,   skippable: false, role: v }
          - { id: review,   skippable: false, role: w }
        work_types:
          trivial: { phases: [verify, review] }
          bugfix:  { phases: [implement, verify, review] }
          feature: { phases: [research, implement, verify, review] }
        """);

    private static Task<(string workType, string reasoning)> Classify(string modelOutput) =>
        new Classifier(new ScriptedLlmClient().Enqueue(ScriptedLlmClient.Text(modelOutput)))
            .ClassifyAsync("some task", Config, CancellationToken.None);

    [Fact]
    public async Task CleanJson_IsUsed()
    {
        var (wt, reasoning) = await Classify("""{"work_type":"bugfix","reasoning":"a bug"}""");
        Assert.Equal("bugfix", wt);
        Assert.Equal("a bug", reasoning);
    }

    [Fact]
    public async Task JsonWrappedInProse_IsExtracted()
    {
        var (wt, _) = await Classify(
            "Sure! Here is the classification:\n```json\n{\"work_type\":\"trivial\",\"reasoning\":\"tiny\"}\n```\nHope that helps.");
        Assert.Equal("trivial", wt);
    }

    [Fact]
    public async Task UnknownWorkTypeInJson_FallsBackToMostThorough()
    {
        var (wt, reasoning) = await Classify("""{"work_type":"epic","reasoning":"?"}""");
        Assert.Equal("feature", wt);
        Assert.Contains("fallback", reasoning);
    }

    [Fact]
    public async Task ProseNamingExactlyOneWorkType_IsAccepted()
    {
        var (wt, _) = await Classify("This looks like a bugfix to me.");
        Assert.Equal("bugfix", wt);
    }

    [Fact]
    public async Task ProseNamingSeveralWorkTypes_IsAmbiguous_SizesUp()
    {
        var (wt, _) = await Classify("Could be a bugfix or maybe a feature.");
        Assert.Equal("feature", wt);
    }

    [Fact]
    public async Task GarbageOutput_SizesUpToMostThorough()
    {
        var (wt, reasoning) = await Classify("I cannot classify this task.");
        Assert.Equal("feature", wt);
        Assert.Contains("fallback", reasoning);
    }

    [Fact]
    public async Task ClassifierCallThrowing_DegradesToMostThorough_NotAnException()
    {
        var llm = new ScriptedLlmClient().EnqueueThrow(new InvalidOperationException("api down"));
        var (wt, reasoning) = await new Classifier(llm)
            .ClassifyAsync("some task", Config, CancellationToken.None);

        Assert.Equal("feature", wt);
        Assert.Contains("api down", reasoning);
    }

    [Fact]
    public async Task WordBoundaryMatching_DoesNotMatchInsideAnotherWord()
    {
        // "fix" as a work_type must not match inside the word "bugfixes"; with no
        // unambiguous mention the classifier sizes up.
        var config = WorkflowLoader.Parse("""
            version: 1
            name: test
            models:
              cheap: { provider: local, model: m }
            defaults: { driver: cheap }
            spine:
              - { id: plan,   skippable: true,  role: p }
              - { id: verify, skippable: false, role: v }
              - { id: review, skippable: false, role: w }
            work_types:
              fix:  { phases: [verify, review] }
              wide: { phases: [plan, verify, review] }
            """);
        var llm = new ScriptedLlmClient().Enqueue(ScriptedLlmClient.Text("This repo has many bugfixes."));
        var (wt, _) = await new Classifier(llm).ClassifyAsync("task", config, CancellationToken.None);

        Assert.NotEqual("fix", wt);
    }
}
