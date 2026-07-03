namespace CodeStack.Ratchet.Core;

/// <summary>
/// A deliberately small YAML-frontmatter reader shared by skills and agent definitions:
/// the leading <c>---</c>…<c>---</c> block, then the body below it. Handles top-level
/// <c>key: value</c> plus block scalars (<c>key: |</c> literal, <c>key: &gt;</c> folded, with
/// the usual <c>+/-</c> chomping indicators) — real Claude skill/agent files routinely write a
/// multi-line folded <c>description: &gt;-</c>. Not a general YAML parser; just what these
/// Markdown-with-frontmatter files need.
/// </summary>
internal static class Frontmatter
{
    /// <summary>Split a file's text into its frontmatter map and the body below the closing fence.
    /// When there is no frontmatter, the map is empty and the body is the whole text.</summary>
    public static (Dictionary<string, string> Meta, string Body) Split(string text)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---")
            return (new(StringComparer.OrdinalIgnoreCase), text);

        var end = -1;
        for (var i = 1; i < lines.Length; i++)
            if (lines[i].Trim() == "---") { end = i; break; }

        var blockEnd = end < 0 ? lines.Length : end;                 // unterminated: whole rest is frontmatter
        var meta = ParseBlock(lines, 1, blockEnd);
        var body = end < 0 ? "" : string.Join('\n', lines[(end + 1)..]).TrimStart('\n');
        return (meta, body);
    }

    /// <summary>Parse just the frontmatter map from a file's lines (no body needed).</summary>
    public static Dictionary<string, string> ParseMeta(string[] lines)
    {
        if (lines.Length == 0 || lines[0].Trim() != "---")
            return new(StringComparer.OrdinalIgnoreCase);
        var end = lines.Length;
        for (var i = 1; i < lines.Length; i++)
            if (lines[i].Trim() == "---") { end = i; break; }
        return ParseBlock(lines, 1, end);
    }

    private static Dictionary<string, string> ParseBlock(string[] lines, int start, int end)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = start;
        while (i < end)
        {
            var raw = lines[i];
            var trimmed = raw.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) { i++; continue; }

            var keyIndent = Indent(raw);
            var sep = trimmed.IndexOf(':');
            if (sep <= 0) { i++; continue; }
            var key = trimmed[..sep].Trim().ToLowerInvariant();
            var rest = trimmed[(sep + 1)..].Trim();

            if (rest.Length > 0 && (rest[0] == '|' || rest[0] == '>'))
            {
                // Block scalar: collect the following more-indented lines.
                var folded = rest[0] == '>';
                i++;
                var body = new List<string>();
                while (i < end)
                {
                    var bodyRaw = lines[i];
                    if (bodyRaw.Trim().Length == 0) { body.Add(""); i++; continue; }
                    if (Indent(bodyRaw) <= keyIndent) break;   // dedent → this key is done
                    body.Add(bodyRaw.Trim());
                    i++;
                }
                while (body.Count > 0 && body[^1].Length == 0) body.RemoveAt(body.Count - 1);
                result[key] = folded
                    ? string.Join(' ', body.Where(l => l.Length > 0))   // folded: newlines → spaces
                    : string.Join('\n', body);                          // literal: newlines kept
            }
            else if (rest.Length == 0 && i + 1 < end && IsListItem(lines[i + 1], keyIndent))
            {
                // Block sequence: `key:` then `  - a` / `  - b`. Joined with ", " so a
                // caller can comma-split it the same as an inline `key: a, b`.
                i++;
                var items = new List<string>();
                while (i < end && IsListItem(lines[i], keyIndent))
                {
                    items.Add(lines[i].Trim()[1..].Trim().Trim('"', '\''));
                    i++;
                }
                result[key] = string.Join(", ", items);
            }
            else
            {
                // Inline flow sequence `[a, b]` collapses to `a, b`; plain scalar keeps its value.
                var v = rest.Trim('"', '\'');
                if (v.StartsWith('[') && v.EndsWith(']')) v = v[1..^1].Trim();
                result[key] = v;
                i++;
            }
        }
        return result;
    }

    private static bool IsListItem(string line, int keyIndent)
    {
        var t = line.TrimStart();
        return t.StartsWith("- ") && Indent(line) > keyIndent;
    }

    private static int Indent(string s)
    {
        var n = 0;
        while (n < s.Length && s[n] == ' ') n++;
        return n;
    }
}
