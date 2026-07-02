using System.Diagnostics;

namespace CodeStack.Ratchet.Core;

/// <summary>
/// Which shell the bash tool drives, and how to invoke it. Centralising this
/// here keeps the tool itself shell-agnostic: pick the spec at startup, pass it
/// in. The "command flag" is the argument that says "run this string and exit"
/// — /c for cmd, -c for bash/zsh, -Command for PowerShell.
/// </summary>
public sealed record ShellSpec(string Name, string FileName, string CommandFlag)
{
    public static readonly ShellSpec Cmd  = new("cmd",  "cmd.exe",  "/c");
    public static readonly ShellSpec Bash = new("bash", "/bin/bash", "-c");
    public static readonly ShellSpec Pwsh = new("pwsh", "pwsh",     "-Command");

    /// <summary>
    /// Configure <paramref name="psi"/> to run <paramref name="command"/> under this shell.
    /// cmd.exe is the special case: it parses its OWN quoting rules, not C-runtime argv
    /// rules — ArgumentList would encode `echo "a b"` as `\"a b\"` and cmd runs it
    /// corrupted. cmd's convention: /d (skip AutoRun) /s (strip outer quotes) /c "verbatim".
    /// bash and pwsh parse argv normally, so ArgumentList is correct for them.
    /// </summary>
    public void Apply(ProcessStartInfo psi, string command)
    {
        psi.FileName = FileName;
        if (Name == "cmd")
        {
            psi.Arguments = $"/d /s /c \"{command}\"";
        }
        else
        {
            psi.ArgumentList.Add(CommandFlag);
            psi.ArgumentList.Add(command);
        }
    }

    /// <summary>The same invocation as one command line (for the ConPTY path).</summary>
    public string CommandLine(string command) =>
        Name == "cmd" ? $"{FileName} /d /s /c \"{command}\"" : $"{FileName} {CommandFlag} {QuoteArg(command)}";

    /// <summary>Standard Windows argv quoting for a single argument (backslash + quote rules).</summary>
    private static string QuoteArg(string s)
    {
        if (s.Length > 0 && s.IndexOfAny(new[] { ' ', '\t', '"', '\n' }) < 0) return s;
        var sb = new System.Text.StringBuilder("\"");
        var slashes = 0;
        foreach (var c in s)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { sb.Append('\\', slashes * 2 + 1).Append('"'); slashes = 0; continue; }
            if (slashes > 0) { sb.Append('\\', slashes); slashes = 0; }
            sb.Append(c);
        }
        sb.Append('\\', slashes * 2).Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// Resolve from a name (case-insensitive). Anything unrecognised falls back
    /// to an OS-appropriate default: cmd on Windows, bash elsewhere.
    /// </summary>
    public static ShellSpec FromName(string? name) =>
        (name?.Trim().ToLowerInvariant()) switch
        {
            "pwsh" or "powershell" => Pwsh,
            "bash"                 => Bash,
            "cmd"                  => Cmd,
            _ => OperatingSystem.IsWindows() ? Cmd : Bash
        };
}
