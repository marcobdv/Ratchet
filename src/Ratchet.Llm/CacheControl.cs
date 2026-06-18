using System.Text.Json;

namespace CodeStack.Ratchet.Llm;

/// <summary>
/// Emits Anthropic's <c>"cache_control": {"type": "ephemeral"}</c> marker on the
/// JSON object currently being written. A breakpoint tells the API to cache the
/// whole prompt prefix up to that point; on the next turn the unchanged prefix is
/// read from cache instead of re-billed at full input price.
///
/// Ratchet places breakpoints on the (stable) system prompt and tool list, and on
/// the tail of the transcript so the growing conversation prefix is cached too.
/// Prefixes shorter than the model's cache minimum are silently not cached, so a
/// breakpoint is always safe to emit.
/// </summary>
internal static class CacheControl
{
    /// <summary>Write the cache_control property into the open JSON object.</summary>
    public static void Write(Utf8JsonWriter w)
    {
        w.WritePropertyName("cache_control");
        w.WriteStartObject();
        w.WriteString("type", "ephemeral");
        w.WriteEndObject();
    }
}
