using CodeStack.Ratchet.Core;
using CodeStack.Ratchet.Tests.Support;
using Xunit;

namespace CodeStack.Ratchet.Tests;

/// <summary>
/// Per-turn REPL routing (RATCHET_ROUTE=auto): the classify call must map to a route
/// from the table, and every failure mode must land on the table's DEFAULT route —
/// never an exception, never a silent wrong pick without a "(fallback: …)" reason.
/// </summary>
public sealed class TurnRouterTests
{
    private static readonly RouteTable Table = new(
        new[]
        {
            new RouteSpec("quick", "small self-contained requests", "local", "small-model"),
            new RouteSpec("standard", "ordinary coding work", "anthropic", "claude-sonnet-4-6"),
            new RouteSpec("deep", "hard or wide work", "anthropic", "claude-opus-4-8"),
        },
        defaultRoute: "standard");

    [Fact]
    public async Task JsonAnswer_PicksTheNamedRoute()
    {
        var llm = new ScriptedLlmClient().Enqueue(ScriptedLlmClient.Text(
            """{"route":"deep","reasoning":"multi-file architectural change"}"""));
        var (route, reason) = await new TurnRouter(llm, Table).RouteAsync("redesign the storage layer", CancellationToken.None);

        Assert.Equal("deep", route.Name);
        Assert.Equal("multi-file architectural change", reason);
    }

    [Fact]
    public async Task RouteNameMatching_IsCaseInsensitive()
    {
        var llm = new ScriptedLlmClient().Enqueue(ScriptedLlmClient.Text("""{"route":"QUICK","reasoning":"r"}"""));
        var (route, _) = await new TurnRouter(llm, Table).RouteAsync("rename a variable", CancellationToken.None);
        Assert.Equal("quick", route.Name);
    }

    [Fact]
    public async Task JsonWrappedInProse_StillParses()
    {
        var llm = new ScriptedLlmClient().Enqueue(ScriptedLlmClient.Text(
            """Sure! Here is my pick: {"route":"quick","reasoning":"one-liner"} — hope that helps."""));
        var (route, _) = await new TurnRouter(llm, Table).RouteAsync("fix a typo", CancellationToken.None);
        Assert.Equal("quick", route.Name);
    }

    [Fact]
    public async Task ProseNamingExactlyOneRoute_IsRescued()
    {
        var llm = new ScriptedLlmClient().Enqueue(ScriptedLlmClient.Text("I would send this to the deep model."));
        var (route, reason) = await new TurnRouter(llm, Table).RouteAsync("subtle race condition", CancellationToken.None);

        Assert.Equal("deep", route.Name);
        Assert.Contains("prose", reason);
    }

    [Fact]
    public async Task AmbiguousProse_FallsBackToDefault()
    {
        // Two route names in the answer — no unambiguous pick, so land on the default.
        var llm = new ScriptedLlmClient().Enqueue(ScriptedLlmClient.Text("either quick or deep would work"));
        var (route, reason) = await new TurnRouter(llm, Table).RouteAsync("do something", CancellationToken.None);

        Assert.Equal("standard", route.Name);
        Assert.Contains("fallback", reason);
    }

    [Fact]
    public async Task UnknownRouteName_FallsBackToDefault()
    {
        var llm = new ScriptedLlmClient().Enqueue(ScriptedLlmClient.Text("""{"route":"turbo","reasoning":"?"}"""));
        var (route, reason) = await new TurnRouter(llm, Table).RouteAsync("task", CancellationToken.None);

        Assert.Equal("standard", route.Name);
        Assert.Contains("fallback", reason);
    }

    [Fact]
    public async Task ClassifierThrow_FallsBackToDefault_NotException()
    {
        var llm = new ScriptedLlmClient().EnqueueThrow(new InvalidOperationException("api down"));
        var (route, reason) = await new TurnRouter(llm, Table).RouteAsync("task", CancellationToken.None);

        Assert.Equal("standard", route.Name);
        Assert.Contains("api down", reason);
    }

    [Fact]
    public async Task Cancellation_Propagates_InsteadOfRoutingToDefault()
    {
        var llm = new ScriptedLlmClient().Enqueue(ScriptedLlmClient.Text("""{"route":"quick"}"""));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new TurnRouter(llm, Table).RouteAsync("task", cts.Token));
    }

    [Fact]
    public async Task HugeRequest_IsTruncatedForClassification()
    {
        var llm = new ScriptedLlmClient().Enqueue(ScriptedLlmClient.Text("""{"route":"standard","reasoning":"r"}"""));
        await new TurnRouter(llm, Table).RouteAsync(new string('x', 50_000), CancellationToken.None);

        var prompt = ((TextBlock)llm.CallTranscripts[0][0].Content[0]).Text;
        Assert.True(prompt.Length < 5_000, $"classify prompt should be bounded, was {prompt.Length}");
        Assert.Contains("truncated", prompt);
    }

    [Fact]
    public async Task ClassifyPrompt_ListsEveryRouteWithItsDescription()
    {
        var llm = new ScriptedLlmClient().Enqueue(ScriptedLlmClient.Text("""{"route":"quick","reasoning":"r"}"""));
        await new TurnRouter(llm, Table).RouteAsync("task", CancellationToken.None);

        var prompt = ((TextBlock)llm.CallTranscripts[0][0].Content[0]).Text;
        Assert.Contains("quick: small self-contained requests", prompt);
        Assert.Contains("standard: ordinary coding work", prompt);
        Assert.Contains("deep: hard or wide work", prompt);
    }
}

/// <summary>The route table: JSON loading, the built-in default, and the fail-loud rules.</summary>
public sealed class RouteTableTests
{
    [Fact]
    public void Parse_FullTable_ResolvesRoutesDefaultAndClassifier()
    {
        var table = RouteTable.Parse("""
            {
              "classifier": { "provider": "local", "model": "tiny" },
              "default": "mid",
              "routes": [
                { "name": "cheap", "description": "small stuff", "provider": "local", "model": "small" },
                { "name": "mid", "description": "normal work", "provider": "anthropic", "model": "claude-sonnet-4-6" }
              ]
            }
            """);

        Assert.Equal(2, table.Routes.Count);
        Assert.Equal("mid", table.Default.Name);
        Assert.Equal("local:tiny", table.Classifier.Tier);
    }

    [Fact]
    public void Parse_NoClassifier_UsesTheFirstRoute()
    {
        var table = RouteTable.Parse("""
            { "default": "a", "routes": [
                { "name": "a", "description": "d", "provider": "local", "model": "m1" },
                { "name": "b", "description": "d", "provider": "local", "model": "m2" } ] }
            """);
        Assert.Equal("local:m1", table.Classifier.Tier);
    }

    [Fact]
    public void Parse_NoDefault_UsesTheFirstRoute()
    {
        var table = RouteTable.Parse("""
            { "routes": [ { "name": "only", "description": "d", "provider": "local", "model": "m" } ] }
            """);
        Assert.Equal("only", table.Default.Name);
    }

    [Fact]
    public void Parse_AllowsCommentsAndTrailingCommas()
    {
        // The file is hand-edited config — the strictest JSON dialect would just annoy.
        var table = RouteTable.Parse("""
            {
              // hand-tuned 2026-07
              "routes": [ { "name": "a", "description": "d", "provider": "local", "model": "m", }, ],
            }
            """);
        Assert.Single(table.Routes);
    }

    [Theory]
    [InlineData("""{ "routes": [] }""", "at least one route")]
    [InlineData("""{ "default": "ghost", "routes": [ { "name": "a", "description": "d", "provider": "p", "model": "m" } ] }""", "ghost")]
    [InlineData("""{ "routes": [ { "name": "a", "description": "d", "provider": "", "model": "m" } ] }""", "provider and model")]
    [InlineData("""{ "routes": [ { "name": "a", "description": "d", "provider": "p", "model": "m" }, { "name": "A", "description": "d", "provider": "p", "model": "m" } ] }""", "duplicate")]
    [InlineData("not json at all", "could not parse")]
    public void Parse_BrokenTable_FailsLoudly(string json, string expectedInMessage)
    {
        var ex = Assert.Throws<RouteConfigException>(() => RouteTable.Parse(json));
        Assert.Contains(expectedInMessage, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_NoFile_ReturnsTheBuiltinAnthropicLadder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ratchet-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var table = RouteTable.Load(dir);
            Assert.Equal(new[] { "quick", "standard", "deep" }, table.Routes.Select(r => r.Name));
            Assert.Equal("standard", table.Default.Name);
            Assert.Equal(table.Routes[0].Tier, table.Classifier.Tier);   // cheapest rung classifies
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Load_FilePresent_WinsOverTheBuiltin()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ratchet-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, ".ratchet"));
        try
        {
            File.WriteAllText(Path.Combine(dir, ".ratchet", "routing.json"),
                """{ "routes": [ { "name": "mine", "description": "d", "provider": "local", "model": "m" } ] }""");
            var table = RouteTable.Load(dir);
            Assert.Equal("mine", table.Default.Name);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
