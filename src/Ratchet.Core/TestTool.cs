using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeStack.Ratchet.Core;

/// <summary>
/// Runs the project's test suite and hands back a *structured* summary rather than
/// a wall of console output — pass/fail/skip counts and the names of failing tests,
/// in the same spirit as <c>roslyn_diagnostics</c>. The agent could already shell
/// out to <c>dotnet test</c>; this just parses the result so the model spends tokens
/// on the failures, not the noise.
///
/// The command defaults to <c>dotnet test</c> and is overridable with
/// <c>RATCHET_TEST_CMD</c> (e.g. <c>npm test</c>, <c>pytest -q</c>); parsing is
/// best-effort and falls back to a tail of the output when it recognises nothing.
/// </summary>
public sealed class TestTool : ITool
{
    private readonly ShellSpec _shell;
    private readonly string _baseCommand;

    public TestTool(ShellSpec shell, string? baseCommand = null)
    {
        _shell = shell;
        _baseCommand = string.IsNullOrWhiteSpace(baseCommand) ? "dotnet test" : baseCommand!;
    }

    public string Name => "run_tests";

    public string Description =>
        $"Run the test suite ('{_baseCommand}') and return a parsed summary: pass/fail/skip counts " +
        "and the names of failing tests. Optionally pass 'args' to append (e.g. a --filter or a project path).";

    public string InputSchemaJson => """
        {"type":"object","properties":{"args":{"type":"string","description":"Extra arguments appended to the test command, e.g. \"--filter Category=Unit\"."}},"required":[]}
        """;

    public async Task<string> ExecuteAsync(string inputJson, CancellationToken ct)
    {
        var extra = Json.GetStringOrNull(inputJson, "args");
        var command = string.IsNullOrWhiteSpace(extra) ? _baseCommand : $"{_baseCommand} {extra}";

        var psi = new ProcessStartInfo();
        _shell.Apply(psi, command);   // shell-correct quoting lives on ShellSpec

        // Suites are legitimately slow, so the deadline is generous — but unattended
        // runs still need one (RATCHET_TEST_TIMEOUT_SECS to override).
        var timeout = TimeSpan.FromSeconds(
            int.TryParse(Environment.GetEnvironmentVariable("RATCHET_TEST_TIMEOUT_SECS"), out var s) && s > 0 ? s : 600);
        var (exitCode, output) = await ProcessRunner.RunAsync(psi, ct, timeout);
        return Summarize(command, output, exitCode);
    }

    private static string Summarize(string command, string raw, int exitCode)
    {
        int? Count(string label)
        {
            var m = Regex.Match(raw, label + @":\s*(\d+)", RegexOptions.IgnoreCase);
            return m.Success ? int.Parse(m.Groups[1].Value) : null;
        }

        var failed = Count("Failed");
        var passed = Count("Passed");
        var skipped = Count("Skipped");
        var total = Count("Total");

        // Failing test names: dotnet/vstest prints "  Failed <name> [..]"; xUnit "[...] <name> [FAIL]".
        var failures = new List<string>();
        foreach (Match m in Regex.Matches(raw, @"^\s*Failed\s+(.+?)\s*(?:\[|\(|$)", RegexOptions.Multiline))
            failures.Add(m.Groups[1].Value.Trim());
        foreach (Match m in Regex.Matches(raw, @"^\s*(.+?)\s*\[FAIL\]", RegexOptions.Multiline))
            failures.Add(m.Groups[1].Value.Trim());
        failures = failures.Where(f => f.Length > 0).Distinct().Take(50).ToList();

        var sb = new StringBuilder();
        sb.Append("$ ").AppendLine(command);

        var recognised = failed is not null || passed is not null;
        if (recognised)
        {
            sb.Append(exitCode == 0 && (failed ?? 0) == 0 ? "PASSED" : "FAILED").Append("  ·  ");
            sb.Append("failed: ").Append(failed ?? 0);
            sb.Append(", passed: ").Append(passed ?? 0);
            sb.Append(", skipped: ").Append(skipped ?? 0);
            if (total is not null) sb.Append(", total: ").Append(total);
            sb.Append("  (exit ").Append(exitCode).Append(')');
        }
        else
        {
            // Couldn't parse a summary — surface the tail so the model still sees something useful.
            sb.Append("(exit ").Append(exitCode).Append(") — no summary recognised; tail of output:\n");
            sb.Append(Tail(raw, 2000));
            return sb.ToString();
        }

        if (failures.Count > 0)
        {
            sb.AppendLine().Append("Failing tests:");
            foreach (var f in failures) sb.AppendLine().Append("  - ").Append(f);
        }
        return sb.ToString();
    }

    private static string Tail(string s, int chars) =>
        s.Length <= chars ? s : "…" + s[^chars..];
}
