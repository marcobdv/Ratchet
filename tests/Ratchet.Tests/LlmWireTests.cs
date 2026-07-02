using System.Net;
using System.Text;
using CodeStack.Ratchet.Core;
using CodeStack.Ratchet.Llm;
using Xunit;

namespace CodeStack.Ratchet.Tests;

/// <summary>
/// The shared failure plumbing: retry/backoff classification, typed error parsing,
/// and Retry-After handling — against a scripted HttpMessageHandler.
/// </summary>
[Collection("llm-wire")] // mutates LlmWire statics; keep these tests off the parallel pool
public sealed class LlmWireTests : IDisposable
{
    private readonly TimeSpan _origBase = LlmWire.BaseDelay;
    private readonly int _origAttempts = LlmWire.MaxAttempts;

    public LlmWireTests() => LlmWire.BaseDelay = TimeSpan.FromMilliseconds(1);

    public void Dispose()
    {
        LlmWire.BaseDelay = _origBase;
        LlmWire.MaxAttempts = _origAttempts;
    }

    private static HttpRequestMessage Request() => new(HttpMethod.Post, "https://example.test/v1/messages")
    {
        Content = new StringContent("{}", Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task RetryableStatus_IsRetried_ThenSucceeds()
    {
        var handler = new ScriptedHandler(
            _ => Respond(HttpStatusCode.TooManyRequests, """{"error":{"type":"rate_limit_error","message":"slow down"}}"""),
            _ => Respond((HttpStatusCode)529, """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}"""),
            _ => Respond(HttpStatusCode.OK, "ok"));
        using var http = new HttpClient(handler);

        using var resp = await LlmWire.SendWithRetryAsync(http, Request, "test", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task NonRetryableStatus_FailsImmediately_WithParsedErrorType()
    {
        var handler = new ScriptedHandler(
            _ => Respond(HttpStatusCode.BadRequest, """{"error":{"type":"invalid_request_error","message":"bad tool schema"}}"""));
        using var http = new HttpClient(handler);

        var ex = await Assert.ThrowsAsync<LlmException>(
            () => LlmWire.SendWithRetryAsync(http, Request, "test", CancellationToken.None));

        Assert.Equal(1, handler.Calls); // no retry burned on a permanent failure
        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("invalid_request_error", ex.ErrorType);
        Assert.False(ex.Retryable);
        Assert.Contains("bad tool schema", ex.Message);
    }

    [Fact]
    public async Task PersistentRetryableFailure_ExhaustsAttempts_ThenThrowsTyped()
    {
        LlmWire.MaxAttempts = 3;
        var handler = new ScriptedHandler(Enumerable.Repeat<Func<HttpRequestMessage, HttpResponseMessage>>(
            _ => Respond((HttpStatusCode)529, """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}"""), 10).ToArray());
        using var http = new HttpClient(handler);

        var ex = await Assert.ThrowsAsync<LlmException>(
            () => LlmWire.SendWithRetryAsync(http, Request, "test", CancellationToken.None));

        Assert.Equal(3, handler.Calls);
        Assert.Equal(529, ex.StatusCode);
        Assert.Equal("overloaded_error", ex.ErrorType);
        Assert.True(ex.Retryable);
    }

    [Fact]
    public async Task NetworkError_IsRetried()
    {
        var handler = new ScriptedHandler(
            _ => throw new HttpRequestException("connection reset"),
            _ => Respond(HttpStatusCode.OK, "ok"));
        using var http = new HttpClient(handler);

        using var resp = await LlmWire.SendWithRetryAsync(http, Request, "test", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task RequestIdHeader_SurfacesInTheError()
    {
        var handler = new ScriptedHandler(_ =>
        {
            var r = Respond(HttpStatusCode.BadRequest, """{"error":{"type":"invalid_request_error","message":"nope"}}""");
            r.Headers.Add("request-id", "req_123abc");
            return r;
        });
        using var http = new HttpClient(handler);

        var ex = await Assert.ThrowsAsync<LlmException>(
            () => LlmWire.SendWithRetryAsync(http, Request, "test", CancellationToken.None));

        Assert.Contains("req_123abc", ex.Message);
    }

    [Fact]
    public void FromHttp_ToleratesNonJsonBody()
    {
        var ex = LlmWire.FromHttp("test", 503, "<html>Service Unavailable</html>");
        Assert.Equal(503, ex.StatusCode);
        Assert.Null(ex.ErrorType);
        Assert.True(ex.Retryable);
    }

    private static HttpResponseMessage Respond(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage>[] _script;
        public int Calls { get; private set; }

        public ScriptedHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] script) => _script = script;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var i = Math.Min(Calls, _script.Length - 1);
            Calls++;
            return Task.FromResult(_script[i](request));
        }
    }
}
