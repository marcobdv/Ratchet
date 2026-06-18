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
    /// <summary>Read-only git tools (status + diff). Safe to expose ungated.</summary>
    public static IEnumerable<ITool> Build(string workingDirectory)
    {
        yield return new GitStatusTool(workingDirectory);
        yield return new GitDiffTool(workingDirectory);
    }

    /// <summary>
    /// Mutating git tools (branch + commit) — the "land" capability. These change
    /// history, so they belong behind the <see cref="IToolGate"/>; they're kept out of
    /// <see cref="Build"/> so a caller adds them deliberately, never by accident.
    /// </summary>
    public static IEnumerable<ITool> BuildWrite(string workingDirectory)
    {
        yield return new GitCreateBranchTool(workingDirectory);
        yield return new GitCommitTool(workingDirectory);
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

/// <summary>Create and switch to a new branch. Mutating — governs via the permission gate.</summary>
public sealed class GitCreateBranchTool : ITool
{
    private readonly string _cwd;
    public GitCreateBranchTool(string cwd) => _cwd = cwd;

    public string Name => "git_create_branch";
    public string Description => "Create a new git branch and switch to it.";
    public string InputSchemaJson => """
        {"type":"object","properties":{"name":{"type":"string","description":"New branch name."}},"required":["name"]}
        """;

    public Task<string> ExecuteAsync(string inputJson, CancellationToken ct)
    {
        var name = Json.GetString(inputJson, "name");
        return GitTools.RunGitAsync(_cwd, new[] { "checkout", "-b", name }, ct);
    }
}

/// <summary>
/// Stage and commit the working tree — the terminal "land" action. Mutating, so it
/// only runs when the permission gate allows it. Stages everything by default; set
/// add_all=false to commit only what's already staged.
/// </summary>
public sealed class GitCommitTool : ITool
{
    private readonly string _cwd;
    public GitCommitTool(string cwd) => _cwd = cwd;

    public string Name => "git_commit";
    public string Description =>
        "Commit the working tree with a message. By default stages all changes first (add_all). " +
        "Changes history — only runs if the permission gate allows it.";
    public string InputSchemaJson => """
        {"type":"object","properties":{"message":{"type":"string","description":"Commit message."},"add_all":{"type":"boolean","description":"Stage all changes before committing (default true)."}},"required":["message"]}
        """;

    public async Task<string> ExecuteAsync(string inputJson, CancellationToken ct)
    {
        var message = Json.GetString(inputJson, "message");
        var addAll = true;
        using (var doc = System.Text.Json.JsonDocument.Parse(inputJson))
            if (doc.RootElement.TryGetProperty("add_all", out var a) &&
                (a.ValueKind == System.Text.Json.JsonValueKind.True || a.ValueKind == System.Text.Json.JsonValueKind.False))
                addAll = a.GetBoolean();

        if (addAll)
        {
            var add = await GitTools.RunGitAsync(_cwd, new[] { "add", "-A" }, ct);
            if (add.StartsWith("could not run git", StringComparison.Ordinal)) return add;
        }
        return await GitTools.RunGitAsync(_cwd, new[] { "commit", "-m", message }, ct);
    }
}
