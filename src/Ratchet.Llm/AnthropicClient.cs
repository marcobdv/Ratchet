using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodeStack.Ratchet.Core;

namespace CodeStack.Ratchet.Llm;

/// <summary>
/// A hand-rolled Anthropic Messages API client — no SDK. The point of v0 is to
/// see the wire format, so this builds the request JSON by hand and parses the
/// response content blocks by hand. Swapping providers later means writing a
/// sibling of this class behind the same ILlmClient seam.
///
/// Endpoint:    POST https://api.anthropic.com/v1/messages
/// Auth:        x-api-key header + anthropic-version header
/// Loop signal: response.stop_reason == "tool_use"
/// </summary>
public sealed class AnthropicClient : ILlmClient, IDisposable
{
    private const string Endpoint = "https://api.anthropic.com/v1/messages";
    private const string ApiVersion = "2023-06-01";

    private readonly HttpClient _http;
    private readonly string _model;
    private readonly int _maxTokens;

    public AnthropicClient(string apiKey, string model, int maxTokens = 4096)
    {
        _model = model;
        _maxTokens = maxTokens;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _http.DefaultRequestHeaders.Add("anthropic-version", ApiVersion);
    }

    public async Task<LlmResponse> CompleteAsync(
        string systemPrompt,
        Conversation conversation,
        IReadOnlyCollection<ITool> tools,
        CancellationToken ct)
    {
        var requestJson = BuildRequestJson(systemPrompt, conversation, tools);
        using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(Endpoint, content, ct);

        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Anthropic API {(int)resp.StatusCode}: {body}");

        return ParseResponse(body);
    }

    // ---- request building -------------------------------------------------

    private string BuildRequestJson(
        string systemPrompt,
        Conversation conversation,
        IReadOnlyCollection<ITool> tools)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var w = new Utf8JsonWriter(buffer);

        w.WriteStartObject();
        w.WriteString("model", _model);
        w.WriteNumber("max_tokens", _maxTokens);
        w.WriteString("system", systemPrompt);

        WriteTools(w, tools);
        WriteMessages(w, conversation);

        w.WriteEndObject();
        w.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteTools(Utf8JsonWriter w, IReadOnlyCollection<ITool> tools)
    {
        w.WriteStartArray("tools");
        foreach (var tool in tools)
        {
            w.WriteStartObject();
            w.WriteString("name", tool.Name);
            w.WriteString("description", tool.Description);
            w.WritePropertyName("input_schema");
            using var schema = JsonDocument.Parse(tool.InputSchemaJson);
            schema.RootElement.WriteTo(w);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteMessages(Utf8JsonWriter w, Conversation conversation)
    {
        w.WriteStartArray("messages");
        foreach (var msg in conversation.Messages)
        {
            w.WriteStartObject();
            w.WriteString("role", msg.Role == Role.User ? "user" : "assistant");
            w.WriteStartArray("content");
            foreach (var block in msg.Content)
                WriteContentBlock(w, block);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteContentBlock(Utf8JsonWriter w, ContentBlock block)
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
                if (r.IsError) w.WriteBoolean("is_error", true);
                w.WriteEndObject();
                break;
        }
    }

    // ---- response parsing -------------------------------------------------

    private static LlmResponse ParseResponse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var blocks = new List<ContentBlock>();
        foreach (var item in root.GetProperty("content").EnumerateArray())
        {
            var type = item.GetProperty("type").GetString();
            switch (type)
            {
                case "text":
                    blocks.Add(new TextBlock(item.GetProperty("text").GetString() ?? ""));
                    break;

                case "tool_use":
                    blocks.Add(new ToolUseBlock(
                        item.GetProperty("id").GetString()!,
                        item.GetProperty("name").GetString()!,
                        item.GetProperty("input").GetRawText()));
                    break;
            }
        }

        var stopReason = root.GetProperty("stop_reason").GetString() ?? "end_turn";
        var usage = root.GetProperty("usage");
        var inTokens = usage.GetProperty("input_tokens").GetInt32();
        var outTokens = usage.GetProperty("output_tokens").GetInt32();

        return new LlmResponse(new Message(Role.Assistant, blocks), stopReason, inTokens, outTokens);
    }

    public void Dispose() => _http.Dispose();
}
