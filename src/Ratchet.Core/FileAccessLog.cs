namespace CodeStack.Ratchet.Core;

/// <summary>
/// Remembers which files the agent has read (or written) this session, so the
/// <see cref="EditTool"/> can refuse to blind-edit a file the model has never
/// looked at — the "read before write" guard. Reading a file or writing it both
/// count as "known"; editing requires the path to be known first.
///
/// One shared instance is threaded through read/write/edit at the composition
/// root. The sub-agents keep their own (or none) — the guard is a property of a
/// tool set, not a global.
/// </summary>
public sealed class FileAccessLog
{
    private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Record that the agent has seen this path (read it or wrote it).</summary>
    public void MarkKnown(string path) => _known.Add(Normalize(path));

    /// <summary>True if the path was read or written this session.</summary>
    public bool IsKnown(string path) => _known.Contains(Normalize(path));

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }
}
