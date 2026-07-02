using CodeStack.Ratchet.Core;
using CodeStack.Ratchet.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CodeStack.Ratchet.Tests;

/// <summary>
/// The SQLite store's index-consistency guarantees: FTS never desyncs from nodes
/// (no duplicate hits, no permanently-unindexed rows), LIKE wildcards match
/// literally, and one connection survives concurrent use.
/// </summary>
public sealed class SqliteSessionStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ratchet-sqlite-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteSessionStore _store;

    public SqliteSessionStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new SqliteSessionStore(_dir);
    }

    public void Dispose()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string DbPath => Path.Combine(_dir, ".ratchet", "ratchet.db");

    private static SessionTree TreeWith(params string[] userTexts)
    {
        var t = new SessionTree();
        foreach (var text in userTexts)
        {
            t.Append(Message.UserText(text));
            t.Append(new Message(Role.Assistant, new ContentBlock[] { new TextBlock("answer to: " + text) }));
        }
        return t;
    }

    [Fact]
    public void SaveLoad_Roundtrips_IncludingThinkingBlocks()
    {
        var t = new SessionTree();
        t.Append(Message.UserText("prompt"));
        t.Append(new Message(Role.Assistant, new ContentBlock[]
        {
            new ThinkingBlock("reasoning", "sig=="),
            new ToolUseBlock("t1", "read", """{"path":"x"}"""),
        }));
        t.Append(Message.UserToolResults(new ContentBlock[] { new ToolResultBlock("t1", "data", false) }));

        var id = _store.Save(null, t);
        var loaded = _store.Load(id);

        Assert.NotNull(loaded);
        Assert.Equal(t.Count, loaded!.Count);
        Assert.Equal(t.HeadId, loaded.HeadId);
        var thinking = Assert.IsType<ThinkingBlock>(
            loaded.Nodes["2"].Message.Content[0]);
        Assert.Equal("sig==", thinking.Signature);
    }

    [Fact]
    public void RepeatedSaves_DoNotDuplicateSearchHits()
    {
        var t = TreeWith("the xylophone incident");
        var id = _store.Save(null, t);
        _store.Save(id, t);   // same tree again — used to double every FTS row
        _store.Save(id, t);

        var hits = _store.SearchText(id, "xylophone", 10);
        Assert.Single(hits.Where(h => h.Role == Role.User));
    }

    [Fact]
    public void PreFtsRows_AreBackfilled_EvenWhenTheIndexIsNotEmpty()
    {
        // Simulate the upgrade trap: a session whose older nodes were written before
        // the FTS table existed (delete their index rows), then ONE new save makes
        // the index non-empty — the old empty-only backfill never indexed the rest.
        var t = TreeWith("ancient artifact");
        var id = _store.Save(null, t);

        using (var raw = new SqliteConnection($"Data Source={DbPath}"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "DELETE FROM nodes_fts WHERE session_id = $s;";
            cmd.Parameters.AddWithValue("$s", id);
            cmd.ExecuteNonQuery();
        }

        t.Append(Message.UserText("fresh addition"));
        _store.Save(id, t);   // index now non-empty (the new node only)

        var oldHits = _store.SearchText(id, "ancient", 10);   // triggers gap backfill
        Assert.NotEmpty(oldHits);
        var newHits = _store.SearchText(id, "fresh", 10);
        Assert.NotEmpty(newHits);

        // And the backfill didn't duplicate the already-indexed new node.
        Assert.Single(newHits.Where(h => h.Role == Role.User));
    }

    [Fact]
    public void LikeFallback_TreatsWildcardsLiterally()
    {
        var t = TreeWith("progress is 50% done", "no percent sign here");
        var id = _store.Save(null, t);

        // "%" has no letters/digits, so it goes straight to the LIKE scan; unescaped
        // it would match every row instead of only the literal percent sign.
        var hits = _store.SearchText(id, "%", 10);
        Assert.All(hits, h => Assert.Contains("%", h.Snippet));
        Assert.Contains(hits, h => h.Snippet.Contains("50%"));
    }

    [Fact]
    public async Task ConcurrentSavesAndSearches_DoNotCorruptTheSharedConnection()
    {
        var t = TreeWith("concurrency probe");
        var id = _store.Save(null, t);

        var tasks = Enumerable.Range(0, 16).Select(i => Task.Run(() =>
        {
            if (i % 2 == 0)
            {
                var tree = _store.Load(id)!;
                tree.Append(Message.UserText($"turn {i}"));
                _store.Save(id, tree);
            }
            else
            {
                _store.SearchText(id, "probe", 5);
                _store.List();
            }
        }));

        await Task.WhenAll(tasks); // used to throw "connection is busy" / corrupt command state
        Assert.NotNull(_store.Load(id));
    }

    [Fact]
    public void SchemaVersion_IsStamped()
    {
        using var raw = new SqliteConnection($"Data Source={DbPath}");
        raw.Open();
        using var cmd = raw.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        Assert.Equal(1L, Convert.ToInt64(cmd.ExecuteScalar()));
    }
}
