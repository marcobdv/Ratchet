using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodeStack.Ratchet.Core;

/// <summary>
/// The four primitives, same set pi ships with. Everything else an agent might
/// do is expressible as a composition of these plus the model's reasoning.
/// Inputs are parsed from the raw JSON the model emitted.
/// </summary>
internal static class Json
{
    public static string GetString(string inputJson, string property)
    {
        using var doc = JsonDocument.Parse(inputJson);
        return doc.RootElement.GetProperty(property).GetString()
            ?? throw new ArgumentException($"'{property}' was null.");
    }

    public static string? GetStringOrNull(string inputJson, string property)
    {
        using var doc = JsonDocument.Parse(inputJson);
        return doc.RootElement.TryGetProperty(property, out var v) ? v.GetString() : null;
    }
}

public sealed class ReadTool : ITool
{
    private readonly FileAccessLog? _access;
    private readonly string? _root;

    /// <param name="access">Optional shared access log; reading a file marks it
    /// "known" so the edit tool will allow editing it (the read-before-write guard).</param>
    /// <param name="root">Optional workspace scope. When set, reads outside this
    /// directory are refused — read-only is not the same as scoped, and a delegated
    /// sub-agent must be both (ADR-0009: the constraint is structural, not prompted).</param>
    public ReadTool(FileAccessLog? access = null, string? root = null)
    {
        _access = access;
        _root = root is null ? null : Path.GetFullPath(root);
    }

    // Cap the returned text so one big file can't blow the context window (the whole
    // transcript is re-sent every model call). Override with RATCHET_READ_MAX_BYTES.
    private static readonly int MaxBytes =
        int.TryParse(Environment.GetEnvironmentVariable("RATCHET_READ_MAX_BYTES"), out var m) && m > 0 ? m : 256 * 1024;

    public string Name => "read";
    public string Description => "Read the full contents of a file at the given path (large files are truncated).";
    public string InputSchemaJson => """
        {"type":"object","properties":{"path":{"type":"string","description":"File path to read"}},"required":["path"]}
        """;

    public async Task<string> ExecuteAsync(string inputJson, CancellationToken ct)
    {
        var path = Json.GetString(inputJson, "path");

        if (_root is not null)
        {
            var full = Path.GetFullPath(path, _root);   // relative paths resolve against the scope
            if (!PathScope.IsWithin(_root, full))
                return $"'{path}' is outside this agent's workspace ('{_root}') — refused.";
            path = full;
        }

        if (!File.Exists(path)) return $"No file at '{path}'.";

        var bytes = await File.ReadAllBytesAsync(path, ct);
        if (FileText.LooksBinary(bytes))
            return $"'{path}' looks binary ({bytes.Length:N0} bytes) — not showing its content.";

        string text;
        try { (text, _) = FileText.Decode(bytes); }
        catch (System.Text.DecoderFallbackException)
        {
            // Unknown legacy encoding: degrade to a lossy read so the model can still
            // look, but don't mark it known — editing it would corrupt those bytes.
            text = System.Text.Encoding.UTF8.GetString(bytes);
            return $"(note: '{path}' is not valid UTF-8 — shown lossily; editing is disabled)\n" + Truncate(text);
        }

        _access?.MarkKnown(path);
        if (text.Length == 0) return "(file is empty)";
        return Truncate(text);
    }

    private static string Truncate(string text) =>
        text.Length <= MaxBytes
            ? text
            : text[..MaxBytes] + $"\n\n…(truncated: showing {MaxBytes} of {text.Length} chars)";
}

public sealed class WriteTool : ITool
{
    private readonly FileAccessLog? _access;

    /// <param name="access">Optional shared access log; writing a file marks it
    /// "known" so a file the agent just created can subsequently be edited.</param>
    public WriteTool(FileAccessLog? access = null) => _access = access;

    public string Name => "write";
    public string Description => "Write (create or overwrite) a file with the given content.";
    public string InputSchemaJson => """
        {"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"}},"required":["path","content"]}
        """;

    public async Task<string> ExecuteAsync(string inputJson, CancellationToken ct)
    {
        var path = Json.GetString(inputJson, "path");
        var content = Json.GetString(inputJson, "content");
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, content, ct);
        _access?.MarkKnown(path);
        return $"Wrote {content.Length} chars to '{path}'.";
    }
}

public sealed class EditTool : ITool
{
    private readonly FileAccessLog? _access;

    /// <param name="access">Optional shared access log. When supplied, the tool
    /// refuses to edit a file the agent hasn't read or written this session — the
    /// read-before-write guard that stops blind, context-free edits.</param>
    public EditTool(FileAccessLog? access = null) => _access = access;

    public string Name => "edit";
    public string Description =>
        "Replace old_str with new_str in the file. By default old_str must match exactly and occur " +
        "EXACTLY ONCE (include enough surrounding context to be unique); set replace_all=true to replace " +
        "every occurrence. Read the file first.";
    public string InputSchemaJson => """
        {"type":"object","properties":{"path":{"type":"string"},"old_str":{"type":"string"},"new_str":{"type":"string"},"replace_all":{"type":"boolean","description":"Replace every occurrence instead of requiring a unique match."}},"required":["path","old_str","new_str"]}
        """;

    public async Task<string> ExecuteAsync(string inputJson, CancellationToken ct)
    {
        var path = Json.GetString(inputJson, "path");
        var oldStr = Json.GetString(inputJson, "old_str");
        var newStr = Json.GetString(inputJson, "new_str");

        var replaceAll = false;
        using (var doc = JsonDocument.Parse(inputJson))
            if (doc.RootElement.TryGetProperty("replace_all", out var ra) &&
                (ra.ValueKind == JsonValueKind.True || ra.ValueKind == JsonValueKind.False))
                replaceAll = ra.GetBoolean();

        if (!File.Exists(path)) return $"No file at '{path}'.";

        // Read-before-write guard: don't edit a file the agent has never looked at.
        if (_access is not null && !_access.IsKnown(path))
            return $"Refusing to edit '{path}' — read it first so the edit is grounded in its current contents.";

        if (oldStr.Length == 0) return "old_str is empty; refusing to edit.";

        // Encoding-preserving read: keep the file's BOM/UTF-16 identity so the write-back
        // changes only the edited span, and refuse unknown encodings rather than corrupt them.
        string text;
        Encoding encoding;
        try { (text, encoding) = await FileText.ReadAsync(path, ct); }
        catch (DecoderFallbackException)
        {
            return $"'{path}' is not valid UTF-8 and has no BOM (unknown legacy encoding) — refusing to edit to avoid corrupting it.";
        }

        var count = CountOccurrences(text, oldStr);
        if (count == 0)
        {
            // The classic Windows miss: the model emits \n, the file uses \r\n. Retry with
            // the line endings normalized to the file's before giving up.
            if (!oldStr.Contains("\r\n", StringComparison.Ordinal) &&
                oldStr.Contains('\n') &&
                text.Contains("\r\n", StringComparison.Ordinal))
            {
                var oldCrlf = oldStr.Replace("\n", "\r\n", StringComparison.Ordinal);
                var crlfCount = CountOccurrences(text, oldCrlf);
                if (crlfCount > 0)
                {
                    oldStr = oldCrlf;
                    newStr = newStr.Replace("\r\n", "\n", StringComparison.Ordinal)
                                   .Replace("\n", "\r\n", StringComparison.Ordinal);
                    count = crlfCount;
                }
            }
            if (count == 0)
                return "old_str not found in file; nothing changed." +
                       (text.Contains("\r\n", StringComparison.Ordinal) && oldStr.Contains('\n')
                           ? " (note: this file uses CRLF line endings)" : "");
        }
        if (count > 1 && !replaceAll)
            return $"old_str occurs {count} times — add surrounding context to make it unique, or pass replace_all=true.";

        var updated = replaceAll
            ? text.Replace(oldStr, newStr)
            : ReplaceFirst(text, oldStr, newStr);

        await File.WriteAllTextAsync(path, updated, encoding, ct);
        _access?.MarkKnown(path);
        return count > 1 ? $"Edited '{path}' ({count} occurrences)." : $"Edited '{path}'.";
    }

    private static int CountOccurrences(string text, string sub)
    {
        int count = 0, i = 0;
        while ((i = text.IndexOf(sub, i, StringComparison.Ordinal)) >= 0) { count++; i += sub.Length; }
        return count;
    }

    private static string ReplaceFirst(string text, string oldStr, string newStr)
    {
        var idx = text.IndexOf(oldStr, StringComparison.Ordinal);
        return text.Remove(idx, oldStr.Length).Insert(idx, newStr);
    }
}

/// <summary>
/// YOLO shell, pi-style: no permission gate. Runs the command in whichever shell
/// the <see cref="ShellSpec"/> selects (cmd / bash / pwsh) and returns combined
/// stdout/stderr. The ConPTY upgrade your Ratchet doc calls for is a later
/// replacement for this Process-based implementation.
///
/// Still named "bash" so the model keeps a stable tool name regardless of which
/// shell backs it; the description tells the model which one is actually live.
/// </summary>
public sealed class BashTool : ITool
{
    private readonly ShellSpec _shell;
    private readonly bool _usePty;

    /// <param name="usePty">When true (and on Windows), run the command under a
    /// ConPTY pseudo-console instead of a redirected <see cref="Process"/>. Gives
    /// the child a real TTY at the cost of noisier, VT-laden output — opt-in.</param>
    public BashTool(ShellSpec shell, bool usePty = false)
    {
        _shell = shell;
        _usePty = usePty && WindowsPty.IsSupported;
    }

    /// <summary>Default command deadline; override with RATCHET_BASH_TIMEOUT_SECS or per-call timeout_secs.</summary>
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("RATCHET_BASH_TIMEOUT_SECS"), out var s) && s > 0 ? s : 120);

    public string Name => "bash";
    public string Description =>
        $"Execute a shell command via {_shell.Name}{(_usePty ? " (pseudo-console)" : "")} and return its combined stdout and stderr. " +
        $"Commands are killed after timeout_secs (default {(int)DefaultTimeout.TotalSeconds}s); stdin is closed, so interactive prompts fail fast.";
    public string InputSchemaJson => """
        {"type":"object","properties":{"command":{"type":"string"},"timeout_secs":{"type":"integer","description":"Deadline for this command in seconds (default 120, max 3600)."}},"required":["command"]}
        """;

    public async Task<string> ExecuteAsync(string inputJson, CancellationToken ct)
    {
        var command = Json.GetString(inputJson, "command");

        var timeout = DefaultTimeout;
        using (var doc = JsonDocument.Parse(inputJson))
            if (doc.RootElement.TryGetProperty("timeout_secs", out var ts) && ts.TryGetInt32(out var secs))
                timeout = TimeSpan.FromSeconds(Math.Clamp(secs, 1, 3600));

        if (_usePty)
        {
            try
            {
                // One command line, quoted per the shell's own rules (see ShellSpec).
                var commandLine = _shell.CommandLine(command);
                var (exit, ptyOutput) = await WindowsPty.RunAsync(commandLine, ct);
                // The command ran under the pty — trust its result and return it. We do
                // NOT fall back to the Process path on empty output, because that would
                // re-execute a command that already ran (and may have had side effects).
                // Fallback happens only on an exception below, i.e. when the command
                // never started. A real terminal renders content here; a nested/odd
                // console may yield only framing (empty) — that's the opt-in's tradeoff.
                return $"(exit {exit})\n{(ptyOutput.Trim().Length == 0 ? "(no captured output)" : ptyOutput)}";
            }
            catch (Exception ex)
            {
                // Setup/spawn failed — the command did not run, so re-running it on the
                // plain Process path is safe. Note why we fell back.
                Console.Error.WriteLine($"[pty fallback] {ex.Message}");
            }
        }

        var psi = new ProcessStartInfo();
        _shell.Apply(psi, command);   // shell-correct quoting lives on ShellSpec

        var (exitCode, text) = await ProcessRunner.RunAsync(psi, ct, timeout);
        return $"(exit {exitCode})\n{(text.Length == 0 ? "(no output)" : text)}";
    }
}
