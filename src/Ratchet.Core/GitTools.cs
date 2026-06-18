using System.Diagnostics;
using System.Text;

namespace CodeStack.Ratchet.Core;

/// <summary>
/// Read-only git awareness: <c>git_status</c> and <c>git_diff</c>. The session tree
/// borrows git's HEAD-over-a-DAG model, but the agent never actually saw the real
/// repository's state; these close that loop so it can check "what have I changed?"
/// without burning a freeform <c>bash</c> call and parsing porcelain by hand.
///
/// Deliberately read-only — staging and committing mutate history and belong behind
/// the permission gate (the curriculum's next rung), not in a YOLO tool. Each tool
/// invokes <c>git</c> directly (no shell) so paths and args need no quoting.
/// </summary>
public static class GitTools
{
    public static IEnumerable<ITool> Build(string workingDirectory)
    {
        yield return new GitStatusTool(workingDirectory);
        yield return new GitDiffTool(workingDirectory);
    }

    internal static async Task<string> RunGitAsync(string workingDirectory, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        var output = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };

        try { proc.Start(); }
        catch (Exception ex) { return $"could not run git: {ex.Message} (is git installed and on PATH?)"; }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        await proc.WaitForExitAsync(ct);

        var text = output.ToString().TrimEnd();
        return text.Length == 0 ? "(no output)" : text;
    }
}

/// <summary>Porcelain status of the working tree plus the current branch.</summary>
public sealed class GitStatusTool : ITool
{
    private readonly string _cwd;
    public GitStatusTool(string cwd) => _cwd = cwd;

    public string Name => "git_status";
    public string Description => "Show the git working-tree status (current branch and changed/untracked files).";
    public string InputSchemaJson => """{"type":"object","properties":{},"required":[]}""";

    public Task<string> ExecuteAsync(string inputJson, CancellationToken ct) =>
        GitTools.RunGitAsync(_cwd, new[] { "status", "--short", "--branch" }, ct);
}

/// <summary>Unified diff of unstaged (or staged) changes, optionally scoped to a path.</summary>
public sealed class GitDiffTool : ITool
{
    private readonly string _cwd;
    public GitDiffTool(string cwd) => _cwd = cwd;

    public string Name => "git_diff";
    public string Description =>
        "Show a unified diff of the working tree. By default shows unstaged changes; set staged=true for the " +
        "index. Optionally scope to a path. Read-only — it never modifies the repo.";
    public string InputSchemaJson => """
        {"type":"object","properties":{"path":{"type":"string","description":"Optional file or directory to scope the diff to."},"staged":{"type":"boolean","description":"Diff the staged (index) changes instead of the working tree."}},"required":[]}
        """;

    public Task<string> ExecuteAsync(string inputJson, CancellationToken ct)
    {
        var path = Json.GetStringOrNull(inputJson, "path");
        var staged = false;
        using (var doc = System.Text.Json.JsonDocument.Parse(inputJson))
            if (doc.RootElement.TryGetProperty("staged", out var s) &&
                (s.ValueKind == System.Text.Json.JsonValueKind.True || s.ValueKind == System.Text.Json.JsonValueKind.False))
                staged = s.GetBoolean();

        var args = new List<string> { "diff" };
        if (staged) args.Add("--staged");
        if (!string.IsNullOrWhiteSpace(path)) { args.Add("--"); args.Add(path!); }
        return GitTools.RunGitAsync(_cwd, args, ct);
    }
}
