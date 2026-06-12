using System.Buffers;
using System.Text.Json;

namespace CodeStack.Ratchet.Core;

/// <summary>Lightweight metadata for listing saved sessions.</summary>
public sealed record SessionInfo(string Id, DateTime UpdatedUtc, int MessageCount, string Preview);

/// <summary>
/// Persists conversations so a session can be resumed later. The transcript is
/// the agent's whole memory, so "save a session" is just "serialize the message
/// list" — there is no other hidden state to capture.
/// </summary>
public interface ISessionStore
{
    /// <summary>Write the conversation. Pass null id to mint a new one; returns the id used.</summary>
    string Save(string? id, Conversation conversation);

    /// <summary>Load a session by id, or null if it doesn't exist.</summary>
    Conversation? Load(string id);

    /// <summary>All saved sessions, most-recently-updated first.</summary>
    IReadOnlyList<SessionInfo> List();
}

/// <summary>
/// One JSON file per session under {baseDir}/.ratchet/sessions/. The on-disk
/// shape mirrors the Messages API wire format (role + typed content blocks), so
/// a saved session reads the same way the request body does — nothing new to learn.
/// </summary>
public sealed class FileSessionStore : ISessionStore
{
    private readonly string _dir;

    public FileSessionStore(string baseDir)
    {
        _dir = Path.Combine(baseDir, ".ratchet", "sessions");
        Directory.CreateDirectory(_dir);
    }

    public string Save(string? id, Conversation conversation)
    {
        id ??= DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var path = Path.Combine(_dir, id + ".json");

        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("id", id);
            w.WriteString("updatedUtc", DateTime.UtcNow);
            w.WriteStartArray("messages");
            foreach (var msg in conversation.Messages)
                WriteMessage(w, msg);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        File.WriteAllBytes(path, buffer.WrittenSpan.ToArray());
        return id;
    }

    public Conversation? Load(string id)
    {
        var path = Path.Combine(_dir, id + ".json");
        if (!File.Exists(path)) return null;

        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var conversation = new Conversation();
        foreach (var m in doc.RootElement.GetProperty("messages").EnumerateArray())
            conversation.Add(ReadMessage(m));
        return conversation;
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
                var messages = root.GetProperty("messages");
                infos.Add(new SessionInfo(id, updated, messages.GetArrayLength(), FirstUserText(messages)));
            }
            catch
            {
                // A malformed session file shouldn't break the listing — skip it.
            }
        }
        infos.Sort((a, b) => b.UpdatedUtc.CompareTo(a.UpdatedUtc));
        return infos;
    }

    // ---- per-message JSON (same shape as the API wire format) -------------

    private static void WriteMessage(Utf8JsonWriter w, Message msg)
    {
        w.WriteStartObject();
        w.WriteString("role", msg.Role == Role.User ? "user" : "assistant");
        w.WriteStartArray("content");
        foreach (var block in msg.Content)
            WriteBlock(w, block);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteBlock(Utf8JsonWriter w, ContentBlock block)
    {
        switch (block)
        {
            case TextBlock t:
                w.WriteStartObject();
                w.WriteString("type", "text");
                w.WriteString("text", t.Text);
                w.WriteEndObject();
                break;

            case ToolUseBlock u:
                w.WriteStartObject();
                w.WriteString("type", "tool_use");
                w.WriteString("id", u.Id);
                w.WriteString("name", u.Name);
                w.WritePropertyName("input");
                using (var input = JsonDocument.Parse(u.InputJson))
                    input.RootElement.WriteTo(w);
                w.WriteEndObject();
                break;

            case ToolResultBlock r:
                w.WriteStartObject();
                w.WriteString("type", "tool_result");
                w.WriteString("tool_use_id", r.ToolUseId);
                w.WriteString("content", r.Content);
                w.WriteBoolean("is_error", r.IsError);
                w.WriteEndObject();
                break;
        }
    }

    private static Message ReadMessage(JsonElement m)
    {
        var role = m.GetProperty("role").GetString() == "assistant" ? Role.Assistant : Role.User;
        var blocks = new List<ContentBlock>();
        foreach (var b in m.GetProperty("content").EnumerateArray())
        {
            switch (b.GetProperty("type").GetString())
            {
                case "text":
                    blocks.Add(new TextBlock(b.GetProperty("text").GetString() ?? ""));
                    break;
                case "tool_use":
                    blocks.Add(new ToolUseBlock(
                        b.GetProperty("id").GetString()!,
                        b.GetProperty("name").GetString()!,
                        b.GetProperty("input").GetRawText()));
                    break;
                case "tool_result":
                    blocks.Add(new ToolResultBlock(
                        b.GetProperty("tool_use_id").GetString()!,
                        b.GetProperty("content").GetString() ?? "",
                        b.TryGetProperty("is_error", out var e) && e.GetBoolean()));
                    break;
            }
        }
        return new Message(role, blocks);
    }

    private static string FirstUserText(JsonElement messages)
    {
        foreach (var m in messages.EnumerateArray())
        {
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
