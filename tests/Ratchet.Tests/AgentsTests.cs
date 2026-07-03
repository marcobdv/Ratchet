using CodeStack.Ratchet.Core;
using CodeStack.Ratchet.Tests.Support;
using Xunit;

namespace CodeStack.Ratchet.Tests;

/// <summary>
/// Tier 1 of the delegation family: loading Claude-Code-style agent definitions and
/// turning them into named delegate tools.
/// </summary>
public sealed class AgentCatalogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ratchet-agents-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private AgentCatalog Discover(string fileName, string content, string root = ".claude")
    {
        var agentsDir = Path.Combine(_dir, root, "agents");
        Directory.CreateDirectory(agentsDir);
        File.WriteAllText(Path.Combine(agentsDir, fileName), content);
        return AgentCatalog.Discover(_dir);
    }

    [Fact]
    public void ParsesClaudeAgentFile_Frontmatter_AndPromptBody()
    {
        var cat = Discover("code-reviewer.md", """
            ---
            name: code-reviewer
            description: Reviews a diff for correctness and convention.
            tools: read, search, git_diff
            model: opus
            ---
            You are a meticulous code reviewer. Check the diff and report findings.
            """);

        var def = cat.Find("code-reviewer");
        Assert.NotNull(def);
        Assert.Equal("code-reviewer", def!.Name);
        Assert.Equal("Reviews a diff for correctness and convention.", def.Description);
        Assert.Equal(new[] { "read", "search", "git_diff" }, def.Tools);
        Assert.Equal("opus", def.Model);
        Assert.StartsWith("You are a meticulous code reviewer", def.SystemPrompt);
    }

    [Fact]
    public void ToolsAsYamlList_AlsoParse()
    {
        var cat = Discover("a.md", """
            ---
            name: a
            description: d
            tools:
              - read
              - search
            ---
            body
            """);
        Assert.Equal(new[] { "read", "search" }, cat.Find("a")!.Tools);
    }

    [Fact]
    public void ModelInherit_OrAbsent_IsNull()
    {
        var inherit = Discover("i.md", "---\nname: i\ndescription: d\nmodel: inherit\n---\nbody").Find("i");
        Assert.Null(inherit!.Model);

        var absent = Discover("j.md", "---\nname: j\ndescription: d\n---\nbody").Find("j");
        Assert.Null(absent!.Model);
        Assert.Null(absent.Tools);   // no tools → default set applied at build time
    }

    [Fact]
    public void MissingName_FallsBackToTheFileName()
    {
        var cat = Discover("my-helper.md", "---\ndescription: d\n---\nbody");
        Assert.NotNull(cat.Find("my-helper"));
    }

    [Fact]
    public void ProviderField_SelectsAgentsOwnBackend()
    {
        var def = Discover("local-arch.md", """
            ---
            name: local-arch
            description: d
            provider: local
            model: qwen2.5-coder:14b
            ---
            body
            """).Find("local-arch");
        Assert.Equal("local", def!.Provider);
        Assert.Equal("qwen2.5-coder:14b", def.Model);
    }

    [Fact]
    public void ProviderColonModelPrefix_SplitsOnTheFirstColon()
    {
        // OpenRouter ids contain '/', so the first ':' is the provider/model boundary.
        var def = Discover("or.md", """
            ---
            name: or
            description: d
            model: openrouter:anthropic/claude-sonnet-4
            ---
            body
            """).Find("or");
        Assert.Equal("openrouter", def!.Provider);
        Assert.Equal("anthropic/claude-sonnet-4", def.Model);
    }

    [Fact]
    public void FileWithNoBody_IsNotAUsableAgent()
    {
        var cat = Discover("empty.md", "---\nname: empty\ndescription: d\n---\n");
        Assert.Null(cat.Find("empty"));
    }
}

public sealed class AgentBuilderTests
{
    private static ITool? Resolve(string name) => name switch
    {
        "read" => new RecordingTool("read", _ => "file contents"),
        "search" => new RecordingTool("search"),
        "bash" => new RecordingTool("bash"),
        _ => null,
    };

    private static AgentCatalog CatalogOf(params AgentDefinition[] defs)
    {
        var cat = new AgentCatalog();
        foreach (var d in defs) cat.Add(d);
        return cat;
    }

    [Fact]
    public async Task BuildsNamedDelegate_ThatRunsWithItsToolSubset()
    {
        var def = new AgentDefinition("reviewer", "Reviews code.", new[] { "read" }, null, "You review.");
        var cat = CatalogOf(def);

        var innerLlm = new ScriptedLlmClient()
            .Enqueue(ScriptedLlmClient.ToolCall("t1", "read", """{"path":"x"}"""))
            .Enqueue(ScriptedLlmClient.Text("Looks good."));

        var tools = SubAgents.BuildFromCatalog(
            cat, Resolve, (_, _) => innerLlm, innerLlm, AllowAllGate.Instance,
            new HashSet<string>(StringComparer.Ordinal), Path.GetTempPath()).ToList();

        var reviewer = Assert.Single(tools);
        Assert.Equal("reviewer", reviewer.Name);
        Assert.Equal("Reviews code.", reviewer.Description);

        var result = await reviewer.ExecuteAsync("""{"task":"review the change"}""", CancellationToken.None);
        Assert.Equal("Looks good.", result);
    }

    [Fact]
    public void SkipsAnAgentWhoseNameCollidesWithAnExistingTool()
    {
        var def = new AgentDefinition("read", "shadows the read tool", null, null, "prompt");
        var cat = CatalogOf(def);
        var warnings = new List<string>();

        var tools = SubAgents.BuildFromCatalog(
            cat, Resolve, (_, _) => new ScriptedLlmClient(), new ScriptedLlmClient(), AllowAllGate.Instance,
            new HashSet<string>(StringComparer.Ordinal) { "read" }, Path.GetTempPath(), warnings.Add).ToList();

        Assert.Empty(tools);
        Assert.Contains(warnings, w => w.Contains("collides"));
    }

    [Fact]
    public void SanitizesToolNameToTheToolCharset()
    {
        Assert.Equal("my_agent", SubAgents.Sanitize("my agent"));
        Assert.Equal("a-b_c", SubAgents.Sanitize("a-b/c"));
    }
}
