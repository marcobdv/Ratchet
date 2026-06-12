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
    public string Name => "read";
    public string Description => "Read the full contents of a file at the given path.";
    public string InputSchemaJson => """
        {"type":"object","properties":{"path":{"type":"string","description":"File path to read"}},"required":["path"]}
        """;

    public async Task<string> ExecuteAsync(string inputJson, CancellationToken ct)
    {
        var path = Json.GetString(inputJson, "path");
        if (!File.Exists(path)) return $"No file at '{path}'.";
        var text = await File.ReadAllTextAsync(path, ct);
        return text.Length == 0 ? "(file is empty)" : text;
    }
}

public sealed class WriteTool : ITool
{
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
        return $"Wrote {content.Length} chars to '{path}'.";
    }
}

public sealed class EditTool : ITool
{
    public string Name => "edit";
    public string Description =>
        "Replace the first occurrence of old_str with new_str in the file. old_str must match exactly and be unique enough to be unambiguous.";
    public string InputSchemaJson => """
        {"type":"object","properties":{"path":{"type":"string"},"old_str":{"type":"string"},"new_str":{"type":"string"}},"required":["path","old_str","new_str"]}
        """;

    public async Task<string> ExecuteAsync(string inputJson, CancellationToken ct)
    {
        var path = Json.GetString(inputJson, "path");
        var oldStr = Json.GetString(inputJson, "old_str");
        var newStr = Json.GetString(inputJson, "new_str");

        if (!File.Exists(path)) return $"No file at '{path}'.";
        var text = await File.ReadAllTextAsync(path, ct);

        var idx = text.IndexOf(oldStr, StringComparison.Ordinal);
        if (idx < 0) return "old_str not found in file; nothing changed.";

        var updated = text.Remove(idx, oldStr.Length).Insert(idx, newStr);
        await File.WriteAllTextAsync(path, updated, ct);
        return $"Edited '{path}'.";
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

    public BashTool(ShellSpec shell) => _shell = shell;

    public string Name => "bash";
    public string Description =>
        $"Execute a shell command via {_shell.Name} and return its combined stdout and stderr.";
    public string InputSchemaJson => """
        {"type":"object","properties":{"command":{"type":"string"}},"required":["command"]}
        """;

    public async Task<string> ExecuteAsync(string inputJson, CancellationToken ct)
    {
        var command = Json.GetString(inputJson, "command");

        var psi = new ProcessStartInfo
        {
            FileName = _shell.FileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(_shell.CommandFlag);
        psi.ArgumentList.Add(command);

        using var proc = new Process { StartInfo = psi };
        var output = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        await proc.WaitForExitAsync(ct);

        var text = output.ToString();
        return $"(exit {proc.ExitCode})\n{(text.Length == 0 ? "(no output)" : text)}";
    }
}
