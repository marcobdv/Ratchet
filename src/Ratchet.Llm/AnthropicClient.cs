using System.Buffers;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodeStack.Ratchet.Core;

namespace CodeStack.Ratchet.Llm;

/// <summary>
/// A hand-rolled Anthropic Messages API client — no SDK. The point of v0 is to
/// see the wire format, so this builds the request JSON by hand and consumes the
/// streamed SSE response by hand. Swapping providers later means writing a
/// sibling of this class behind the same ILlmClient seam.
///
/// Endpoint:    POST https://api.anthropic.com/v1/messages  (stream: true)
/// Auth:        x-api-key header + anthropic-version header
/// Loop signal: message_delta carries stop_reason == "tool_use"
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
        Action<string> onTextDelta,
        CancellationToken ct)
    {
        using var span = RatchetTelemetry.StartChat("anthropic", _model);
        var started = Stopwatch.GetTimestamp();

        try
        {
            var requestJson = BuildRequestJson(systemPrompt, conversation, tools);
            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
            };

            // ResponseHeadersRead: start reading the body as it arrives instead of
            // buffering the whole response — that's what makes streaming "live".
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException($"Anthropic API {(int)resp.StatusCode}: {err}");
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            var response = await ConsumeStreamAsync(reader, onTextDelta, ct);

            RatchetTelemetry.RecordChatResult(span, "anthropic", _model, response.InputTokens, response.OutputTokens,
                Stopwatch.GetElapsedTime(started).TotalSeconds, response.StopReason);
            return response;
        }
        catch (Exception ex)
        {
            // A failed call (non-2xx, network drop, parse error) must surface on the span and
            // the duration metric, not look successful — else error/latency dashboards under-report.
            RatchetTelemetry.RecordChatError(span, "anthropic", _model, Stopwatch.GetElapsedTime(started).TotalSeconds, ex);
            throw;
        }
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
        w.WriteBoolean("stream", true);

        // Prompt caching: the system prompt and tool specs are stable across every
        // turn, so cache them; and put a breakpoint at the end of the transcript so
        // the conversation prefix is cached and re-read instead of re-billed each
        // turn. The API ignores breakpoints under the cache minimum, so this is safe
        // to always emit. See CacheControl.Write.
        w.WritePropertyName("system");
        w.WriteStartArray();
        w.WriteStartObject();
        w.WriteString("type", "text");
        w.WriteString("text", systemPrompt);
        CacheControl.Write(w);
        w.WriteEndObject();
        w.WriteEndArray();

        WriteTools(w, tools);
        WriteMessages(w, conversation);

        w.WriteEndObject();
        w.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteTools(Utf8JsonWriter w, IReadOnlyCollection<ITool> tools)
    {
        w.WriteStartArray("tools");
        var last = tools.Count - 1;
        var i = 0;
        foreach (var tool in tools)
        {
            w.WriteStartObject();
            w.WriteString("name", tool.Name);
            w.WriteString("description", tool.Description);
            w.WritePropertyName("input_schema");
            using (var schema = JsonDocument.Parse(tool.InputSchemaJson))
                schema.RootElement.WriteTo(w);
            if (i == last) CacheControl.Write(w);   // cache the whole tools prefix
            w.WriteEndObject();
            i++;
        }
        w.WriteEndArray();
    }

    private static void WriteMessages(Utf8JsonWriter w, Conversation conversation)
    {
        w.WriteStartArray("messages");
        var lastMsg = conversation.Messages.Count - 1;
        for (var mi = 0; mi < conversation.Messages.Count; mi++)
        {
            var msg = conversation.Messages[mi];
            w.WriteStartObject();
            w.WriteString("role", msg.Role == Role.User ? "user" : "assistant");
            w.WriteStartArray("content");
            var lastBlock = msg.Content.Count - 1;
            for (var bi = 0; bi < msg.Content.Count; bi++)
                // A cache breakpoint on the final block caches the whole prefix up to here.
                WriteContentBlock(w, msg.Content[bi], cache: mi == lastMsg && bi == lastBlock);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteContentBlock(Utf8JsonWriter w, ContentBlock block, bool cache = false)
    {
        switch (block)
        {
            case TextBlock t:
                w.WriteStartObject();
                w.WriteString("type", "text");
                w.WriteString("text", t.Text);
                if (cache) CacheControl.Write(w);
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
                if (cache) CacheControl.Write(w);
                w.WriteEndObject();
                break;

            case ToolResultBlock r:
                w.WriteStartObject();
                w.WriteString("type", "tool_result");
                w.WriteString("tool_use_id", r.ToolUseId);
                w.WriteString("content", r.Content);
                if (r.IsError) w.WriteBoolean("is_error", true);
                if (cache) CacheControl.Write(w);
                w.WriteEndObject();
                break;
        }
    }

    // ---- response parsing (SSE) -------------------------------------------

    /// <summary>
    /// Consume the Server-Sent Events stream and rebuild the assistant message.
    ///
    /// The Messages API streams a sequence of typed events. The ones that matter:
    ///   message_start        -> input token count
    ///   content_block_start  -> a new block begins (text, or tool_use with id+name)
    ///   content_block_delta  -> text_delta (live text) OR input_json_delta (tool args)
    ///   content_block_stop   -> that block is complete
    ///   message_delta        -> stop_reason + final output token count
    ///   message_stop         -> end of stream
    ///
    /// Text deltas are emitted live; tool-call arguments arrive as JSON fragments
    /// that we concatenate per block index, then parse as a whole at the end.
    /// </summary>
    internal static async Task<LlmResponse> ConsumeStreamAsync(
        StreamReader reader, Action<string> onTextDelta, CancellationToken ct)
    {
        var builders = new SortedDictionary<int, BlockBuilder>();
        int inputTokens = 0, outputTokens = 0;
        var stopReason = "end_turn";

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            // SSE: events are separated by blank lines; we only need the data: rows.
            if (line.Length == 0 || line.StartsWith("event:", StringComparison.Ordinal))
                continue;
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var payload = line.AsSpan(5).Trim();
            if (payload.IsEmpty)
                continue;

            using var doc = JsonDocument.Parse(payload.ToString());
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();

            switch (type)
            {
                case "message_start":
                    if (root.TryGetProperty("message", out var m) &&
                        m.TryGetProperty("usage", out var u0))
                        // Anthropic reports the cached portions separately; include them so
                        // input-token accounting reflects actual billed input (caching is always on).
                        inputTokens = UsageInt(u0, "input_tokens")
                                    + UsageInt(u0, "cache_creation_input_tokens")
                                    + UsageInt(u0, "cache_read_input_tokens");
                    break;

                case "content_block_start":
                {
                    var idx = root.GetProperty("index").GetInt32();
                    var cb = root.GetProperty("content_block");
                    var cbType = cb.GetProperty("type").GetString()!;
                    var b = new BlockBuilder { Type = cbType };
                    if (cbType == "tool_use")
                    {
                        b.Id = cb.GetProperty("id").GetString();
                        b.Name = cb.GetProperty("name").GetString();
                    }
                    builders[idx] = b;
                    break;
                }

                case "content_block_delta":
                {
                    var idx = root.GetProperty("index").GetInt32();
                    if (!builders.TryGetValue(idx, out var b)) break;
                    var delta = root.GetProperty("delta");
                    switch (delta.GetProperty("type").GetString())
                    {
                        case "text_delta":
                            var t = delta.GetProperty("text").GetString() ?? "";
                            b.Text.Append(t);
                            onTextDelta(t);          // <-- live output
                            break;
                        case "input_json_delta":
                            b.Json.Append(delta.GetProperty("partial_json").GetString() ?? "");
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
                    var msg = root.TryGetProperty("error", out var e) &&
                              e.TryGetProperty("message", out var em)
                        ? em.GetString() : "unknown streaming error";
                    throw new InvalidOperationException($"Anthropic stream error: {msg}");

                // content_block_stop / message_stop / ping: nothing to do.
            }
        }

        var blocks = new List<ContentBlock>(builders.Count);
        foreach (var b in builders.Values)
        {
            if (b.Type == "text")
                blocks.Add(new TextBlock(b.Text.ToString()));
            else if (b.Type == "tool_use")
                blocks.Add(new ToolUseBlock(b.Id!, b.Name!, b.Json.Length == 0 ? "{}" : b.Json.ToString()));
        }

        return new LlmResponse(new Message(Role.Assistant, blocks), stopReason, inputTokens, outputTokens);
    }

    private static int UsageInt(JsonElement usage, string prop) =>
        usage.TryGetProperty(prop, out var v) && v.TryGetInt32(out var n) ? n : 0;

    /// <summary>Accumulates one streamed content block across its delta events.</summary>
    private sealed class BlockBuilder
    {
        public string Type = "";
        public string? Id;
        public string? Name;
        public readonly StringBuilder Text = new();
        public readonly StringBuilder Json = new();
    }

    public void Dispose() => _http.Dispose();
}
