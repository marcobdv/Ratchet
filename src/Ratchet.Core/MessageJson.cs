using System.Buffers;
using System.Text;
using System.Text.Json;

namespace CodeStack.Ratchet.Core;

/// <summary>
/// Converts a single <see cref="Message"/> to/from JSON in the Messages API wire
/// shape (role + typed content blocks). Shared by every session store so the
/// on-disk format is identical regardless of backend.
/// </summary>
public static class MessageJson
{
    public static string Serialize(Message message)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
            Write(w, message);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static Message Deserialize(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Read(doc.RootElement);
    }

    public static void Write(Utf8JsonWriter w, Message message)
    {
        w.WriteStartObject();
        w.WriteString("role", message.Role == Role.User ? "user" : "assistant");
        w.WriteStartArray("content");
        foreach (var block in message.Content)
            WriteBlock(w, block);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    public static Message Read(JsonElement m)
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
}
