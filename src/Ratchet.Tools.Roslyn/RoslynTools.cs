using System.Text;
using System.Text.Json;
using CodeStack.Ratchet.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;

namespace CodeStack.Ratchet.Tools.Roslyn;

/// <summary>Builds the Roslyn semantic-C# tools over a shared <see cref="RoslynWorkspace"/>.</summary>
public sealed class RoslynToolset : IDisposable
{
    private readonly RoslynWorkspace _workspace;

    public RoslynToolset(string workingDirectory)
    {
        _workspace = new RoslynWorkspace(workingDirectory);
        Tools =
        [
            new DiagnosticsTool(_workspace),
            new FindSymbolTool(_workspace),
            new FindReferencesTool(_workspace),
            new OutlineTool(_workspace),
            new RenameTool(_workspace),
        ];
    }

    public IReadOnlyList<ITool> Tools { get; }

    public void Dispose() => _workspace.Dispose();
}

internal abstract class RoslynToolBase(RoslynWorkspace workspace) : ITool
{
    protected RoslynWorkspace Workspace { get; } = workspace;

    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract string InputSchemaJson { get; }
    public abstract Task<string> ExecuteAsync(string inputJson, CancellationToken ct);

    protected string Loc(Location location)
    {
        var span = location.GetLineSpan();
        var path = string.IsNullOrEmpty(span.Path) ? "?" : Path.GetRelativePath(Workspace.WorkingDirectory, span.Path);
        return $"{path}:{span.StartLinePosition.Line + 1}";
    }

    protected static async Task<List<ISymbol>> ResolveSymbolsAsync(Solution solution, string name, string? containingType, CancellationToken ct)
    {
        var symbols = (await SymbolFinder.FindSourceDeclarationsAsync(solution, name, ignoreCase: false, ct))
            .Where(s => s.Locations.Any(l => l.IsInSource))
            .ToList();
        if (!string.IsNullOrWhiteSpace(containingType))
            symbols = symbols.Where(s => string.Equals(s.ContainingType?.Name, containingType, StringComparison.Ordinal)).ToList();
        return symbols;
    }

    protected static string? Req(string json, string prop)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty(prop, out var v) ? v.GetString() : null;
    }

    protected static string Opt(string json, string prop)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty(prop, out var v) ? v.GetString() ?? "" : "";
    }
}

internal sealed class DiagnosticsTool(RoslynWorkspace ws) : RoslynToolBase(ws)
{
    public override string Name => "roslyn_diagnostics";
    public override string Description =>
        "Compile the C# project/solution with Roslyn and report errors and warnings (file:line). " +
        "Use this to verify edits compile — faster and more precise than a full build.";
    public override string InputSchemaJson => """
        {"type":"object","properties":{"project":{"type":"string","description":"Optional path to a .sln/.csproj; defaults to auto-detect."}},"required":[]}
        """;

    public override async Task<string> ExecuteAsync(string inputJson, CancellationToken ct)
    {
        var project = Opt(inputJson, "project");
        var (solution, error) = await Workspace.EnsureLoadedAsync(string.IsNullOrWhiteSpace(project) ? null : project, reload: true, ct);
        if (solution is null) return $"Error: {error}";

        // Collect across ALL projects first, then order globally — otherwise the per-project
        // 200-row cap could spend the budget on one project's warnings and drop another's errors
        // from the listing while still counting them.
        var all = new List<Diagnostic>();
        foreach (var proj in solution.Projects)
        {
            var compilation = await proj.GetCompilationAsync(ct);
            if (compilation is null) continue;
            all.AddRange(compilation.GetDiagnostics(ct)
                .Where(d => d.Severity >= DiagnosticSeverity.Warning && d.Location.IsInSource));
        }

        var errors = all.Count(d => d.Severity == DiagnosticSeverity.Error);
        var warnings = all.Count - errors;

        var sb = new StringBuilder();
        if (Workspace.LastLoadWarning is { } warn) sb.AppendLine(warn);   // partial-load diagnostics
        foreach (var diag in all.OrderByDescending(d => d.Severity).Take(200))
            sb.AppendLine($"{diag.Severity.ToString().ToLowerInvariant()} {diag.Id} {Loc(diag.Location)}: {diag.GetMessage()}");

        if (errors + warnings == 0)
            return (Workspace.LastLoadWarning is null ? "" : Workspace.LastLoadWarning + "\n") +
                   $"No errors or warnings in {Path.GetFileName(Workspace.LoadedTarget!)}.";
        return $"{errors} error(s), {warnings} warning(s) in {Path.GetFileName(Workspace.LoadedTarget!)}:\n{sb}";
    }
}

internal sealed class FindSymbolTool(RoslynWorkspace ws) : RoslynToolBase(ws)
{
    public override string Name => "roslyn_find_symbol";
    public override string Description => "Find where a C# symbol (type, method, property, field) is declared, by name, across the solution.";
    public override string InputSchemaJson => """
        {"type":"object","properties":{"name":{"type":"string","description":"Symbol name to find."}},"required":["name"]}
        """;

    public override async Task<string> ExecuteAsync(string inputJson, CancellationToken ct)
    {
        var name = Req(inputJson, "name");
        if (string.IsNullOrWhiteSpace(name)) return "Error: 'name' is required.";
        var (solution, error) = await Workspace.EnsureLoadedAsync(null, reload: false, ct);
        if (solution is null) return $"Error: {error}";

        var symbols = (await SymbolFinder.FindSourceDeclarationsAsync(solution, name, ignoreCase: false, ct))
            .Where(s => s.Locations.Any(l => l.IsInSource)).Take(50).ToList();
        if (symbols.Count == 0) return $"No declarations named '{name}' were found.";

        var sb = new StringBuilder();
        foreach (var symbol in symbols)
        {
            var loc = symbol.Locations.FirstOrDefault(l => l.IsInSource);
            sb.AppendLine($"{symbol.Kind.ToString().ToLowerInvariant()} {symbol.ToDisplayString()}  —  {(loc is null ? "?" : Loc(loc))}");
        }
        return sb.ToString();
    }
}

internal sealed class FindReferencesTool(RoslynWorkspace ws) : RoslynToolBase(ws)
{
    public override string Name => "roslyn_find_references";
    public override string Description => "Find every reference to a C# symbol across the solution (call sites, usages), as file:line.";
    public override string InputSchemaJson => """
        {"type":"object","properties":{"name":{"type":"string","description":"Symbol name."},"containingType":{"type":"string","description":"Optional containing type to disambiguate."}},"required":["name"]}
        """;

    public override async Task<string> ExecuteAsync(string inputJson, CancellationToken ct)
    {
        var name = Req(inputJson, "name");
        if (string.IsNullOrWhiteSpace(name)) return "Error: 'name' is required.";
        var (solution, error) = await Workspace.EnsureLoadedAsync(null, reload: false, ct);
        if (solution is null) return $"Error: {error}";

        var symbols = await ResolveSymbolsAsync(solution, name, Opt(inputJson, "containingType"), ct);
        if (symbols.Count == 0) return $"No declarations named '{name}' were found.";

        var sb = new StringBuilder();
        var total = 0;
        foreach (var symbol in symbols)
        {
            var references = await SymbolFinder.FindReferencesAsync(symbol, solution, ct);
            var locations = references.SelectMany(r => r.Locations)
                .Where(l => l.Location.IsInSource).Select(l => Loc(l.Location)).Distinct().ToList();
            sb.AppendLine($"{symbol.ToDisplayString()} — {locations.Count} reference(s):");
            foreach (var loc in locations.Take(200)) { sb.AppendLine($"  {loc}"); total++; }
        }
        return total == 0 ? "No references found." : sb.ToString();
    }
}

internal sealed class OutlineTool(RoslynWorkspace ws) : RoslynToolBase(ws)
{
    public override string Name => "roslyn_outline";
    public override string Description => "Show the outline of a C# file: namespaces, types, and members with line numbers.";
    public override string InputSchemaJson => """
        {"type":"object","properties":{"file":{"type":"string","description":"Path to the .cs file."}},"required":["file"]}
        """;

    public override async Task<string> ExecuteAsync(string inputJson, CancellationToken ct)
    {
        var file = Req(inputJson, "file");
        if (string.IsNullOrWhiteSpace(file)) return "Error: 'file' is required.";
        var full = Path.IsPathRooted(file) ? Path.GetFullPath(file) : Path.GetFullPath(Path.Combine(Workspace.WorkingDirectory, file));
        if (!File.Exists(full)) return $"Error: file not found: {file}";

        SyntaxNode root;
        var (solution, _) = await Workspace.EnsureLoadedAsync(null, reload: false, ct);
        var docId = solution?.GetDocumentIdsWithFilePath(full).FirstOrDefault();
        if (solution is not null && docId is not null)
            root = (await solution.GetDocument(docId)!.GetSyntaxRootAsync(ct))!;
        else
            root = await CSharpSyntaxTree.ParseText(await File.ReadAllTextAsync(full, ct), cancellationToken: ct).GetRootAsync(ct);

        var sb = new StringBuilder();
        WriteOutline(root, 0, sb);
        return sb.Length == 0 ? "(no declarations found)" : sb.ToString();
    }

    private static void WriteOutline(SyntaxNode node, int depth, StringBuilder sb)
    {
        foreach (var child in node.ChildNodes())
        {
            var (label, recurse) = child switch
            {
                BaseNamespaceDeclarationSyntax ns => ($"namespace {ns.Name}", true),
                ClassDeclarationSyntax c => ($"class {c.Identifier.Text}", true),
                StructDeclarationSyntax s => ($"struct {s.Identifier.Text}", true),
                InterfaceDeclarationSyntax i => ($"interface {i.Identifier.Text}", true),
                RecordDeclarationSyntax r => ($"record {r.Identifier.Text}", true),
                EnumDeclarationSyntax e => ($"enum {e.Identifier.Text}", false),
                ConstructorDeclarationSyntax ctor => ($"ctor {ctor.Identifier.Text}()", false),
                MethodDeclarationSyntax m => ($"method {m.Identifier.Text}()", false),
                PropertyDeclarationSyntax p => ($"property {p.Identifier.Text}", false),
                FieldDeclarationSyntax f => ($"field {f.Declaration.Variables.FirstOrDefault()?.Identifier.Text}", false),
                _ => ((string?)null, false),
            };

            if (label is not null)
            {
                var line = child.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                sb.AppendLine($"{new string(' ', depth * 2)}{label}  (L{line})");
                if (recurse) WriteOutline(child, depth + 1, sb);
            }
            else if (child is CompilationUnitSyntax)
            {
                WriteOutline(child, depth, sb);
            }
        }
    }
}

internal sealed class RenameTool(RoslynWorkspace ws) : RoslynToolBase(ws)
{
    public override string Name => "roslyn_rename";
    public override string Description =>
        "Rename a C# symbol across the whole solution (declaration and all references), semantically and safely. " +
        "Writes the changed files.";
    public override string InputSchemaJson => """
        {"type":"object","properties":{"name":{"type":"string"},"newName":{"type":"string"},"containingType":{"type":"string","description":"Optional containing type to disambiguate."}},"required":["name","newName"]}
        """;

    public override async Task<string> ExecuteAsync(string inputJson, CancellationToken ct)
    {
        var name = Req(inputJson, "name");
        var newName = Req(inputJson, "newName");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(newName))
            return "Error: 'name' and 'newName' are required.";

        var (solution, error) = await Workspace.EnsureLoadedAsync(null, reload: false, ct);
        if (solution is null) return $"Error: {error}";

        var symbols = await ResolveSymbolsAsync(solution, name, Opt(inputJson, "containingType"), ct);
        if (symbols.Count == 0) return $"No declarations named '{name}' were found.";
        if (symbols.Count > 1)
            return $"'{name}' is ambiguous ({symbols.Count} matches). Disambiguate with containingType:\n" +
                   string.Join("\n", symbols.Select(s => "  " + s.ToDisplayString()));

        var symbol = symbols[0];
        var updated = await Renamer.RenameSymbolAsync(solution, symbol, new SymbolRenameOptions(), newName, ct);

        var changedFiles = updated.GetChanges(solution).GetProjectChanges()
            .SelectMany(p => p.GetChangedDocuments())
            .Select(id => updated.GetDocument(id)?.FilePath)
            .Where(p => p is not null).Distinct()
            .Select(p => Path.GetRelativePath(Workspace.WorkingDirectory, p!)).ToList();

        if (changedFiles.Count == 0) return "Rename produced no changes.";
        if (!Workspace.TryApplyChanges(updated))
            return "Error: failed to apply the rename to disk (workspace may be stale; run roslyn_diagnostics to reload).";

        return $"Renamed {symbol.Name} to {newName} across {changedFiles.Count} file(s):\n{string.Join("\n", changedFiles)}";
    }
}
