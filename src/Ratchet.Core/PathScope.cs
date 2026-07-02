namespace CodeStack.Ratchet.Core;

/// <summary>
/// The one path-containment check, shared by every workspace-scoped tool. Separator-aware:
/// a bare StartsWith would let <c>..\proj2</c> pass for root <c>...\proj</c> (the classic
/// prefix bypass). Case-insensitive only on Windows.
/// </summary>
internal static class PathScope
{
    /// <summary>True when <paramref name="fullPath"/> (already normalized via GetFullPath)
    /// is the root itself or inside it.</summary>
    public static bool IsWithin(string root, string fullPath)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var rootTrimmed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return string.Equals(fullPath, rootTrimmed, comparison)
            || fullPath.StartsWith(rootTrimmed + Path.DirectorySeparatorChar, comparison);
    }
}
