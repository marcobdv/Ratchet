using System.Diagnostics;
using System.Text;
using CodeStack.Ratchet.Core;
using Xunit;

namespace CodeStack.Ratchet.Tests;

/// <summary>
/// The process substrate: bounded output, deadlines, closed stdin, and
/// shell-correct quoting. Process-spawning tests are Windows-only (CI runs
/// windows-latest); pure-logic tests run everywhere.
/// </summary>
public sealed class ProcessRunnerTests
{
    private static ProcessStartInfo Cmd(string command)
    {
        var psi = new ProcessStartInfo();
        ShellSpec.Cmd.Apply(psi, command);
        return psi;
    }

    [Fact]
    public async Task CapturesOutputAndExitCode()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (exit, output) = await ProcessRunner.RunAsync(Cmd("echo hello"), CancellationToken.None);
        Assert.Equal(0, exit);
        Assert.Contains("hello", output);
    }

    [Fact]
    public async Task Timeout_KillsTheProcess_AndReturnsPartialOutputAsFailure()
    {
        if (!OperatingSystem.IsWindows()) return;
        var sw = Stopwatch.StartNew();
        var (exit, output) = await ProcessRunner.RunAsync(
            Cmd("echo started & ping -n 30 127.0.0.1 >nul & echo finished"),
            CancellationToken.None, timeout: TimeSpan.FromSeconds(2));
        sw.Stop();

        Assert.NotEqual(0, exit);
        Assert.Contains("started", output);          // partial output survives
        Assert.Contains("timed out", output);
        Assert.DoesNotContain("finished", output);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), "the deadline must actually cut the run short");
    }

    [Fact]
    public async Task StdinIsClosed_SoAPromptingCommandFinishesInsteadOfHanging()
    {
        if (!OperatingSystem.IsWindows()) return;
        // `set /p` reads stdin; with stdin closed it gets EOF and returns instead of
        // blocking on the console. The 30s deadline is a backstop so a regression
        // fails the test rather than wedging the suite.
        var (_, output) = await ProcessRunner.RunAsync(
            Cmd("set /p answer=PROMPT: & echo done"),
            CancellationToken.None, timeout: TimeSpan.FromSeconds(30));
        Assert.Contains("done", output);
        Assert.DoesNotContain("timed out", output);
    }

    [Fact]
    public void BoundedBuffer_KeepsHeadAndTail_ElidesTheMiddle()
    {
        var buffer = new BoundedLineBuffer(2_000);
        for (var i = 0; i < 1_000; i++)
            buffer.AddLine($"line-{i:D4}");

        var text = buffer.ToString();
        Assert.True(text.Length < 4_000, $"output must stay near the cap (was {text.Length})");
        Assert.Contains("line-0000", text);            // head kept
        Assert.Contains("line-0999", text);            // tail kept
        Assert.Contains("omitted", text);              // the elision is marked
        Assert.DoesNotContain("line-0500", text);      // the middle is gone
    }

    [Fact]
    public void BoundedBuffer_UnderTheCap_IsVerbatim()
    {
        var buffer = new BoundedLineBuffer(10_000);
        buffer.AddLine("one");
        buffer.AddLine("two");
        Assert.Equal("one\ntwo\n", buffer.ToString());
    }

    [Fact]
    public void BoundedBuffer_TruncatesAPathologicalSingleLine()
    {
        var buffer = new BoundedLineBuffer(100_000);
        buffer.AddLine(new string('x', 500_000));
        Assert.True(buffer.ToString().Length < 30_000);
    }
}

public sealed class BashToolQuotingTests
{
    [Fact]
    public async Task CmdShell_PreservesEmbeddedDoubleQuotes()
    {
        if (!OperatingSystem.IsWindows()) return;
        // Through ArgumentList this became `echo \"a b\"` and cmd printed the backslashes.
        var bash = new BashTool(ShellSpec.Cmd);
        var result = await bash.ExecuteAsync("""{"command":"echo \"a b\""}""", CancellationToken.None);
        Assert.Contains("\"a b\"", result);
        Assert.DoesNotContain("\\\"", result);
    }

    [Fact]
    public async Task TimeoutSecs_IsHonoured()
    {
        if (!OperatingSystem.IsWindows()) return;
        var bash = new BashTool(ShellSpec.Cmd);
        var result = await bash.ExecuteAsync(
            """{"command":"ping -n 30 127.0.0.1 >nul","timeout_secs":1}""", CancellationToken.None);
        Assert.Contains("timed out", result);
    }
}

public sealed class ToolRegistryTests
{
    private sealed class NamedTool(string name) : ITool
    {
        public string Name => name;
        public string Description => "t";
        public string InputSchemaJson => """{"type":"object","properties":{}}""";
        public Task<string> ExecuteAsync(string inputJson, CancellationToken ct) => Task.FromResult("ok");
    }

    [Fact]
    public void DuplicateToolNames_FailWithADiagnosticError_NamingTheTool()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new ToolRegistry(new ITool[] { new NamedTool("read"), new NamedTool("read") }));
        Assert.Contains("'read'", ex.Message);
        Assert.Contains("MCP", ex.Message); // points at the likely culprit
    }
}

public sealed class ScopedReadToolTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "ratchet-scoperead-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;

    public ScopedReadToolTests()
    {
        _root = Path.Combine(_base, "proj");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_base, "proj2"));
        File.WriteAllText(Path.Combine(_root, "inside.txt"), "inside contents");
        File.WriteAllText(Path.Combine(_base, "proj2", "secret.txt"), "SECRET");
        File.WriteAllText(Path.Combine(_base, "outside.txt"), "OUTSIDE");
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { }
    }

    private Task<string> Read(string path) =>
        new ReadTool(access: null, root: _root).ExecuteAsync(
            System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["path"] = path }),
            CancellationToken.None);

    [Fact]
    public async Task InsideTheWorkspace_RelativeAndAbsolute_AreAllowed()
    {
        Assert.Equal("inside contents", await Read("inside.txt"));
        Assert.Equal("inside contents", await Read(Path.Combine(_root, "inside.txt")));
    }

    [Fact]
    public async Task EscapesAreRefused_AndContentNeverLeaks()
    {
        var byDotDot = await Read(Path.Combine("..", "outside.txt"));
        var bySiblingPrefix = await Read(Path.Combine("..", "proj2", "secret.txt"));
        var byAbsolute = await Read(Path.Combine(_base, "proj2", "secret.txt"));

        foreach (var result in new[] { byDotDot, bySiblingPrefix, byAbsolute })
        {
            Assert.Contains("outside this agent's workspace", result);
            Assert.DoesNotContain("SECRET", result);
            Assert.DoesNotContain("OUTSIDE", result);
        }
    }

    [Fact]
    public async Task UnscopedReadTool_StillReadsAnywhere()
    {
        // The top-level agent stays YOLO by design (ADR-0009) — only delegates are scoped.
        var result = await new ReadTool().ExecuteAsync(
            System.Text.Json.JsonSerializer.Serialize(
                new Dictionary<string, string> { ["path"] = Path.Combine(_base, "outside.txt") }),
            CancellationToken.None);
        Assert.Equal("OUTSIDE", result);
    }
}

public sealed class SearchToolScopeTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "ratchet-scope-" + Guid.NewGuid().ToString("N"));

    public SearchToolScopeTests()
    {
        Directory.CreateDirectory(Path.Combine(_base, "proj"));
        Directory.CreateDirectory(Path.Combine(_base, "proj2"));
        File.WriteAllText(Path.Combine(_base, "proj2", "secret.txt"), "outside");
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { }
    }

    [Fact]
    public async Task SiblingDirectoryWithMatchingPrefix_IsRejected()
    {
        // root ...\proj, path ..\proj2 resolves to ...\proj2 — a bare StartsWith let it through.
        var search = new SearchTool(Path.Combine(_base, "proj"));
        var result = await search.ExecuteAsync("""{"path":"..\\proj2"}""", CancellationToken.None);
        Assert.Contains("outside the working directory", result);
    }

    [Fact]
    public async Task ParentEscape_IsRejected()
    {
        var search = new SearchTool(Path.Combine(_base, "proj"));
        var result = await search.ExecuteAsync("""{"path":".."}""", CancellationToken.None);
        Assert.Contains("outside the working directory", result);
    }
}

public sealed class EditToolEncodingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ratchet-edit-" + Guid.NewGuid().ToString("N"));

    public EditToolEncodingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static Task<string> Edit(string path, string oldStr, string newStr)
    {
        var input = System.Text.Json.JsonSerializer.Serialize(
            new Dictionary<string, string> { ["path"] = path, ["old_str"] = oldStr, ["new_str"] = newStr });
        return new EditTool().ExecuteAsync(input, CancellationToken.None);
    }

    [Fact]
    public async Task Utf8Bom_SurvivesAnEdit()
    {
        var path = Write("bom.cs", new UTF8Encoding(true).GetPreamble()
            .Concat("var x = 1;"u8.ToArray()).ToArray());

        var result = await Edit(path, "x = 1", "x = 2");

        Assert.StartsWith("Edited", result);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray()); // BOM intact
        Assert.Contains("x = 2", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task Utf16File_StaysUtf16()
    {
        var path = Write("wide.txt", Encoding.Unicode.GetPreamble()
            .Concat(Encoding.Unicode.GetBytes("hello world")).ToArray());

        var result = await Edit(path, "world", "there");

        Assert.StartsWith("Edited", result);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xFF, 0xFE }, bytes.Take(2).ToArray());      // still UTF-16 LE
        Assert.Equal("hello there", Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2));
    }

    [Fact]
    public async Task UnknownLegacyEncoding_IsRefused_NotCorrupted()
    {
        // 0x93/0x94 are curly quotes in Windows-1252 and invalid UTF-8.
        var original = new byte[] { (byte)'a', 0x93, (byte)'b', 0x94, (byte)'c' };
        var path = Write("legacy.txt", original);

        var result = await Edit(path, "a", "z");

        Assert.Contains("refusing to edit", result);
        Assert.Equal(original, File.ReadAllBytes(path)); // untouched
    }

    [Fact]
    public async Task LfOldStr_MatchesACrlfFile_AndPreservesCrlf()
    {
        var path = Write("crlf.cs", Encoding.UTF8.GetBytes("line one\r\nline two\r\nline three\r\n"));

        var result = await Edit(path, "line one\nline two", "line 1\nline 2");

        Assert.StartsWith("Edited", result);
        var text = File.ReadAllText(path);
        Assert.Equal("line 1\r\nline 2\r\nline three\r\n", text); // CRLF preserved throughout
    }

    [Fact]
    public async Task NoMatchOnACrlfFile_MentionsLineEndings()
    {
        var path = Write("crlf2.cs", Encoding.UTF8.GetBytes("alpha\r\nbeta\r\n"));
        var result = await Edit(path, "gamma\ndelta", "x");
        Assert.Contains("CRLF", result);
    }
}

public sealed class ReadToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ratchet-read-" + Guid.NewGuid().ToString("N"));

    public ReadToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task BinaryFile_IsReportedNotDumped()
    {
        var path = Path.Combine(_dir, "blob.bin");
        File.WriteAllBytes(path, new byte[] { 0x4D, 0x5A, 0x00, 0x00, 0x01, 0x02 });

        var result = await new ReadTool().ExecuteAsync(
            System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["path"] = path }),
            CancellationToken.None);

        Assert.Contains("binary", result);
        Assert.DoesNotContain("MZ", result); // the raw content never reaches the transcript
    }

    private string WriteLines(string name, int count)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllLines(path, Enumerable.Range(1, count).Select(i => $"line-{i:D4}"));
        return path;
    }

    private static Task<string> Read(string path, int? offset = null, int? limit = null)
    {
        var input = new Dictionary<string, object> { ["path"] = path };
        if (offset is { } o) input["offset"] = o;
        if (limit is { } l) input["limit"] = l;
        return new ReadTool().ExecuteAsync(
            System.Text.Json.JsonSerializer.Serialize(input), CancellationToken.None);
    }

    [Fact]
    public async Task OffsetAndLimit_ReturnExactlyThatWindow_WithItsPositionNamed()
    {
        var path = WriteLines("paged.txt", 100);
        var result = await Read(path, offset: 41, limit: 3);

        Assert.Contains("line-0041", result);
        Assert.Contains("line-0043", result);
        Assert.DoesNotContain("line-0040", result);
        Assert.DoesNotContain("line-0044", result);
        Assert.Contains("lines 41–43 of ", result);
    }

    [Fact]
    public async Task WholeSmallFile_ReturnsVerbatim_NoFramingNoise()
    {
        var path = Path.Combine(_dir, "small.txt");
        File.WriteAllText(path, "alpha\nbeta");
        Assert.Equal("alpha\nbeta", await Read(path));
    }

    [Fact]
    public async Task TruncatedBigFile_NamesTheNextOffset_AndItActuallyContinues()
    {
        // ~60k lines x ~10 chars comfortably exceeds the 256 KB cap.
        var path = WriteLines("big.txt", 60_000);
        var first = await Read(path);

        Assert.Contains("continue with offset=", first);
        var next = int.Parse(System.Text.RegularExpressions.Regex.Match(first, @"offset=(\d+)").Groups[1].Value);
        Assert.DoesNotContain($"line-{next:D4}", first);   // the named offset is genuinely unread

        var second = await Read(path, offset: next, limit: 5);
        Assert.Contains($"line-{next:D4}", second);        // paging picks up exactly where it stopped
    }

    [Fact]
    public async Task OffsetPastTheEnd_SaysSo()
    {
        var path = WriteLines("short.txt", 10);
        var result = await Read(path, offset: 50);
        Assert.Contains("past the end", result);
        Assert.Contains("10 lines", result);
    }

    [Fact]
    public async Task NonUtf8TextFile_IsShownLossily_ButNotMarkedEditable()
    {
        var path = Path.Combine(_dir, "legacy.txt");
        File.WriteAllBytes(path, new byte[] { (byte)'h', (byte)'i', 0x93, (byte)'!', (byte)'\n' });

        var access = new FileAccessLog();
        var result = await new ReadTool(access).ExecuteAsync(
            System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["path"] = path }),
            CancellationToken.None);

        Assert.Contains("not valid UTF-8", result);
        Assert.False(access.IsKnown(path)); // editing stays disabled for it
    }
}
