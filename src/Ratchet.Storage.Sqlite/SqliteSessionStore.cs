using System.Globalization;
using CodeStack.Ratchet.Core;
using Microsoft.Data.Sqlite;

namespace CodeStack.Ratchet.Storage.Sqlite;

/// <summary>
/// SQLite-backed <see cref="ISessionStore"/>. One database file
/// (.ratchet/ratchet.db) holds every session as a flat <c>nodes</c> table with a
/// <c>parent_id</c> — the tree lives in the relationships, not the storage engine.
///
/// Nodes are append-only and immutable (branching only ever *adds* nodes), so a
/// turn inserts just the new rows and updates the session's HEAD — no full-file
/// rewrite, which is the win over the JSON-file store. Recursive CTEs walk the
/// parent chain when you want the database to compute a path for you.
/// </summary>
public sealed class SqliteSessionStore : ISessionStore, IDisposable
{
    private readonly SqliteConnection _conn;

    public SqliteSessionStore(string baseDir)
    {
        var dir = Path.Combine(baseDir, ".ratchet");
        Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={Path.Combine(dir, "ratchet.db")}");
        _conn.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Exec("""
            CREATE TABLE IF NOT EXISTS sessions (
                id          TEXT PRIMARY KEY,
                head_id     TEXT,
                updated_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS nodes (
                session_id   TEXT NOT NULL,
                id           TEXT NOT NULL,
                parent_id    TEXT,
                message_json TEXT NOT NULL,
                role         TEXT NOT NULL,
                text_excerpt TEXT,
                PRIMARY KEY (session_id, id)
            );
            CREATE INDEX IF NOT EXISTS ix_nodes_parent ON nodes(session_id, parent_id);
            """);
    }

    public string Save(string? id, SessionTree tree)
    {
        id ??= DateTime.Now.ToString("yyyyMMdd-HHmmss");

        // Which nodes are already persisted? They're immutable, so we insert only
        // the ones we haven't seen — reading a few ids is far cheaper than
        // re-serialising every message every turn.
        var existing = new HashSet<string>(StringComparer.Ordinal);
        using (var sel = _conn.CreateCommand())
        {
            sel.CommandText = "SELECT id FROM nodes WHERE session_id = $s";
            sel.Parameters.AddWithValue("$s", id);
            using var r = sel.ExecuteReader();
            while (r.Read()) existing.Add(r.GetString(0));
        }

        using var tx = _conn.BeginTransaction();

        using (var up = _conn.CreateCommand())
        {
            up.Transaction = tx;
            up.CommandText = """
                INSERT INTO sessions (id, head_id, updated_utc) VALUES ($id, $head, $upd)
                ON CONFLICT(id) DO UPDATE SET head_id = excluded.head_id, updated_utc = excluded.updated_utc;
                """;
            up.Parameters.AddWithValue("$id", id);
            up.Parameters.AddWithValue("$head", (object?)tree.HeadId ?? DBNull.Value);
            up.Parameters.AddWithValue("$upd", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            up.ExecuteNonQuery();
        }

        using (var ins = _conn.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT OR IGNORE INTO nodes (session_id, id, parent_id, message_json, role, text_excerpt)
                VALUES ($s, $id, $p, $m, $role, $ex);
                """;
            var pS = ins.Parameters.Add("$s", SqliteType.Text);
            var pId = ins.Parameters.Add("$id", SqliteType.Text);
            var pP = ins.Parameters.Add("$p", SqliteType.Text);
            var pM = ins.Parameters.Add("$m", SqliteType.Text);
            var pRole = ins.Parameters.Add("$role", SqliteType.Text);
            var pEx = ins.Parameters.Add("$ex", SqliteType.Text);

            foreach (var node in tree.Nodes.Values)
            {
                if (existing.Contains(node.Id)) continue;
                pS.Value = id;
                pId.Value = node.Id;
                pP.Value = (object?)node.ParentId ?? DBNull.Value;
                pM.Value = MessageJson.Serialize(node.Message);
                pRole.Value = node.Message.Role == Role.User ? "user" : "assistant";
                pEx.Value = (object?)Excerpt(node.Message) ?? DBNull.Value;
                ins.ExecuteNonQuery();
            }
        }

        tx.Commit();
        return id;
    }

    public SessionTree? Load(string id)
    {
        string? head;
        using (var h = _conn.CreateCommand())
        {
            h.CommandText = "SELECT head_id FROM sessions WHERE id = $id";
            h.Parameters.AddWithValue("$id", id);
            using var r = h.ExecuteReader();
            if (!r.Read()) return null;                       // no such session
            head = r.IsDBNull(0) ? null : r.GetString(0);
        }

        // The whole tree is needed (every branch), so this is a plain WHERE, not
        // a recursive walk — the in-memory SessionTree handles path-finding.
        var nodes = new List<SessionTree.Node>();
        using (var q = _conn.CreateCommand())
        {
            q.CommandText = "SELECT id, parent_id, message_json FROM nodes WHERE session_id = $id";
            q.Parameters.AddWithValue("$id", id);
            using var r = q.ExecuteReader();
            while (r.Read())
                nodes.Add(new SessionTree.Node(
                    r.GetString(0),
                    r.IsDBNull(1) ? null : r.GetString(1),
                    MessageJson.Deserialize(r.GetString(2))));
        }

        return SessionTree.FromNodes(nodes, head);
    }

    public IReadOnlyList<SessionInfo> List()
    {
        var infos = new List<SessionInfo>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.id, s.updated_utc,
                   (SELECT COUNT(*) FROM nodes n WHERE n.session_id = s.id),
                   (SELECT n2.text_excerpt FROM nodes n2
                      WHERE n2.session_id = s.id AND n2.role = 'user' AND n2.text_excerpt IS NOT NULL
                      ORDER BY CAST(n2.id AS INTEGER) LIMIT 1)
            FROM sessions s
            ORDER BY s.updated_utc DESC;
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var updated = DateTime.Parse(r.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            infos.Add(new SessionInfo(
                r.GetString(0),
                updated,
                r.GetInt32(2),
                r.IsDBNull(3) ? "(no prompt)" : r.GetString(3)));
        }
        return infos;
    }

    /// <summary>
    /// BONUS (not part of ISessionStore): materialise just the active path
    /// root..HEAD with a recursive CTE — the database walks the parent chain
    /// rather than loading every branch into memory. This is the version you'd
    /// reach for on a huge session where loading the whole tree is wasteful.
    /// </summary>
    public Conversation MaterializeActivePath(string id)
    {
        string? head;
        using (var h = _conn.CreateCommand())
        {
            h.CommandText = "SELECT head_id FROM sessions WHERE id = $id";
            h.Parameters.AddWithValue("$id", id);
            using var r = h.ExecuteReader();
            head = r.Read() && !r.IsDBNull(0) ? r.GetString(0) : null;
        }

        var conversation = new Conversation();
        if (head is null) return conversation;

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            WITH RECURSIVE path(id, parent_id, message_json, depth) AS (
                SELECT id, parent_id, message_json, 0
                FROM nodes WHERE session_id = $s AND id = $head
                UNION ALL
                SELECT n.id, n.parent_id, n.message_json, p.depth + 1
                FROM nodes n JOIN path p ON n.id = p.parent_id AND n.session_id = $s
            )
            SELECT message_json FROM path ORDER BY depth DESC;  -- root first, HEAD last
            """;
        cmd.Parameters.AddWithValue("$s", id);
        cmd.Parameters.AddWithValue("$head", head);
        using var rr = cmd.ExecuteReader();
        while (rr.Read())
            conversation.Add(MessageJson.Deserialize(rr.GetString(0)));
        return conversation;
    }

    private static string? Excerpt(Message m)
    {
        foreach (var b in m.Content)
            if (b is TextBlock t && !string.IsNullOrWhiteSpace(t.Text))
            {
                var s = t.Text.ReplaceLineEndings(" ");
                return s.Length > 60 ? s[..60] + "…" : s;
            }
        return null;
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();
}
