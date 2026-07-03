using System.Text.Json;

/// <summary>
/// `ratchet --models`: query every CONFIGURED provider's model-list endpoint and print what
/// each actually offers, so you don't have to guess an id. A provider is "configured" when its
/// key (or, for local, a reachable server) is present — so two providers set at once (say a
/// local server and OpenRouter) list both catalogs together. All the OpenAI-compatible ones and
/// Anthropic return a <c>{ "data": [ { "id": … } ] }</c> shape; Ollama's native tags endpoint is
/// handled too.
/// </summary>
internal static class ModelCatalog
{
    private sealed record Source(string Name, string Url, (string Key, string Value)[] Headers, bool Silent);

    public static async Task<int> PrintAsync(string filter)
    {
        static string? Env(string n) => Environment.GetEnvironmentVariable(n);
        static string? First(params string[] names) => names.Select(Env).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        var sources = new List<Source>();
        void Add(string name, string url, bool configured, bool silent, params (string, string)[] headers)
        {
            if (configured) sources.Add(new Source(name, url, headers.Where(h => !string.IsNullOrEmpty(h.Item2)).ToArray(), silent));
        }

        Add("anthropic", "https://api.anthropic.com/v1/models", Env("ANTHROPIC_API_KEY") is { Length: > 0 }, silent: false,
            ("x-api-key", Env("ANTHROPIC_API_KEY") ?? ""), ("anthropic-version", "2023-06-01"));
        Add("openai", "https://api.openai.com/v1/models", Env("OPENAI_API_KEY") is { Length: > 0 }, silent: false,
            ("Authorization", "Bearer " + Env("OPENAI_API_KEY")));
        var orKey = First("OPENROUTER_API_KEY", "RATCHET_API_KEY");
        Add("openrouter", "https://openrouter.ai/api/v1/models", orKey is not null, silent: false,
            ("Authorization", "Bearer " + orKey));
        Add("groq", "https://api.groq.com/openai/v1/models", Env("GROQ_API_KEY") is { Length: > 0 }, silent: false,
            ("Authorization", "Bearer " + Env("GROQ_API_KEY")));

        // local: always attempt (default Ollama); skip silently if no server is running.
        var localBase = (Env("RATCHET_LOCAL_BASE_URL") ?? "http://localhost:11434/v1").TrimEnd('/');
        Add("local", localBase + "/models", configured: true, silent: true,
            ("Authorization", Env("RATCHET_LOCAL_API_KEY") is { Length: > 0 } lk ? "Bearer " + lk : ""));

        var customBase = Env("RATCHET_BASE_URL");
        Add("custom", (customBase ?? "").TrimEnd('/') + "/models", customBase is { Length: > 0 }, silent: false,
            ("Authorization", First("RATCHET_API_KEY") is { } ck ? "Bearer " + ck : ""));

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var any = false;
        foreach (var s in sources)
        {
            List<string> ids;
            try { ids = await FetchAsync(http, s); }
            catch (Exception ex)
            {
                if (!s.Silent) Console.WriteLine($"{s.Name}: could not list models ({ex.Message.Split('\n')[0]})");
                continue;
            }

            if (!string.IsNullOrEmpty(filter))
                ids = ids.Where(i => i.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
            ids.Sort(StringComparer.OrdinalIgnoreCase);
            if (ids.Count == 0) continue;

            any = true;
            Console.WriteLine($"\n{s.Name} ({ids.Count} model{(ids.Count == 1 ? "" : "s")}):");
            foreach (var id in ids) Console.WriteLine($"  {id}");
        }

        if (!any)
            Console.WriteLine(string.IsNullOrEmpty(filter)
                ? "No models found. Configure a provider (ANTHROPIC_API_KEY / OPENROUTER_API_KEY / OPENAI_API_KEY / GROQ_API_KEY, or start a local server)."
                : $"No models matching '{filter}' from the configured providers.");
        else
            Console.WriteLine("\nUse an id in RATCHET_MODEL, or in an agent's `model:` / `provider:` frontmatter.");
        return 0;
    }

    private static async Task<List<string>> FetchAsync(HttpClient http, Source s)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, s.Url);
        foreach (var (k, v) in s.Headers) req.Headers.TryAddWithoutValidation(k, v);
        using var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var ids = new List<string>();
        var root = doc.RootElement;
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var m in data.EnumerateArray())
                if (m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String) ids.Add(id.GetString()!);
        else if (root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)   // Ollama /api/tags
            foreach (var entry in models.EnumerateArray())
                if (entry.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String) ids.Add(nm.GetString()!);
        return ids;
    }
}
