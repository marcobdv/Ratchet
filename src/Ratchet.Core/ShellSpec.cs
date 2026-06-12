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
