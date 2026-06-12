using System.Buffers;
using System.Text.Json;

namespace CodeStack.Ratchet.Core;

/// <summary>Lightweight metadata for listing saved sessions.</summary>
public sealed record SessionInfo(string Id, DateTime UpdatedUtc, int MessageCount, string Preview);

/// <summary>
/// A conversation as a TREE of message nodes rather than a flat list. Each node
/// has a parent; HEAD points at the current leaf. The path root..HEAD is the
/// live conversation the model sees. Rewinding HEAD and continuing creates a
/// branch — the old descendants stay in the tree, so nothing is ever lost. This
/// is the same HEAD-over-a-DAG idea that makes git (and pi's sessions) work.
/// </summary>
public sealed class SessionTree
{
    public sealed record Node(string Id, string? ParentId, Message Message);

    private readonly Dictionary<string, Node> _nodes = new();
    private int _counter;

    public string? HeadId { get; private set; }
    public int Count => _nodes.Count;
    public IReadOnlyDictionary<string, Node> Nodes => _nodes;

    /// <summary>Append a message as a child of HEAD and advance HEAD to it.</summary>
    public string Append(Message message)
    {
        var id = (++_counter).ToString();
        _nodes[id] = new Node(id, HeadId, message);
        HeadId = id;
        return id;
    }

    /// <summary>
    /// Move HEAD back n whole turns. A "turn" boundary is a human prompt (a
    /// user message containing text — not a tool-result). Landing on a boundary
    /// guarantees the path is valid to continue from; raw per-message rewind
    /// could strand an assistant tool_use with no matching tool_result.
    /// </summary>
    public void RewindTurns(int n)
    {
        var removed = 0;
        for (var cur = HeadId; cur is not null; cur = _nodes[cur].ParentId)
        {
            if (!IsHumanPrompt(_nodes[cur].Message)) continue;
            if (++removed == n) { HeadId = _nodes[cur].ParentId; return; }
        }
        HeadId = null; // fewer than n prompts — rewind to empty (a fresh root)
    }

    /// <summary>Point HEAD at an explicit node. Returns false if it doesn't exist.</summary>
    public bool Goto(string nodeId)
    {
        if (!_nodes.ContainsKey(nodeId)) return false;
        HeadId = nodeId;
        return true;
    }

    /// <summary>The messages from root down to HEAD — the live conversation.</summary>
    public Conversation MaterializeConversation()
    {
        var path = new List<Message>();
        for (var id = HeadId; id is not null; id = _nodes[id].ParentId)
            path.Add(_nodes[id].Message);
        path.Reverse();

        var c = new Conversation();
        foreach (var m in path) c.Add(m);
        return c;
    }

    /// <summary>Children of a node (null = roots), ordered by id, for printing.</summary>
    public IReadOnlyList<Node> ChildrenOf(string? parentId) =>
        _nodes.Values
              .Where(n => n.ParentId == parentId)
              .OrderBy(n => int.TryParse(n.Id, out var v) ? v : 0)
              .ToList();

    /// <summary>Rebuild a tree from persisted nodes, continuing the id counter.</summary>
    public static SessionTree FromNodes(IEnumerable<Node> nodes, string? head)
    {
        var t = new SessionTree();
        var max = 0;
        foreach (var n in nodes)
        {
            t._nodes[n.Id] = n;
            if (int.TryParse(n.Id, out var v) && v > max) max = v;
        }
        t._counter = max;
        t.HeadId = head;
        return t;
    }

    private static bool IsHumanPrompt(Message m) =>
        m.Role == Role.User && m.Content.Any(b => b is TextBlock);
}

/// <summary>
/// Persists session trees so they can be resumed and branched later. The on-disk
/// shape stores nodes (id + parent + message) plus the HEAD pointer.
/// </summary>
public interface ISessionStore
{
    /// <summary>Write the tree. Pass null id to mint a new one; returns the id used.</summary>
    string Save(string? id, SessionTree tree);

    /// <summary>Load a session by id, or null if it doesn't exist.</summary>
    SessionTree? Load(string id);

    /// <summary>All saved sessions, most-recently-updated first.</summary>
    IReadOnlyList<SessionInfo> List();
}

/// <summary>
/// One JSON file per session under {baseDir}/.ratchet/sessions/. Each message
/// is stored in the same shape as the Messages API wire format, wrapped in a
/// node with id + parent. Older v0.2 flat-list files still load (imported as a
/// linear chain), so existing sessions aren't lost.
/// </summary>
public sealed class FileSessionStore : ISessionStore
{
    private readonly string _dir;

    public FileSessionStore(string baseDir)
    {
        _dir = Path.Combine(baseDir, ".ratchet", "sessions");
        Directory.CreateDirectory(_dir);
    }

    public string Save(string? id, SessionTree tree)
    {
        id ??= DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var path = Path.Combine(_dir, id + ".json");

        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("id", id);
            w.WriteString("updatedUtc", DateTime.UtcNow);
            if (tree.HeadId is null) w.WriteNull("head"); else w.WriteString("head", tree.HeadId);

            w.WriteStartArray("nodes");
            foreach (var node in tree.Nodes.Values)
            {
                w.WriteStartObject();
                w.WriteString("id", node.Id);
                if (node.ParentId is null) w.WriteNull("parent"); else w.WriteString("parent", node.ParentId);
                w.WritePropertyName("message");
                MessageJson.Write(w, node.Message);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        File.WriteAllBytes(path, buffer.WrittenSpan.ToArray());
        return id;
    }

    public SessionTree? Load(string id)
    {
        var path = Path.Combine(_dir, id + ".json");
        if (!File.Exists(path)) return null;

        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = doc.RootElement;

        // Tree format (v0.3+).
        if (root.TryGetProperty("nodes", out var nodesEl))
        {
            var nodes = new List<SessionTree.Node>();
            foreach (var n in nodesEl.EnumerateArray())
            {
                var nid = n.GetProperty("id").GetString()!;
                var parent = n.TryGetProperty("parent", out var p) && p.ValueKind == JsonValueKind.String
                    ? p.GetString() : null;
                nodes.Add(new SessionTree.Node(nid, parent, MessageJson.Read(n.GetProperty("message"))));
            }
            var head = root.TryGetProperty("head", out var h) && h.ValueKind == JsonValueKind.String
                ? h.GetString() : null;
            return SessionTree.FromNodes(nodes, head);
        }

        // Backward-compat: v0.2 flat "messages" -> import as a linear chain.
        if (root.TryGetProperty("messages", out var msgs))
        {
            var tree = new SessionTree();
            foreach (var m in msgs.EnumerateArray())
                tree.Append(MessageJson.Read(m));
            return tree;
        }

        return new SessionTree();
    }

    public IReadOnlyList<SessionInfo> List()
    {
        var infos = new List<SessionInfo>();
        foreach (var path in Directory.EnumerateFiles(_dir, "*.json"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
                var root = doc.RootElement;
                var id = root.GetProperty("id").GetString() ?? Path.GetFileNameWithoutExtension(path);
                var updated = root.TryGetProperty("updatedUtc", out var u)
                    ? u.GetDateTime() : File.GetLastWriteTimeUtc(path);

                int count;
                string preview;
                if (root.TryGetProperty("nodes", out var nodes))
                {
                    count = nodes.GetArrayLength();
                    preview = FirstUserText(nodes, nodeWrapped: true);
                }
                else if (root.TryGetProperty("messages", out var msgs))
                {
                    count = msgs.GetArrayLength();
                    preview = FirstUserText(msgs, nodeWrapped: false);
                }
                else { count = 0; preview = "(empty)"; }

                infos.Add(new SessionInfo(id, updated, count, preview));
            }
            catch
            {
                // A malformed session file shouldn't break the listing — skip it.
            }
        }
        infos.Sort((a, b) => b.UpdatedUtc.CompareTo(a.UpdatedUtc));
        return infos;
    }

    // Message <-> JSON lives in MessageJson (shared with the SQLite store).

    /// <summary>First human prompt text, for the session list preview.</summary>
    private static string FirstUserText(JsonElement items, bool nodeWrapped)
    {
        foreach (var item in items.EnumerateArray())
        {
            var m = nodeWrapped ? item.GetProperty("message") : item;
            if (m.GetProperty("role").GetString() != "user") continue;
            foreach (var b in m.GetProperty("content").EnumerateArray())
            {
                if (b.GetProperty("type").GetString() != "text") continue;
                var s = b.GetProperty("text").GetString() ?? "";
                return s.Length > 60 ? s[..60] + "…" : s;
            }
        }
        return "(no prompt)";
    }
}
