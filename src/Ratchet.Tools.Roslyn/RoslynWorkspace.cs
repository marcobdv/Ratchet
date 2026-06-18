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

    public RoslynWorkspace(string workingDirectory) => _workingDirectory = workingDirectory;

    public string WorkingDirectory => _workingDirectory;
    public string? LoadedTarget => _loadedTarget;

    public async Task<(Solution? Solution, string? Error)> EnsureLoadedAsync(string? explicitPath, bool reload, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var target = ResolveTarget(_workingDirectory, explicitPath);
            if (target is null)
                return (null, "No .sln or .csproj found under the working directory. Pass an explicit project path.");

            if (!reload && _workspace is not null && string.Equals(_loadedTarget, target, StringComparison.OrdinalIgnoreCase))
                return (_workspace.CurrentSolution, null);

            _workspace?.Dispose();
            MsBuildBootstrap.Ensure();
            _workspace = MSBuildWorkspace.Create();

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
            var solution = _workspace.CurrentSolution;
            return solution.Projects.Any()
                ? (solution, null)
                : (null, $"Loaded {Path.GetFileName(target)} but found no projects.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool TryApplyChanges(Solution solution) => _workspace?.TryApplyChanges(solution) ?? false;

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
