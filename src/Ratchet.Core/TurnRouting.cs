using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodeStack.Ratchet.Core;

/// <summary>
/// Per-turn model routing for the REPL — the interactive exception ADR-0007 reserved.
/// The workflow's two-layer routing keys off gate ground truth, which an interactive
/// conversation doesn't have (there is no red gate mid-chat, and a failed cheap attempt
/// costs a user-visible pause). So the REPL gets the one thing the batch design rejected:
/// a small PREDICTIVE classify call per human turn. It stays inside the house rules —
/// the route table is readable config (not a learned router), the classifier's choice +
/// reasoning is printed and logged, and a wrong pick degrades to the table's default.
/// </summary>
public sealed record RouteSpec(string Name, string Description, string Provider, string Model)
{
    public string Tier => $"{Provider}:{Model}";
}

/// <summary>
/// The route table: named routes (cheap → expensive by convention), which one is the
/// fallback default, and the tier that runs the classify call. Loaded from
/// <c>.ratchet/routing.json</c> when present, else the built-in Anthropic ladder.
/// </summary>
public sealed class RouteTable
{
    public IReadOnlyList<RouteSpec> Routes { get; }
    public RouteSpec Default { get; }
    public RouteSpec Classifier { get; }

    public RouteTable(IReadOnlyList<RouteSpec> routes, string defaultRoute, RouteSpec? classifier = null)
    {
        if (routes.Count == 0) throw new RouteConfigException("routing: at least one route is required.");
        foreach (var r in routes)
            if (string.IsNullOrWhiteSpace(r.Name) || string.IsNullOrWhiteSpace(r.Provider) || string.IsNullOrWhiteSpace(r.Model))
                throw new RouteConfigException($"routing: route '{r.Name}' needs name, provider and model.");
        var dup = routes.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null) throw new RouteConfigException($"routing: duplicate route name '{dup.Key}'.");

        Routes = routes;
        Default = Find(routes, defaultRoute)
            ?? throw new RouteConfigException($"routing: default '{defaultRoute}' is not a defined route.");
        // No explicit classifier → the first route; by the cheap-first convention that is
        // the cheapest model in the table, which is what a classify call wants.
        Classifier = classifier ?? routes[0];
        if (string.IsNullOrWhiteSpace(Classifier.Provider) || string.IsNullOrWhiteSpace(Classifier.Model))
            throw new RouteConfigException("routing: classifier needs provider and model.");
    }

    public RouteSpec? Find(string name) => Find(Routes, name);

    private static RouteSpec? Find(IReadOnlyList<RouteSpec> routes, string name) =>
        routes.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The built-in table for when no <c>.ratchet/routing.json</c> exists: the
    /// Anthropic ladder, defaulting to the middle rung.</summary>
    public static RouteTable AnthropicDefault { get; } = new(
        new[]
        {
            new RouteSpec("quick", "a small, self-contained request: a one-file edit, a factual question " +
                "about the code, a rename, a formatting task, a short explanation", "anthropic", "claude-haiku-4-5-20251001"),
            new RouteSpec("standard", "ordinary coding work: implement a function or feature, fix a bug " +
                "with a known repro, write tests, refactor within a module", "anthropic", "claude-sonnet-4-6"),
            new RouteSpec("deep", "hard or wide work: architectural changes, multi-file refactors, subtle " +
                "debugging with no clear repro, performance analysis, security-sensitive code", "anthropic", "claude-opus-4-8"),
        },
        defaultRoute: "standard");

    /// <summary>Loads <c>.ratchet/routing.json</c> under <paramref name="workspaceDir"/>,
    /// or the built-in default when the file doesn't exist. Invalid content throws
    /// <see cref="RouteConfigException"/> — a present-but-broken table must fail loudly,
    /// not silently route everything to the default.</summary>
    public static RouteTable Load(string workspaceDir)
    {
        var path = Path.Combine(workspaceDir, ".ratchet", "routing.json");
        return File.Exists(path) ? Parse(File.ReadAllText(path), path) : AnthropicDefault;
    }

    public static RouteTable Parse(string json, string sourceName = "routing.json")
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = doc.RootElement;

            var routes = new List<RouteSpec>();
            if (root.TryGetProperty("routes", out var rs) && rs.ValueKind == JsonValueKind.Array)
                foreach (var r in rs.EnumerateArray())
                    routes.Add(new RouteSpec(
                        Str(r, "name"), Str(r, "description"), Str(r, "provider"), Str(r, "model")));

            RouteSpec? classifier = null;
            if (root.TryGetProperty("classifier", out var c) && c.ValueKind == JsonValueKind.Object)
                classifier = new RouteSpec("classifier", "", Str(c, "provider"), Str(c, "model"));

            var def = root.TryGetProperty("default", out var d) ? d.GetString() ?? "" : "";
            if (def.Length == 0 && routes.Count > 0) def = routes[0].Name;

            return new RouteTable(routes, def, classifier);
        }
        catch (RouteConfigException) { throw; }
        catch (Exception ex)
        {
            throw new RouteConfigException($"routing: could not parse {sourceName}: {ex.Message}");
        }

        static string Str(JsonElement e, string prop) =>
            e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    }
}

public sealed class RouteConfigException : Exception
{
    public RouteConfigException(string message) : base(message) { }
}

/// <summary>
/// One cheap classify call per human turn: which route should drive this request?
/// Mirrors the workflow intake classifier's shape (JSON answer, prose rescue,
/// graceful fallback) but falls back to the table's DEFAULT route, not the biggest —
/// interactively, a wrong expensive pick wastes money and a wrong cheap pick wastes
/// the user's time, so the middle of the table is the safe landing.
/// </summary>
public sealed class TurnRouter
{
    // Classify on a bounded prefix: routing needs the shape of the request, not a
    // pasted 40KB stack trace, and the classifier bills per token.
    private const int MaxClassifyChars = 2000;

    private readonly ILlmClient _classifier;
    private readonly RouteTable _table;

    public TurnRouter(ILlmClient classifier, RouteTable table)
    {
        _classifier = classifier;
        _table = table;
    }

    public async Task<(RouteSpec Route, string Reasoning)> RouteAsync(string userRequest, CancellationToken ct)
    {
        var menu = new StringBuilder();
        foreach (var r in _table.Routes)
            menu.Append("- ").Append(r.Name).Append(": ").Append(r.Description).Append('\n');

        var request = userRequest.Length <= MaxClassifyChars
            ? userRequest
            : userRequest[..MaxClassifyChars] + "\n…(truncated for classification)";

        var convo = new Conversation();
        convo.Add(Message.UserText(
            "Pick the single best route for the following request to a coding agent. The routes are " +
            "ordered cheapest to most capable; pick the CHEAPEST that can genuinely handle it well.\n\n" +
            "Routes:\n" + menu + "\n" +
            "Request:\n" + request + "\n\n" +
            "Respond with ONLY a JSON object: {\"route\":\"<one of the names>\",\"reasoning\":\"<one short sentence>\"}"));

        string raw;
        try
        {
            var resp = await _classifier.CompleteAsync(
                "You are a precise request-routing classifier. Output only the requested JSON.",
                convo, Array.Empty<ITool>(), _ => { }, ct).ConfigureAwait(false);
            raw = string.Concat(resp.AssistantMessage.Content.OfType<TextBlock>().Select(t => t.Text)).Trim();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return (_table.Default, $"(fallback: classifier call failed: {ex.Message})");
        }

        if (TryParse(raw, out var route, out var reason)) return (route!, reason);
        return (_table.Default, "(fallback: unparseable classifier output — using the default route)");
    }

    private bool TryParse(string raw, out RouteSpec? route, out string reasoning)
    {
        route = null; reasoning = "";
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            try
            {
                using var doc = JsonDocument.Parse(raw[start..(end + 1)]);
                var name = doc.RootElement.TryGetProperty("route", out var r) ? r.GetString() : null;
                reasoning = doc.RootElement.TryGetProperty("reasoning", out var re) ? re.GetString() ?? "" : "";
                if (name is not null && _table.Find(name) is { } found) { route = found; return true; }
            }
            catch { /* fall through to the prose scan */ }
        }

        // Prose rescue: accept only an UNambiguous single route name on a word boundary.
        var named = _table.Routes
            .Where(r => Regex.IsMatch(raw, $@"\b{Regex.Escape(r.Name)}\b", RegexOptions.IgnoreCase))
            .ToList();
        if (named.Count == 1) { route = named[0]; reasoning = "(parsed route from prose)"; return true; }
        return false;
    }
}
