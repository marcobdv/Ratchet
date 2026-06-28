using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace CodeStack.Ratchet.Llm;

/// <summary>
/// Ratchet's hand-rolled Anthropic Messages client, exposed as a
/// <see cref="IChatClient"/>. Same wire-level approach as <see cref="AnthropicClient"/>
/// (build the request JSON by hand, consume the SSE stream by hand) — but it speaks the
/// Microsoft.Extensions.AI types so it plugs into the standard <c>IChatClient</c> seam.
/// That's how Ratchet "adopts IChatClient" without giving up owning the wire: the loop now
/// talks to any provider through this abstraction, and MCP tools (which are AITools) drop in.
/// </summary>
public sealed class AnthropicChatClient : IChatClient
{
    private const string Endpoint = "https://api.anthropic.com/v1/messages";
    private const string ApiVersion = "2023-06-01";

    private readonly HttpClient _http;
    private readonly string _model;
    private readonly int _maxTokens;

    public AnthropicChatClient(string apiKey, string model, int maxTokens = 4096)
    {
        _model = model;
        _maxTokens = maxTokens;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _http.DefaultRequestHeaders.Add("anthropic-version", ApiVersion);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var requestJson = BuildRequestJson(messages, options);
        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json"),
        };

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"Anthropic API {(int)resp.StatusCode}: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        var toolBuilders = new SortedDictionary<int, ToolBuilder>();
        long inputTokens = 0, outputTokens = 0;
        var stopReason = "end_turn";

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            if (line.Length == 0 || !line.StartsWith("data:", StringComparison.Ordinal))
                continue;
            var payload = line.AsSpan(5).Trim();
            if (payload.IsEmpty)
                continue;

            using var doc = JsonDocument.Parse(payload.ToString());
            var root = doc.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "message_start":
                    if (root.TryGetProperty("message", out var m) &&
                        m.TryGetProperty("usage", out var u0))
                        // Include the cached portions so input accounting reflects billed input.
                        inputTokens = UsageInt(u0, "input_tokens")
                                    + UsageInt(u0, "cache_creation_input_tokens")
                                    + UsageInt(u0, "cache_read_input_tokens");
                    break;

                case "content_block_start":
                {
                    var idx = root.GetProperty("index").GetInt32();
                    var cb = root.GetProperty("content_block");
                    if (cb.GetProperty("type").GetString() == "tool_use")
                    {
                        toolBuilders[idx] = new ToolBuilder
                        {
                            Id = cb.GetProperty("id").GetString()!,
                            Name = cb.GetProperty("name").GetString()!,
                        };
                    }
                    break;
                }

                case "content_block_delta":
                {
                    var idx = root.GetProperty("index").GetInt32();
                    var delta = root.GetProperty("delta");
                    switch (delta.GetProperty("type").GetString())
                    {
                        case "text_delta":
                            var text = delta.GetProperty("text").GetString() ?? "";
                            // Live text fragment.
                            yield return new ChatResponseUpdate(ChatRole.Assistant, text);
                            break;
                        case "input_json_delta":
                            if (toolBuilders.TryGetValue(idx, out var tb))
                                tb.Json.Append(delta.GetProperty("partial_json").GetString() ?? "");
                            break;
                    }
                    break;
                }

                case "message_delta":
                    if (root.TryGetProperty("delta", out var md) &&
                        md.TryGetProperty("stop_reason", out var sr) &&
                        sr.ValueKind == JsonValueKind.String)
                        stopReason = sr.GetString()!;
                    if (root.TryGetProperty("usage", out var u1) &&
                        u1.TryGetProperty("output_tokens", out var ot))
                        outputTokens = ot.GetInt32();
                    break;

                case "error":
                    var msg = root.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var em)
                        ? em.GetString() : "unknown streaming error";
                    throw new InvalidOperationException($"Anthropic stream error: {msg}");
            }
        }

        // Final update: any tool calls, the finish reason, and usage.
        var finalContents = new List<AIContent>();
        foreach (var tb in toolBuilders.Values)
        {
            var args = tb.Json.Length == 0
                ? new Dictionary<string, object?>()
                : JsonSerializer.Deserialize<Dictionary<string, object?>>(tb.Json.ToString())
                  ?? new Dictionary<string, object?>();
            finalContents.Add(new FunctionCallContent(tb.Id, tb.Name, args));
        }
        finalContents.Add(new UsageContent(new UsageDetails
        {
            InputTokenCount = inputTokens,
            OutputTokenCount = outputTokens,
        }));

        yield return new ChatResponseUpdate(ChatRole.Assistant, finalContents)
        {
            FinishReason = MapFinishReason(stopReason, toolBuilders.Count > 0),
        };
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
            updates.Add(update);
        return updates.ToChatResponse();
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() => _http.Dispose();

    // ---- request building (M.E.AI messages -> Anthropic wire JSON) --------

    private string BuildRequestJson(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var messageList = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();

        // System prompt: any ChatRole.System messages plus options.Instructions.
        var system = new StringBuilder();
        if (!string.IsNullOrEmpty(options?.Instructions))
            system.Append(options.Instructions);
        foreach (var msg in messageList)
        {
            if (msg.Role == ChatRole.System)
            {
                if (system.Length > 0) system.Append("\n\n");
                system.Append(msg.Text);
            }
        }

        var buffer = new ArrayBufferWriter<byte>();
        using var w = new Utf8JsonWriter(buffer);
        w.WriteStartObject();
        w.WriteString("model", options?.ModelId ?? _model);
        w.WriteNumber("max_tokens", (int)(options?.MaxOutputTokens ?? _maxTokens));
        w.WriteBoolean("stream", true);

        // Cache the stable system prompt + tools, and the transcript tail — same breakpoint
        // strategy as AnthropicClient. Only emit the system array when non-empty: Anthropic
        // rejects an empty text content block (400), which would break tool-only requests.
        if (system.Length > 0)
        {
            w.WritePropertyName("system");
            w.WriteStartArray();
            w.WriteStartObject();
            w.WriteString("type", "text");
            w.WriteString("text", system.ToString());
            CacheControl.Write(w);
            w.WriteEndObject();
            w.WriteEndArray();
        }

        WriteTools(w, options?.Tools);
        WriteMessages(w, messageList);

        w.WriteEndObject();
        w.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteTools(Utf8JsonWriter w, IList<AITool>? tools)
    {
        if (tools is null || tools.Count == 0) return;
        var functions = tools.OfType<AIFunction>().ToList();
        if (functions.Count == 0) return;
        w.WriteStartArray("tools");
        for (var i = 0; i < functions.Count; i++)
        {
            var fn = functions[i];
            w.WriteStartObject();
            w.WriteString("name", fn.Name);
            w.WriteString("description", fn.Description);
            w.WritePropertyName("input_schema");
            fn.JsonSchema.WriteTo(w);
            if (i == functions.Count - 1) CacheControl.Write(w);   // cache the tools prefix
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteMessages(Utf8JsonWriter w, IReadOnlyList<ChatMessage> messages)
    {
        // The last non-system message carries the cache breakpoint for the transcript.
        var lastIdx = -1;
        for (var i = 0; i < messages.Count; i++)
            if (messages[i].Role != ChatRole.System) lastIdx = i;

        w.WriteStartArray("messages");
        for (var i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];
            if (msg.Role == ChatRole.System) continue; // folded into "system"

            // Tool results are carried in ChatRole.Tool messages; Anthropic expects
            // them inside a user turn.
            var role = msg.Role == ChatRole.Assistant ? "assistant" : "user";
            w.WriteStartObject();
            w.WriteString("role", role);
            w.WriteStartArray("content");
            var contents = msg.Contents;
            for (var c = 0; c < contents.Count; c++)
                WriteContent(w, contents[c], cache: i == lastIdx && c == contents.Count - 1);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteContent(Utf8JsonWriter w, AIContent content, bool cache = false)
    {
        switch (content)
        {
            case TextContent t:
                w.WriteStartObject();
                w.WriteString("type", "text");
                w.WriteString("text", t.Text);
                if (cache) CacheControl.Write(w);
                w.WriteEndObject();
                break;

            case FunctionCallContent call:
                w.WriteStartObject();
                w.WriteString("type", "tool_use");
                w.WriteString("id", call.CallId);
                w.WriteString("name", call.Name);
                w.WritePropertyName("input");
                JsonSerializer.Serialize(w, call.Arguments ?? new Dictionary<string, object?>());
                if (cache) CacheControl.Write(w);
                w.WriteEndObject();
                break;

            case FunctionResultContent result:
                w.WriteStartObject();
                w.WriteString("type", "tool_result");
                w.WriteString("tool_use_id", result.CallId);
                w.WriteString("content", ResultToString(result.Result));
                if (cache) CacheControl.Write(w);
                w.WriteEndObject();
                break;
        }
    }

    private static string ResultToString(object? result) => result switch
    {
        null => "",
        string s => s,
        _ => JsonSerializer.Serialize(result),
    };

    private static ChatFinishReason MapFinishReason(string stopReason, bool hasToolCalls) =>
        hasToolCalls || stopReason == "tool_use" ? ChatFinishReason.ToolCalls
        : stopReason == "max_tokens" ? ChatFinishReason.Length
        : ChatFinishReason.Stop;

    private static int UsageInt(JsonElement usage, string prop) =>
        usage.TryGetProperty(prop, out var v) && v.TryGetInt32(out var n) ? n : 0;

    private sealed class ToolBuilder
    {
        public string Id = "";
        public string Name = "";
        public readonly StringBuilder Json = new();
    }
}
