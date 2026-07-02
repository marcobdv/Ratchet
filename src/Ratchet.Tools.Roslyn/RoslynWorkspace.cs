using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace CodeStack.Ratchet.Tools.Roslyn;

/// <summary>
/// Registers the installed .NET SDK's MSBuild with <see cref="MSBuildLocator"/>. Must run before any
/// <c>Microsoft.CodeAnalysis.MSBuild</c> type is touched — the CLI calls it as its first line.
/// </summary>
public static class MsBuildBootstrap
{
    private static bool _registered;

    public static void Ensure()
    {
        if (_registered) return;
        if (!MSBuildLocator.IsRegistered)
        {
            var instance = MSBuildLocator.QueryVisualStudioInstances().OrderByDescending(i => i.Version).FirstOrDefault();
            if (instance is not null) MSBuildLocator.RegisterInstance(instance);
            else MSBuildLocator.RegisterDefaults();
        }
        _registered = true;
    }
}

/// <summary>
/// Owns a Roslyn <see cref="MSBuildWorkspace"/> and lazily loads the workspace's solution or project so
/// the Roslyn tools can ask semantic questions. The loaded solution is cached; loading a different
/// target rebuilds the workspace.
/// </summary>
public sealed class RoslynWorkspace : IDisposable
{
    private static readonly string[] IgnoredDirs = [".git", "bin", "obj", "node_modules", ".vs", ".idea"];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _workingDirectory;
    private MSBuildWorkspace? _workspace;
    private string? _loadedTarget;
    private string? _lastLoadWarning;
    private long _loadedStamp;   // latest *.cs write-time at load, to skip rebuilds when nothing changed

    public RoslynWorkspace(string workingDirectory) => _workingDirectory = workingDirectory;

    public string WorkingDirectory => _workingDirectory;
    public string? LoadedTarget => _loadedTarget;

    /// <summary>Non-fatal load problems from the most recent load (e.g. a project that failed to load), or null.</summary>
    public string? LastLoadWarning => _lastLoadWarning;

    public async Task<(Solution? Solution, string? Error)> EnsureLoadedAsync(string? explicitPath, bool reload, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var target = ResolveTarget(_workingDirectory, explicitPath);
            if (target is null)
                return (null, "No .sln or .csproj found under the working directory. Pass an explicit project path.");

            // Cache hit: same target, and either the caller didn't force a reload OR no source
            // changed on disk since the last load. This stops roslyn_diagnostics (which asks for
            // reload to stay fresh) from paying a multi-second MSBuild rebuild on every call when
            // nothing actually changed.
            var stamp = LatestSourceStamp(_workingDirectory);
            if (_workspace is not null && string.Equals(_loadedTarget, target, StringComparison.OrdinalIgnoreCase)
                && (!reload || stamp == _loadedStamp))
                return (_workspace.CurrentSolution, null);

            _workspace?.Dispose();
            MsBuildBootstrap.Ensure();
            _workspace = MSBuildWorkspace.Create();

            // MSBuildWorkspace reports most load problems (missing refs, SDK resolution,
            // unsupported project types) via this event, NOT by throwing — so without it a
            // partial load looks successful and tools run on an incomplete compilation.
            var failures = new List<string>();
            _workspace.WorkspaceFailed += (_, e) =>
            {
                if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
                    lock (failures) failures.Add(e.Diagnostic.Message);
            };

            try
            {
                if (target.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
                    await _workspace.OpenSolutionAsync(target, cancellationToken: ct).ConfigureAwait(false);
                else
                    await _workspace.OpenProjectAsync(target, cancellationToken: ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return (null, $"Failed to load {Path.GetFileName(target)}: {ex.Message}");
            }

            _loadedTarget = target;
            _loadedStamp = stamp;
            var solution = _workspace.CurrentSolution;
            string? warning;
            lock (failures)
                warning = failures.Count == 0
                    ? null
                    : $"{failures.Count} workspace load problem(s) — results may be incomplete:\n  - " +
                      string.Join("\n  - ", failures.Take(20));
            _lastLoadWarning = warning;

            if (!solution.Projects.Any())
                return (null, $"Loaded {Path.GetFileName(target)} but found no projects." +
                              (warning is null ? "" : "\n" + warning));
            return (solution, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool TryApplyChanges(Solution solution) => _workspace?.TryApplyChanges(solution) ?? false;

    /// <summary>
    /// A stamp that changes whenever the workspace's inputs do, so a reload is skipped
    /// only when nothing relevant moved. Covers source AND project/build files — editing
    /// a .csproj (adding a PackageReference) then running diagnostics used to serve
    /// stale results because the stamp watched *.cs only. Folds in the file COUNT so a
    /// deletion (which lowers no surviving file's write-time) still invalidates.
    /// On error, returns "now" to force a reload. Ignores obj/bin.
    /// </summary>
    private static long LatestSourceStamp(string dir)
    {
        try
        {
            long max = 0, count = 0;
            var skip = $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}";
            var skip2 = $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}";
            foreach (var pattern in new[] { "*.cs", "*.csproj", "*.props", "*.targets", "*.sln" })
                foreach (var f in Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories))
                {
                    if (f.Contains(skip, StringComparison.OrdinalIgnoreCase) || f.Contains(skip2, StringComparison.OrdinalIgnoreCase))
                        continue;
                    count++;
                    var t = File.GetLastWriteTimeUtc(f).Ticks;
                    if (t > max) max = t;
                }
            return max ^ unchecked((long)((ulong)count * 0x9E3779B97F4A7C15));   // mix count in so deletions invalidate
        }
        catch { return DateTime.UtcNow.Ticks; }
    }

    private static string? ResolveTarget(string workingDirectory, string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var full = Path.IsPathRooted(explicitPath)
                ? Path.GetFullPath(explicitPath)
                : Path.GetFullPath(Path.Combine(workingDirectory, explicitPath));
            return File.Exists(full) ? full : null;
        }

        var rootSln = Directory.GetFiles(workingDirectory, "*.sln").FirstOrDefault();
        if (rootSln is not null) return rootSln;

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        return Enumerate(workingDirectory, "*.sln", options).FirstOrDefault()
            ?? Enumerate(workingDirectory, "*.csproj", options).FirstOrDefault();
    }

    private static IEnumerable<string> Enumerate(string root, string pattern, EnumerationOptions options)
    {
        foreach (var file in Directory.EnumerateFiles(root, pattern, options))
        {
            var relative = Path.GetRelativePath(root, file);
            if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(seg => IgnoredDirs.Contains(seg, StringComparer.OrdinalIgnoreCase)))
                continue;
            yield return file;
        }
    }

    public void Dispose()
    {
        _workspace?.Dispose();
        _gate.Dispose();
    }
}
