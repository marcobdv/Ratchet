using System.Buffers;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace CodeStack.Ratchet.Llm;

/// <summary>
/// A hand-rolled OpenAI-compatible chat client (the <c>/v1/chat/completions</c> wire
/// shape), exposed as an <see cref="IChatClient"/> so it rides the same seam as
/// <see cref="AnthropicChatClient"/> and is wrapped by <see cref="ChatClientLlm"/>.
/// This is the "second ILlmClient impl" the workflow design names as a prerequisite:
/// it points Ratchet at any OpenAI-compatible endpoint — Ollama, LM Studio, vLLM,
/// llama.cpp — so a workflow's cheap <c>local</c> driver tiers have something to run on.
///
/// Same philosophy as the Anthropic client: build the request JSON by hand, consume
/// the SSE stream by hand, including reassembling streamed tool-call arguments.
/// </summary>
public sealed class OpenAiChatClient : IChatClient
{
    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string _model;
    private readonly int _maxTokens;

    /// <param name="baseUrl">e.g. http://localhost:11434/v1 (Ollama), https://openrouter.ai/api/v1, https://api.openai.com/v1.</param>
    /// <param name="apiKey">Bearer token; many local servers ignore it (pass anything).</param>
    /// <param name="extraHeaders">Extra request headers — e.g. OpenRouter's HTTP-Referer / X-Title attribution.</param>
    public OpenAiChatClient(string baseUrl, string model, string? apiKey = null, int maxTokens = 4096,
        IReadOnlyDictionary<string, string>? extraHeaders = null)
    {
        _model = model;
        _maxTokens = maxTokens;
        _endpoint = baseUrl.TrimEnd('/') + "/chat/completions";
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        if (!string.IsNullOrWhiteSpace(apiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        if (extraHeaders is not null)
            foreach (var (k, v) in extraHeaders)
                if (!string.IsNullOrWhiteSpace(v)) _http.DefaultRequestHeaders.Add(k, v);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var requestJson = BuildRequestJson(messages, options);
        using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json"),
        };

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"OpenAI API {(int)resp.StatusCode}: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        var toolBuilders = new SortedDictionary<int, ToolBuilder>();
        long inputTokens = 0, outputTokens = 0;
        var finish = "stop";

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = line.AsSpan(5).Trim();
            if (payload.IsEmpty) continue;
            if (payload.SequenceEqual("[DONE]")) break;

            using var doc = JsonDocument.Parse(payload.ToString());
            var root = doc.RootElement;

            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt64(out var p)) inputTokens = p;
                if (usage.TryGetProperty("completion_tokens", out var ctk) && ctk.TryGetInt64(out var co)) outputTokens = co;
            }

            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
            var choice = choices[0];

            if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                finish = fr.GetString()!;

            if (!choice.TryGetProperty("delta", out var delta)) continue;

            if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            {
                var text = content.GetString() ?? "";
                if (text.Length > 0) yield return new ChatResponseUpdate(ChatRole.Assistant, text);
            }

            if (delta.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
            {
                foreach (var call in calls.EnumerateArray())
                {
                    var idx = call.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var iv) ? iv : 0;
                    if (!toolBuilders.TryGetValue(idx, out var tb)) toolBuilders[idx] = tb = new ToolBuilder();
                    if (call.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                        tb.Id = id.GetString()!;
                    if (call.TryGetProperty("function", out var fn))
                    {
                        if (fn.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String)
                            tb.Name = nm.GetString()!;
                        if (fn.TryGetProperty("arguments", out var ar) && ar.ValueKind == JsonValueKind.String)
                            tb.Args.Append(ar.GetString());
                    }
                }
            }
        }

        var finalContents = new List<AIContent>();
        foreach (var tb in toolBuilders.Values)
        {
            if (string.IsNullOrEmpty(tb.Name)) continue;
            var args = tb.Args.Length == 0
                ? new Dictionary<string, object?>()
                : JsonSerializer.Deserialize<Dictionary<string, object?>>(tb.Args.ToString()) ?? new();
            finalContents.Add(new FunctionCallContent(
                string.IsNullOrEmpty(tb.Id) ? "call_" + tb.Name : tb.Id, tb.Name, args));
        }
        finalContents.Add(new UsageContent(new UsageDetails
        {
            InputTokenCount = inputTokens,
            OutputTokenCount = outputTokens,
        }));

        yield return new ChatResponseUpdate(ChatRole.Assistant, finalContents)
        {
            FinishReason = toolBuilders.Count > 0 || finish == "tool_calls" ? ChatFinishReason.ToolCalls
                : finish == "length" ? ChatFinishReason.Length
                : ChatFinishReason.Stop,
        };
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
            updates.Add(u);
        return updates.ToChatResponse();
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() => _http.Dispose();

    // ---- request building --------------------------------------------------

    private string BuildRequestJson(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        var buffer = new ArrayBufferWriter<byte>();
        using var w = new Utf8JsonWriter(buffer);

        w.WriteStartObject();
        w.WriteString("model", options?.ModelId ?? _model);
        w.WriteNumber("max_tokens", (int)(options?.MaxOutputTokens ?? _maxTokens));
        w.WriteBoolean("stream", true);
        w.WritePropertyName("stream_options");
        w.WriteStartObject(); w.WriteBoolean("include_usage", true); w.WriteEndObject();

        WriteMessages(w, list, options?.Instructions);
        WriteTools(w, options?.Tools);

        w.WriteEndObject();
        w.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteMessages(Utf8JsonWriter w, IReadOnlyList<ChatMessage> messages, string? instructions)
    {
        w.WriteStartArray("messages");

        if (!string.IsNullOrEmpty(instructions))
        {
            w.WriteStartObject();
            w.WriteString("role", "system");
            w.WriteString("content", instructions);
            w.WriteEndObject();
        }

        foreach (var msg in messages)
        {
            if (msg.Role == ChatRole.Tool)
            {
                // Each tool result is its own OpenAI `tool` message.
                foreach (var c in msg.Contents.OfType<FunctionResultContent>())
                {
                    w.WriteStartObject();
                    w.WriteString("role", "tool");
                    w.WriteString("tool_call_id", c.CallId);
                    w.WriteString("content", ResultToString(c.Result));
                    w.WriteEndObject();
                }
                continue;
            }

            var role = msg.Role == ChatRole.System ? "system"
                : msg.Role == ChatRole.Assistant ? "assistant" : "user";
            var text = string.Concat(msg.Contents.OfType<TextContent>().Select(t => t.Text));
            var toolCalls = msg.Contents.OfType<FunctionCallContent>().ToList();

            w.WriteStartObject();
            w.WriteString("role", role);
            w.WriteString("content", text);
            if (toolCalls.Count > 0)
            {
                w.WriteStartArray("tool_calls");
                foreach (var call in toolCalls)
                {
                    w.WriteStartObject();
                    w.WriteString("id", call.CallId);
                    w.WriteString("type", "function");
                    w.WritePropertyName("function");
                    w.WriteStartObject();
                    w.WriteString("name", call.Name);
                    w.WriteString("arguments", JsonSerializer.Serialize(call.Arguments ?? new Dictionary<string, object?>()));
                    w.WriteEndObject();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteTools(Utf8JsonWriter w, IList<AITool>? tools)
    {
        var functions = tools?.OfType<AIFunction>().ToList();
        if (functions is null || functions.Count == 0) return;
        w.WriteStartArray("tools");
        foreach (var fn in functions)
        {
            w.WriteStartObject();
            w.WriteString("type", "function");
            w.WritePropertyName("function");
            w.WriteStartObject();
            w.WriteString("name", fn.Name);
            w.WriteString("description", fn.Description);
            w.WritePropertyName("parameters");
            fn.JsonSchema.WriteTo(w);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static string ResultToString(object? result) => result switch
    {
        null => "",
        string s => s,
        _ => JsonSerializer.Serialize(result),
    };

    private sealed class ToolBuilder
    {
        public string Id = "";
        public string Name = "";
        public readonly StringBuilder Args = new();
    }
}
