namespace CodeStack.Ratchet.Core;

/// <summary>
/// A tool the agent can call. This is the extension seam: pi has four of these,
/// and so do we (read, write, edit, bash). Ratchet grows by adding implementations
/// — a Roslyn navigation tool, an MCP-backed tool — without touching the loop.
/// </summary>
public interface ITool
{
    /// <summary>Name the model calls, e.g. "bash". Must be stable.</summary>
    string Name { get; }

    /// <summary>One-line description the model reads to decide when to use it.</summary>
    string Description { get; }

    /// <summary>
    /// JSON Schema (as a JSON string) describing the tool's input object.
    /// Sent to the model verbatim in the tool spec.
    /// </summary>
    string InputSchemaJson { get; }

    /// <summary>
    /// Run the tool. <paramref name="inputJson"/> is the raw JSON the model
    /// produced for this call. Return a human/model-readable result string.
    /// </summary>
    Task<string> ExecuteAsync(string inputJson, CancellationToken ct);
}

/// <summary>Name-to-tool lookup, built once at startup and handed to the loop.</summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools;

    public ToolRegistry(IEnumerable<ITool> tools) =>
        _tools = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);

    public IReadOnlyCollection<ITool> All => _tools.Values;

    public bool TryGet(string name, out ITool tool) => _tools.TryGetValue(name, out tool!);
}
