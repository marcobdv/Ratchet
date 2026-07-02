using System.Text.Json;
using CodeStack.Ratchet.Core;

namespace CodeStack.Ratchet.Llm;

/// <summary>
/// Shared HTTP plumbing for the hand-rolled clients: send-with-retry (exponential
/// backoff honouring Retry-After for 408/429/5xx and network errors), provider error
/// parsing into <see cref="LlmException"/>, and an idle-read guard for SSE streams.
/// The wire formats stay hand-rolled in each client; only the failure handling is
/// shared, because it must be identical everywhere to be trustworthy.
/// </summary>
internal static class LlmWire
{
    /// <summary>Attempts per request (first try + retries). Retries happen only before any content streams.</summary>
    internal static int MaxAttempts = 4;

    /// <summary>Backoff base — attempt n waits base * 2^(n-1), capped at 30s. Test hook.</summary>
    internal static TimeSpan BaseDelay = TimeSpan.FromSeconds(1);

    /// <summary>Abort a stream when no line arrives for this long (a hung connection, not progress).</summary>
    internal static TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// POST and return the successful response, retrying retryable failures. The request
    /// is rebuilt per attempt (HttpRequestMessage is single-use). Anything non-retryable —
    /// or still failing after <see cref="MaxAttempts"/> — surfaces as <see cref="LlmException"/>.
    /// </summary>
    internal static async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpClient http, Func<HttpRequestMessage> makeRequest, string provider, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage? resp = null;
            LlmException failure;
            try
            {
                resp = await http.SendAsync(makeRequest(), HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                    return resp;

                var status = (int)resp.StatusCode;
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var requestId = resp.Headers.TryGetValues("request-id", out var ids) ? ids.FirstOrDefault() : null;
                var retryAfter = resp.Headers.RetryAfter?.Delta
                    ?? (resp.Headers.RetryAfter?.Date is { } when ? when - DateTimeOffset.UtcNow : null);
                resp.Dispose();

                failure = FromHttp(provider, status, body, requestId);
                if (!failure.Retryable || attempt >= MaxAttempts)
                    throw failure;
                await Task.Delay(Backoff(attempt, retryAfter), ct).ConfigureAwait(false);
                continue;
            }
            catch (HttpRequestException ex)
            {
                resp?.Dispose();
                failure = new LlmException($"{provider}: network error: {ex.Message}",
                    statusCode: null, errorType: "network_error", retryable: true, inner: ex);
            }

            if (attempt >= MaxAttempts)
                throw failure;
            await Task.Delay(Backoff(attempt, null), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Parse a non-2xx body into a typed exception. Understands both the
    /// Anthropic ({"error":{"type","message"}}) and OpenAI ({"error":{"type"/"code","message"}}) shapes.</summary>
    internal static LlmException FromHttp(string provider, int status, string body, string? requestId = null)
    {
        string? errorType = null, errorMessage = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
            {
                if (err.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String)
                    errorType = t.GetString();
                else if (err.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String)
                    errorType = c.GetString();
                if (err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                    errorMessage = m.GetString();
            }
        }
        catch (JsonException) { /* non-JSON error body — keep it verbatim */ }

        var retryable = status is 408 or 429 or >= 500 || errorType == "overloaded_error";
        var detail = errorMessage ?? Truncate(body, 500);
        var suffix = requestId is null ? "" : $" (request-id: {requestId})";
        return new LlmException(
            $"{provider} API {status}{(errorType is null ? "" : $" {errorType}")}: {detail}{suffix}",
            status, errorType, retryable);
    }

    /// <summary>
    /// Read one SSE line, converting a stall (no data for <see cref="IdleTimeout"/>) into
    /// <see cref="LlmStreamInterruptedException"/> instead of hanging forever. The caller
    /// creates <paramref name="idle"/> linked to its own token; each successful read re-arms it.
    /// </summary>
    internal static async ValueTask<string?> ReadLineAsync(
        StreamReader reader, CancellationTokenSource idle, CancellationToken callerCt)
    {
        try
        {
            var line = await reader.ReadLineAsync(idle.Token).ConfigureAwait(false);
            idle.CancelAfter(IdleTimeout);
            return line;
        }
        catch (OperationCanceledException) when (!callerCt.IsCancellationRequested)
        {
            throw new LlmStreamInterruptedException(
                $"stream stalled: no data received for {IdleTimeout.TotalSeconds:0}s.");
        }
    }

    internal static CancellationTokenSource StartIdleGuard(CancellationToken ct)
    {
        var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(IdleTimeout);
        return idle;
    }

    private static TimeSpan Backoff(int attempt, TimeSpan? retryAfter)
    {
        var backoff = TimeSpan.FromTicks(BaseDelay.Ticks * (1L << Math.Min(attempt - 1, 5)));
        if (backoff > TimeSpan.FromSeconds(30)) backoff = TimeSpan.FromSeconds(30);
        if (retryAfter is { } ra && ra > backoff && ra <= TimeSpan.FromSeconds(60)) return ra;
        return backoff;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
