using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodeStack.Ratchet.Core;

/// <summary>
/// Read-only code search: regex over file contents and/or filename globbing under the
/// working directory. It executes no process and writes nothing, so — unlike a raw shell —
/// it is read-only <i>by construction</i>. That makes it the right investigative tool for a
/// scoped sub-agent (the <c>explore</c> delegate) which must not be able to mutate anything,
/// no matter how it's prompted.
/// </summary>
public sealed class SearchTool : ITool
{
    private static readonly string[] SkipDirs =
        { "bin", "obj", ".git", ".ratchet", "node_modules", ".vs" };

    private readonly string _root;
    public SearchTool(string? root = null) => _root = Path.GetFullPath(root ?? Directory.GetCurrentDirectory());

    public string Name => "search";
    public string Description =>
        "Search the codebase (read-only). Give a regex `pattern` to find matching lines, and/or a " +
        "`glob` (e.g. *.cs) to filter files; omit `pattern` to just list matching files. Scoped to the " +
        "working directory.";
    public string InputSchemaJson => """
        {"type":"object","properties":{"pattern":{"type":"string","description":"Regex matched against file contents. Omit to only list files."},"glob":{"type":"string","description":"Filename glob filter, e.g. *.cs (default: all files)."},"path":{"type":"string","description":"Subdirectory to search, relative to the working dir (default: whole tree)."},"max":{"type":"integer","description":"Maximum results (default 100)."}},"required":[]}
        """;

    public Task<string> ExecuteAsync(string inputJson, CancellationToken ct)
    {
        var pattern = Json.GetStringOrNull(inputJson, "pattern");
        var glob = Json.GetStringOrNull(inputJson, "glob");
        if (string.IsNullOrWhiteSpace(glob)) glob = "*";
        var sub = Json.GetStringOrNull(inputJson, "path");

        var max = 100;
        using (var doc = JsonDocument.Parse(inputJson))
            if (doc.RootElement.TryGetProperty("max", out var m) && m.TryGetInt32(out var v))
                max = Math.Clamp(v, 1, 1000);

        // Resolve the search root and keep it inside the working dir (read-only + scoped).
        var basePath = string.IsNullOrWhiteSpace(sub) ? _root : Path.GetFullPath(Path.Combine(_root, sub));
        if (!basePath.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult($"path '{sub}' is outside the working directory.");
        if (!Directory.Exists(basePath) && !File.Exists(basePath))
            return Task.FromResult($"No such path '{sub}'.");

        Regex? regex = null;
        if (!string.IsNullOrEmpty(pattern))
        {
            try { regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(2)); }
            catch (ArgumentException ex) { return Task.FromResult($"invalid regex: {ex.Message}"); }
        }

        IEnumerable<string> files;
        try { files = EnumerateFiles(basePath, glob!); }
        catch (Exception ex) { return Task.FromResult($"search failed: {ex.Message}"); }

        var hits = new List<string>(max);
        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) break;
            if (hits.Count >= max) break;

            if (regex is null) { hits.Add(Rel(file)); continue; }   // file-listing mode

            string text;
            try
            {
                var info = new FileInfo(file);
                if (info.Length > 2 * 1024 * 1024) continue;        // skip very large files
                text = File.ReadAllText(file);
            }
            catch { continue; }

            var lineNo = 0;
            foreach (var line in text.Split('\n'))
            {
                lineNo++;
                if (!regex.IsMatch(line)) continue;
                var trimmed = line.TrimEnd('\r').Trim();
                hits.Add($"{Rel(file)}:{lineNo}: {(trimmed.Length > 200 ? trimmed[..200] + "…" : trimmed)}");
                if (hits.Count >= max) break;
            }
        }

        if (hits.Count == 0) return Task.FromResult(regex is null ? "(no files match)" : "(no matches)");
        var capped = hits.Count >= max ? $"\n(capped at {max})" : "";
        return Task.FromResult(string.Join("\n", hits) + capped);
    }

    private static IEnumerable<string> EnumerateFiles(string basePath, string glob)
    {
        if (File.Exists(basePath)) { yield return basePath; yield break; }
        foreach (var file in Directory.EnumerateFiles(basePath, glob, SearchOption.AllDirectories))
        {
            var dir = Path.GetDirectoryName(file) ?? "";
            if (SkipDirs.Any(s => dir.Contains($"{Path.DirectorySeparatorChar}{s}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                                  || dir.EndsWith($"{Path.DirectorySeparatorChar}{s}", StringComparison.OrdinalIgnoreCase)))
                continue;
            yield return file;
        }
    }

    private string Rel(string file) => Path.GetRelativePath(_root, file);
}
