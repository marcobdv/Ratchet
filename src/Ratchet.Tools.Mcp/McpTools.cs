using System.Text.Json;
using System.Text.Json.Serialization;
using CodeStack.Ratchet.Core;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace CodeStack.Ratchet.Tools.Mcp;

/// <summary>
/// Connects to Model Context Protocol servers declared in <c>.mcp.json</c> (or <c>mcp.json</c>) and
/// exposes each server tool as a Ratchet <see cref="ITool"/>. Supports local stdio servers
/// (<c>command</c>/<c>args</c>) and remote HTTP servers (<c>url</c>) — the de-facto config shape used
/// by Claude Code / VS Code / Cursor. Connections are held by the returned <see cref="McpConnections"/>,
/// which the caller disposes at shutdown.
/// </summary>
public static class McpToolset
{
    public static async Task<McpConnections> ConnectAsync(string workingDirectory, Action<string> log, CancellationToken ct)
    {
        var connections = new McpConnections();

        var configPath = new[] { ".mcp.json", "mcp.json" }
            .Select(n => Path.Combine(workingDirectory, n))
            .FirstOrDefault(File.Exists);
        if (configPath is null) return connections;

        McpConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<McpConfig>(
                File.ReadAllText(configPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            log($"mcp: failed to parse {Path.GetFileName(configPath)}: {ex.Message}");
            return connections;
        }

        if (config?.McpServers is not { Count: > 0 } servers) return connections;

        foreach (var (name, server) in servers)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(45));

                var client = await McpClient.CreateAsync(CreateTransport(name, server), cancellationToken: timeout.Token)
                    .ConfigureAwait(false);
                connections.Clients.Add(client);

                var tools = await client.ListToolsAsync(cancellationToken: timeout.Token).ConfigureAwait(false);
                foreach (var tool in tools)
                    connections.Tools.Add(new McpToolAdapter(tool));

                log($"mcp: connected '{name}' ({tools.Count} tool(s): {string.Join(", ", tools.Select(t => t.Name).Take(8))})");
            }
            catch (Exception ex)
            {
                log($"mcp: '{name}' failed to connect: {ex.Message}");
            }
        }

        return connections;
    }

    private static IClientTransport CreateTransport(string name, McpServerConfig server)
    {
        if (!string.IsNullOrWhiteSpace(server.Url))
            return new HttpClientTransport(new HttpClientTransportOptions { Name = name, Endpoint = new Uri(server.Url) });

        if (string.IsNullOrWhiteSpace(server.Command))
            throw new InvalidOperationException("server entry must have either 'command' or 'url'.");

        return new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = name,
            Command = server.Command,
            Arguments = server.Args ?? [],
            EnvironmentVariables = server.Env,
        });
    }

    private sealed class McpConfig
    {
        [JsonPropertyName("mcpServers")] public Dictionary<string, McpServerConfig>? McpServers { get; set; }
    }

    private sealed class McpServerConfig
    {
        [JsonPropertyName("command")] public string? Command { get; set; }
        [JsonPropertyName("args")] public string[]? Args { get; set; }
        [JsonPropertyName("env")] public Dictionary<string, string?>? Env { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
    }
}

/// <summary>Holds the live MCP clients and the tools they expose. Dispose to close all connections.</summary>
public sealed class McpConnections : IAsyncDisposable
{
    internal List<McpClient> Clients { get; } = [];
    public List<ITool> Tools { get; } = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var client in Clients)
        {
            try { await client.DisposeAsync().ConfigureAwait(false); } catch { /* best effort */ }
        }
    }
}

/// <summary>
/// Adapts one MCP server tool (an <see cref="McpClientTool"/>, which is an
/// <see cref="AIFunction"/>) to Ratchet's <see cref="ITool"/>: same name/description/schema, and
/// <see cref="ExecuteAsync"/> invokes the tool on its server and stringifies the result.
/// </summary>
internal sealed class McpToolAdapter : ITool
{
    private readonly McpClientTool _tool;

    public McpToolAdapter(McpClientTool tool) => _tool = tool;

    public string Name => _tool.Name;
    public string Description => _tool.Description ?? "";
    public string InputSchemaJson => _tool.JsonSchema.GetRawText();

    public async Task<string> ExecuteAsync(string inputJson, CancellationToken ct)
    {
        var args = string.IsNullOrWhiteSpace(inputJson)
            ? new Dictionary<string, object?>()
            : JsonSerializer.Deserialize<Dictionary<string, object?>>(inputJson) ?? new Dictionary<string, object?>();

        var result = await _tool.InvokeAsync(new AIFunctionArguments(args), ct).ConfigureAwait(false);
        return result switch
        {
            null => "",
            string s => s,
            JsonElement je => je.ValueKind == JsonValueKind.String ? je.GetString() ?? "" : je.GetRawText(),
            _ => JsonSerializer.Serialize(result),
        };
    }
}
