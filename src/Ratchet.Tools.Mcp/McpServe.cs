using System.Text.Json;
using CodeStack.Ratchet.Core;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CodeStack.Ratchet.Tools.Mcp;

/// <summary>
/// An <see cref="ITool"/> that can report human-readable progress while it runs. The MCP
/// server adapter uses this to forward milestones (phase starts, gate results, tool calls)
/// as MCP progress notifications, which is what keeps a long-running call alive past the
/// client's tool timeout. Plain <see cref="ITool"/>s still get a periodic heartbeat.
/// </summary>
public interface IProgressTool : ITool
{
    Task<string> ExecuteAsync(string inputJson, Action<string> progress, CancellationToken ct);
}

/// <summary>
/// Wraps an <see cref="ITool"/> under a different name/description for serving — an
/// in-process tool built for Ratchet's own model (e.g. `council`) usually wants an
/// exported name (`ratchet_council`) and a description written for the REMOTE caller,
/// which starts cold and needs to be told what context to pass. Execution is untouched.
/// </summary>
public sealed class RelabeledTool : ITool
{
    private readonly ITool _inner;

    public RelabeledTool(string name, string description, ITool inner)
    {
        Name = name;
        Description = description;
        _inner = inner;
    }

    public string Name { get; }
    public string Description { get; }
    public string InputSchemaJson => _inner.InputSchemaJson;

    public Task<string> ExecuteAsync(string inputJson, CancellationToken ct) =>
        _inner.ExecuteAsync(inputJson, ct);
}

/// <summary>
/// Serves Ratchet <see cref="ITool"/>s over the Model Context Protocol — the exact inverse
/// of <see cref="McpToolset"/> (which adapts MCP server tools into <see cref="ITool"/>s).
/// The same seam, pointed outward: any MCP client (Claude Code, VS Code, another Ratchet)
/// can call the tools it is given here. Transport-agnostic so tests can run it over
/// in-memory streams; the CLI passes stdio.
/// </summary>
public static class McpServe
{
    public static async Task RunAsync(
        IEnumerable<ITool> tools,
        string serverName,
        string version,
        string instructions,
        ITransport transport,
        CancellationToken ct)
    {
        var collection = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var tool in tools)
            collection.Add(new RatchetToolAdapter(tool));

        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = serverName, Version = version },
            ServerInstructions = instructions,
            ToolCollection = collection,
        };

        await using var server = McpServer.Create(transport, options);
        await server.RunAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Adapts one Ratchet <see cref="ITool"/> to an MCP server tool: same name/description/
/// schema; a call deserialises the MCP arguments back to the tool's input JSON. While the
/// tool runs, a heartbeat progress notification goes out every 15s (and, for an
/// <see cref="IProgressTool"/>, every milestone) so a client with a per-call timeout —
/// Claude Code resets its clock on progress — survives an implementation that takes an
/// hour. A tool failure maps to an MCP error RESULT, not a protocol fault: the calling
/// model should see and adapt to it, same policy as the agent loop's own tool handling.
/// </summary>
internal sealed class RatchetToolAdapter : McpServerTool
{
    private static readonly TimeSpan HeartbeatEvery = TimeSpan.FromSeconds(15);

    private readonly ITool _tool;
    private readonly Tool _protocolTool;

    public RatchetToolAdapter(ITool tool)
    {
        _tool = tool;
        _protocolTool = new Tool
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = JsonSerializer.Deserialize<JsonElement>(tool.InputSchemaJson),
        };
    }

    public override Tool ProtocolTool => _protocolTool;

    public override IReadOnlyList<object> Metadata => [];

    public override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken ct)
    {
        var inputJson = request.Params?.Arguments is { } args
            ? JsonSerializer.Serialize(args)
            : "{}";

        // Progress can only be sent when the client asked for it (sent a progressToken).
        var token = request.Params?.ProgressToken;
        var progress = 0;
        async Task Notify(string message)
        {
            if (token is not { } t) return;
            try
            {
                await request.Server.NotifyProgressAsync(
                    t,
                    new ProgressNotificationValue { Progress = ++progress, Message = message },
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            catch { /* progress is best-effort; never fail the call over it */ }
        }

        var started = DateTime.UtcNow;
        var lastMilestone = "starting";

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = token is null ? Task.CompletedTask : Task.Run(async () =>
        {
            while (!heartbeatCts.Token.IsCancellationRequested)
            {
                try { await Task.Delay(HeartbeatEvery, heartbeatCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                var elapsed = DateTime.UtcNow - started;
                await Notify($"working — {elapsed:mm\\:ss} elapsed · {lastMilestone}").ConfigureAwait(false);
            }
        });

        try
        {
            var result = _tool is IProgressTool reporting
                ? await reporting.ExecuteAsync(inputJson, m =>
                    {
                        lastMilestone = m;
                        _ = Notify(m);   // fire-and-forget: milestones must not slow the work
                    }, ct).ConfigureAwait(false)
                : await _tool.ExecuteAsync(inputJson, ct).ConfigureAwait(false);

            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = result }],
                IsError = false,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // the client cancelled; let the protocol layer answer
        }
        catch (Exception ex)
        {
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = $"Tool '{_tool.Name}' failed: {ex.Message}" }],
                IsError = true,
            };
        }
        finally
        {
            heartbeatCts.Cancel();
            try { await heartbeat.ConfigureAwait(false); } catch { /* heartbeat is best-effort */ }
        }
    }
}
