using System.IO.Pipelines;
using CodeStack.Ratchet.Core;
using CodeStack.Ratchet.Tests.Support;
using CodeStack.Ratchet.Tools.Mcp;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace CodeStack.Ratchet.Tests;

/// <summary>
/// Ratchet as an MCP server (`--mcp-serve`): a real MCP client connects over in-memory
/// pipes to a real McpServe host — the full JSON-RPC round trip, no fakes in the
/// protocol path. Covers the ITool → MCP adaptation (names/schemas on list, execution
/// on call), the failure policy (tool faults become error RESULTS, not protocol
/// faults), and the progress seam (an IProgressTool's milestones reach the client).
/// </summary>
public sealed class McpServeTests : IAsyncDisposable
{
    private readonly Pipe _clientToServer = new();
    private readonly Pipe _serverToClient = new();
    private readonly CancellationTokenSource _serverCts = new();
    private Task? _serverTask;

    private async Task<McpClient> StartAsync(params ITool[] tools)
    {
        _serverTask = McpServe.RunAsync(
            tools, "ratchet-test", "0.0", "test instance",
            new ModelContextProtocol.Server.StreamServerTransport(
                _clientToServer.Reader.AsStream(), _serverToClient.Writer.AsStream(), "test"),
            _serverCts.Token);

        var transport = new StreamClientTransport(
            serverInput: _clientToServer.Writer.AsStream(),
            serverOutput: _serverToClient.Reader.AsStream());
        return await McpClient.CreateAsync(transport, cancellationToken: CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        _serverCts.Cancel();
        if (_serverTask is not null)
        {
            try { await _serverTask; } catch { /* cancellation surfacing is fine */ }
        }
        _serverCts.Dispose();
    }

    [Fact]
    public async Task ListTools_ExposesNameDescriptionAndSchema()
    {
        var tool = new RecordingTool("ratchet_task");
        await using var client = await StartAsync(tool);

        var tools = await client.ListToolsAsync(cancellationToken: CancellationToken.None);

        var listed = Assert.Single(tools);
        Assert.Equal("ratchet_task", listed.Name);
        Assert.Equal("test tool", listed.Description);
        Assert.Equal("object", listed.JsonSchema.GetProperty("type").GetString());
    }

    [Fact]
    public async Task CallTool_RoundTripsArgumentsAndResult()
    {
        string? seenInput = null;
        var tool = new RecordingTool("echo", input => { seenInput = input; return "done: 42"; });
        await using var client = await StartAsync(tool);

        var result = await client.CallToolAsync(
            "echo",
            new Dictionary<string, object?> { ["prompt"] = "implement the plan" },
            cancellationToken: CancellationToken.None);

        Assert.NotEqual(true, result.IsError);
        Assert.Contains("done: 42", string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text)));
        Assert.NotNull(seenInput);
        Assert.Contains("implement the plan", seenInput);
    }

    [Fact]
    public async Task FaultingTool_BecomesErrorResult_NotProtocolFault()
    {
        var tool = new RecordingTool("boom", _ => throw new InvalidOperationException("kaboom"));
        await using var client = await StartAsync(tool);

        var result = await client.CallToolAsync("boom",
            cancellationToken: CancellationToken.None);

        Assert.Equal(true, result.IsError);
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        Assert.Contains("boom", text);
        Assert.Contains("kaboom", text);

        // The session survived the fault: the next call still works.
        var again = await client.CallToolAsync("boom",
            cancellationToken: CancellationToken.None);
        Assert.Equal(true, again.IsError);
    }

    [Fact]
    public async Task ProgressTool_MilestonesReachTheClient()
    {
        var tool = new MilestoneTool();
        await using var client = await StartAsync(tool);

        var notes = new List<string>();
        var result = await client.CallToolAsync(
            "long_job",
            arguments: null,
            progress: new SyncProgress(v => { lock (notes) notes.Add(v.Message ?? ""); }),
            cancellationToken: CancellationToken.None);

        Assert.NotEqual(true, result.IsError);
        Assert.Contains("finished", string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text)));
        // Progress is async notifications; the milestones were sent before the result,
        // but allow a moment for delivery ordering.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (notes) if (notes.Any(n => n.Contains("phase implement"))) break;
            await Task.Delay(50, CancellationToken.None);
        }
        lock (notes) Assert.Contains(notes, n => n.Contains("phase implement"));
    }

    [Fact]
    public async Task MultipleTools_AllListed_CorrectOneInvoked()
    {
        var a = new RecordingTool("ratchet_implement", _ => "A ran");
        var b = new RecordingTool("ratchet_run", _ => "B ran");
        await using var client = await StartAsync(a, b);

        var tools = await client.ListToolsAsync(cancellationToken: CancellationToken.None);
        Assert.Equal(new[] { "ratchet_implement", "ratchet_run" }, tools.Select(t => t.Name).OrderBy(n => n));

        var result = await client.CallToolAsync("ratchet_run",
            cancellationToken: CancellationToken.None);
        Assert.Contains("B ran", string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text)));
        Assert.Empty(a.Inputs);
        Assert.Single(b.Inputs);
    }

    /// <summary>An IProgressTool that reports two milestones, then finishes.</summary>
    private sealed class MilestoneTool : IProgressTool
    {
        public string Name => "long_job";
        public string Description => "reports milestones";
        public string InputSchemaJson => """{"type":"object","properties":{}}""";

        public Task<string> ExecuteAsync(string inputJson, CancellationToken ct) =>
            ExecuteAsync(inputJson, _ => { }, ct);

        public async Task<string> ExecuteAsync(string inputJson, Action<string> progress, CancellationToken ct)
        {
            progress("classified: feature");
            progress("phase implement (driver local)");
            await Task.Delay(50, ct);   // give the notifications a beat to flush
            return "finished";
        }
    }

    /// <summary>IProgress that invokes synchronously (Progress&lt;T&gt; posts to a sync context tests don't pump).</summary>
    private sealed class SyncProgress : IProgress<ProgressNotificationValue>
    {
        private readonly Action<ProgressNotificationValue> _onReport;
        public SyncProgress(Action<ProgressNotificationValue> onReport) => _onReport = onReport;
        public void Report(ProgressNotificationValue value) => _onReport(value);
    }
}
