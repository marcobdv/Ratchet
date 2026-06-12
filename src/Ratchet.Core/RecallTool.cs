using System.Text;
using System.Text.Json;

namespace CodeStack.Ratchet.Core;

/// <summary>
/// The retrieval half of handover. A resumed (cold) session carries the handover
/// summary as its working set; when it needs detail the summary left out, it
/// calls <c>recall</c> to search the prior session's full transcript — which is
/// still sitting in the store. Nothing was destroyed at handover; old context was
/// just demoted to cold storage, and this is how you page it back in.
///
/// Backend-agnostic: it searches the loaded source tree in memory, so it works
/// over the file store or the SQLite store. At scale you'd push this into the DB
/// (SQLite FTS, or the recursive-CTE path walk) instead of loading the whole
/// tree; for now, load-once-and-cache is plenty.
/// </summary>
public sealed class RecallTool : ITool
{
    private readonly ISessionStore _store;
    private readonly string _sourceSessionId;
    private SessionTree? _cached;

    public RecallTool(ISessionStore store, string sourceSessionId)
    {
        _store = store;
        _sourceSessionId = sourceSessionId;
    }

    public string Name => "recall";

    public string Description =>
        "Search the prior (handed-over) session's full transcript for text and return the " +
        "matching messages with their node ids. Use this to retrieve detail the handover " +
        "summary left out — decisions, code, exact wording — instead of guessing.";

    public string InputSchemaJson => """
        {"type":"object","properties":{"query":{"type":"string","description":"Text to search for in the prior session"},"max":{"type":"integer","description":"Maximum matches to return (default 5)"}},"required":["query"]}
        """;

    public Task<string> ExecuteAsync(string inputJson, CancellationToken ct)
    {
        var query = Json.GetString(inputJson, "query");

        var max = 5;
        using (var doc = JsonDocument.Parse(inputJson))
            if (doc.RootElement.TryGetProperty("max", out var m) && m.TryGetInt32(out var v))
                max = Math.Clamp(v, 1, 20);

        var tree = _cached ??= _store.Load(_sourceSessionId);
        if (tree is null)
            return Task.FromResult($"No prior session '{_sourceSessionId}' to recall from.");

        var ordered = tree.Nodes.Values.OrderBy(n => int.TryParse(n.Id, out var v) ? v : 0);

        var hits = new List<string>(max);
        foreach (var node in ordered)
        {
            var text = Flatten(node.Message);
            if (text.Length == 0 || text.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            hits.Add($"[node {node.Id} · {(node.Message.Role == Role.User ? "user" : "asst")}] {Snippet(text, query)}");
            if (hits.Count >= max) break;
        }

        return Task.FromResult(hits.Count == 0
            ? $"No matches for '{query}' in session {_sourceSessionId}."
            : string.Join("\n\n", hits));
    }

    private static string Flatten(Message m)
    {
        var sb = new StringBuilder();
        foreach (var b in m.Content)
        {
            switch (b)
            {
                case TextBlock t: sb.Append(t.Text); break;
                case ToolUseBlock u: sb.Append(u.Name).Append(' ').Append(u.InputJson); break;
                case ToolResultBlock r: sb.Append(r.Content); break;
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string Snippet(string text, string query)
    {
        var i = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        var start = Math.Max(0, i - 60);
        var len = Math.Min(text.Length - start, query.Length + 160);
        var window = text.Substring(start, len).ReplaceLineEndings(" ").Trim();
        return (start > 0 ? "…" : "") + window + (start + len < text.Length ? "…" : "");
    }
}
