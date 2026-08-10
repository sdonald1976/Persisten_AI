using System.Net;
using System.Net.Http.Headers;

namespace Companion.Tests.Fixtures;

/// <summary>
/// A deterministic <see cref="HttpMessageHandler"/> for provider tests — no network, no LM Studio
/// or Ollama needed. Returns queued responses in order (repeating the last), can inject a delay
/// (to exercise the client timeout) or throw (to simulate connection refusal), and records how
/// many times it was called (to assert retries).
/// </summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responders = new();
    private Func<HttpRequestMessage, HttpResponseMessage>? _last;

    public int CallCount { get; private set; }
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;
    public Exception? Throw { get; set; }

    /// <summary>The last request's body text, captured for assertions (e.g. the JSON we sent).</summary>
    public string? LastRequestBody { get; private set; }

    public StubHttpMessageHandler Enqueue(HttpStatusCode status, string body, string contentType = "application/json")
    {
        _responders.Enqueue(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, contentType),
        });
        return this;
    }

    /// <summary>Enqueue a binary response (e.g. synthesized audio).</summary>
    public StubHttpMessageHandler EnqueueBytes(HttpStatusCode status, byte[] body, string contentType)
    {
        _responders.Enqueue(_ =>
        {
            var content = new ByteArrayContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            return new HttpResponseMessage(status) { Content = content };
        });
        return this;
    }

    /// <summary>Enqueue a response carrying a Retry-After header (seconds).</summary>
    public StubHttpMessageHandler EnqueueWithRetryAfter(HttpStatusCode status, int retryAfterSeconds)
    {
        _responders.Enqueue(_ =>
        {
            var resp = new HttpResponseMessage(status) { Content = new StringContent("{}") };
            resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(retryAfterSeconds));
            return resp;
        });
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        CallCount++;

        if (request.Content is not null)
            LastRequestBody = await request.Content.ReadAsStringAsync(ct);

        if (Throw is not null)
            throw Throw;

        if (Delay > TimeSpan.Zero)
            await Task.Delay(Delay, ct); // honors the client timeout / caller cancellation

        var responder = _responders.Count > 0 ? _responders.Dequeue() : _last;
        _last = responder ?? _last;
        if (responder is null)
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        return responder(request);
    }
}
