using System.Globalization;
using System.Text;

namespace CodeStack.Ratchet.Core;

/// <summary>
/// A handover document: the authored working-set summary that lets a *fresh*
/// session resume cold, without replaying the whole transcript. This is the
/// deliberate alternative to in-place context compaction — what's left out is a
/// decision, not a silent loss, and the omitted detail stays reachable in the
/// prior session's tree via the <see cref="RecallTool"/>.
/// </summary>
public sealed record Handover(string SourceSessionId, string? SourceHeadId, DateTime CreatedUtc, string Content);

/// <summary>
/// The prompt that turns a transcript into a handover. The sections are the
/// design: they capture the working set (goal, state, decisions, next steps,
/// gotchas) and *point* at the rest rather than reproducing it.
/// </summary>
public static class HandoverPrompt
{
    public const string System =
        "You are writing a HANDOVER so a fresh session — another instance of you, " +
        "with no memory of this conversation — can pick the work up cold. Be concrete: " +
        "prefer file paths, identifiers, exact decisions and constraints over narration. " +
        "Drop pleasantries and play-by-play. What you leave out is lost, so choose " +
        "deliberately — but you do NOT need to reproduce everything: the prior session's " +
        "full transcript stays available through the `recall` tool, so capture the working " +
        "set and point to the rest.";

    public const string Instruction =
        "Write the handover now as Markdown, with exactly these sections:\n\n" +
        "## Goal\nWhat we're trying to achieve and what \"done\" looks like.\n\n" +
        "## Current state\nWhat exists now — built/working vs half-done, verified vs assumed.\n\n" +
        "## Key decisions\nChoices made and the reasoning, especially constraints that must " +
        "not be silently reversed.\n\n" +
        "## Next steps\nThe concrete next actions, in order.\n\n" +
        "## Gotchas\nNon-obvious constraints, dead-ends already ruled out, things easy to forget.\n\n" +
        "## Pointers\nFiles touched, and terms to `recall` from the prior session for detail.\n\n" +
        "Keep it tight — a working set, not a transcript.";
}

/// <summary>
/// Generates a handover by asking the model to summarise the active path. Reuses
/// the <see cref="ILlmClient"/> seam — no new provider plumbing — with the
/// handover prompt and no tools, so it's just one ordinary completion.
/// </summary>
public sealed class HandoverGenerator
{
    private readonly ILlmClient _llm;

    public HandoverGenerator(ILlmClient llm) => _llm = llm;

    /// <param name="activePath">The conversation root..HEAD to summarise.</param>
    /// <param name="onDelta">Optional live stream of the doc as it's written.</param>
    public async Task<string> GenerateAsync(Conversation activePath, Action<string>? onDelta, CancellationToken ct)
    {
        // Transient conversation: the path so far + the "write the handover" ask.
        var convo = new Conversation();
        foreach (var m in activePath.Messages) convo.Add(m);
        convo.Add(Message.UserText(HandoverPrompt.Instruction));

        var response = await _llm.CompleteAsync(
            HandoverPrompt.System, convo, Array.Empty<ITool>(), onDelta ?? (_ => { }), ct);

        return string.Concat(
            response.AssistantMessage.Content.OfType<TextBlock>().Select(t => t.Text)).Trim();
    }
}

/// <summary>
/// Persists handover docs as plain, human-editable Markdown under
/// {baseDir}/.ratchet/handovers/{sourceSessionId}.md. A one-line HTML-comment
/// header carries the metadata (source session, head node, timestamp) so a
/// resume knows which cold store to point `recall` at — while the file still
/// renders cleanly in any Markdown viewer, and you can edit the body before
/// resuming.
/// </summary>
public sealed class FileHandoverStore
{
    private const string HeaderTag = "<!-- ratchet:handover";
    private readonly string _dir;

    public FileHandoverStore(string baseDir)
    {
        _dir = Path.Combine(baseDir, ".ratchet", "handovers");
        Directory.CreateDirectory(_dir);
    }

    /// <summary>Write the handover; returns the file path.</summary>
    public string Save(Handover h)
    {
        var path = Path.Combine(_dir, SessionId.Validate(h.SourceSessionId) + ".md");
        var header = $"{HeaderTag} source={h.SourceSessionId} head={h.SourceHeadId ?? "-"} " +
                     $"created={h.CreatedUtc.ToString("o", CultureInfo.InvariantCulture)} -->";
        File.WriteAllText(path, header + "\n\n" + h.Content + "\n");
        return path;
    }

    /// <summary>Load by source-session id, or null if none. Tolerates a hand-edited body.</summary>
    public Handover? Load(string sourceId)
    {
        if (!SessionId.IsValid(sourceId)) return null;
        var path = Path.Combine(_dir, sourceId + ".md");
        if (!File.Exists(path)) return null;

        var text = File.ReadAllText(path);
        string src = sourceId;
        string? head = null;
        var created = File.GetLastWriteTimeUtc(path);
        var body = text;

        using (var reader = new StringReader(text))
        {
            var first = reader.ReadLine();
            if (first is not null && first.StartsWith(HeaderTag, StringComparison.Ordinal))
            {
                foreach (var tok in first.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (tok.StartsWith("source=", StringComparison.Ordinal)) src = tok["source=".Length..];
                    else if (tok.StartsWith("head=", StringComparison.Ordinal))
                    { var h = tok["head=".Length..]; head = h == "-" ? null : h; }
                    else if (tok.StartsWith("created=", StringComparison.Ordinal) &&
                             DateTime.TryParse(tok["created=".Length..], CultureInfo.InvariantCulture,
                                 DateTimeStyles.RoundtripKind, out var c))
                        created = c;
                }
                body = reader.ReadToEnd().TrimStart('\r', '\n');
            }
        }

        return new Handover(src, head, created, body.Trim());
    }

    /// <summary>Available handover ids, newest id first.</summary>
    public IReadOnlyList<string> List() =>
        Directory.EnumerateFiles(_dir, "*.md")
                 .Select(Path.GetFileNameWithoutExtension)
                 .OfType<string>()
                 .OrderByDescending(x => x, StringComparer.Ordinal)
                 .ToList();
}
