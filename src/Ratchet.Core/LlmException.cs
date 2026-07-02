namespace CodeStack.Ratchet.Core;

/// <summary>
/// A model call failed in a way the caller can reason about: HTTP status, the
/// provider's error type (e.g. "overloaded_error", "rate_limit_error"), and whether
/// retrying could help. Lives in Core (BCL-only) so the loop, the CLI, and the
/// workflow scheduler can catch it without referencing a provider project — the
/// provider clients construct it, everything above the <see cref="ILlmClient"/>
/// seam consumes it.
/// </summary>
public class LlmException : Exception
{
    /// <summary>HTTP status code, when the failure was an HTTP response (else null).</summary>
    public int? StatusCode { get; }

    /// <summary>The provider's machine-readable error type, when one was parseable.</summary>
    public string? ErrorType { get; }

    /// <summary>Whether a retry could plausibly succeed (429/5xx/overloaded/network).</summary>
    public bool Retryable { get; }

    public LlmException(string message, int? statusCode = null, string? errorType = null,
        bool retryable = false, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        ErrorType = errorType;
        Retryable = retryable;
    }
}

/// <summary>
/// The stream ended before the provider said it was done (dropped connection, proxy
/// timeout, idle stall). Distinct from <see cref="LlmException"/> because partial
/// output may already have been delivered to the observer — the caller must treat
/// the turn as incomplete, never as a short-but-successful answer.
/// </summary>
public sealed class LlmStreamInterruptedException : LlmException
{
    public LlmStreamInterruptedException(string message, Exception? inner = null)
        : base(message, statusCode: null, errorType: "stream_interrupted", retryable: false, inner) { }
}
