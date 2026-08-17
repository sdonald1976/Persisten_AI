using System.Net;
using Companion.Infrastructure.Models;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Provider-boundary hardening (Priority 4), exercised with a mocked HttpMessageHandler — no LM
/// Studio or Ollama required. Both speak the same OpenAI-compatible API, so one adapter is tested.
/// </summary>
public class ProviderHardeningTests
{
    private const string ChatOk =
        "{\"choices\":[{\"message\":{\"content\":\"hello from the model\"}}]}";
    private static string EmbeddingOk(int dims) =>
        "{\"data\":[{\"embedding\":[" + string.Join(",", Enumerable.Repeat("0.1", dims)) + "]}]}";

    private static EndpointOptions Options(int retries = 2, int timeoutSeconds = 30, int dims = 0) => new()
    {
        BaseUrl = "http://localhost:1234/v1",
        Model = "test-model",
        TimeoutSeconds = timeoutSeconds,
        MaxRetries = retries,
        Dimensions = dims,
    };

    // Build a client that runs the SAME Polly retry pipeline as production, over the stub handler,
    // so retry/backoff behavior is exercised end-to-end (not bypassed).
    private static HttpClient BuildClient(StubHttpMessageHandler stub, EndpointOptions opts)
    {
        var policy = ProviderHttpClients.RetryPolicy(opts, "test", NullLogger.Instance);
        var handler = new Microsoft.Extensions.Http.PolicyHttpMessageHandler(policy) { InnerHandler = stub };
        return new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:1234/v1/"),
            Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds),
        };
    }

    private static OpenAiCompatibleChatModel Chat(StubHttpMessageHandler handler, EndpointOptions opts)
    {
        var http = BuildClient(handler, opts);
        return new OpenAiCompatibleChatModel(opts, () => http, NullLogger<OpenAiCompatibleChatModel>.Instance);
    }

    private static OpenAiCompatibleEmbeddingModel Embed(StubHttpMessageHandler handler, EndpointOptions opts)
    {
        var http = BuildClient(handler, opts);
        return new OpenAiCompatibleEmbeddingModel(opts, () => http, NullLogger<OpenAiCompatibleEmbeddingModel>.Instance);
    }

    [Fact]
    public async Task Chat_Success_ReturnsContent() // LM Studio / Ollama share this path
    {
        var handler = new StubHttpMessageHandler().Enqueue(HttpStatusCode.OK, ChatOk);
        var reply = await Chat(handler, Options()).CompleteAsync("sys", "hi");
        Assert.Equal("hello from the model", reply.Text);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Embeddings_Success_ReturnsVector()
    {
        var handler = new StubHttpMessageHandler().Enqueue(HttpStatusCode.OK, EmbeddingOk(768));
        var vec = await Embed(handler, Options(dims: 768)).EmbedAsync("hi");
        Assert.Equal(768, vec.Length);
    }

    [Fact]
    public async Task Chat_RetriesTransient_ThenSucceeds()
    {
        var handler = new StubHttpMessageHandler()
            .Enqueue(HttpStatusCode.TooManyRequests, "{}")   // 429
            .Enqueue(HttpStatusCode.ServiceUnavailable, "{}") // 503
            .Enqueue(HttpStatusCode.OK, ChatOk);
        var reply = await Chat(handler, Options(retries: 3)).CompleteAsync("sys", "hi");
        Assert.Equal("hello from the model", reply.Text);
        Assert.Equal(3, handler.CallCount); // two failures + success
    }

    [Fact]
    public async Task Chat_RetryExhaustion_Throws()
    {
        var handler = new StubHttpMessageHandler()
            .Enqueue(HttpStatusCode.TooManyRequests, "{}")
            .Enqueue(HttpStatusCode.TooManyRequests, "{}")
            .Enqueue(HttpStatusCode.TooManyRequests, "{}");
        var ex = await Assert.ThrowsAsync<ModelProviderException>(
            () => Chat(handler, Options(retries: 2)).CompleteAsync("sys", "hi"));
        Assert.Contains("rate-limit", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, handler.CallCount); // 1 + 2 retries
    }

    [Fact]
    public async Task RetryAfterHeader_IsHonored_WithoutHangingTheTest()
    {
        // 1s Retry-After then success — proves the header path runs and still completes quickly.
        var handler = new StubHttpMessageHandler()
            .EnqueueWithRetryAfter(HttpStatusCode.TooManyRequests, retryAfterSeconds: 1)
            .Enqueue(HttpStatusCode.OK, ChatOk);
        var reply = await Chat(handler, Options(retries: 1)).CompleteAsync("sys", "hi");
        Assert.Equal("hello from the model", reply.Text);
    }

    [Fact]
    public async Task MissingModel_404_MapsToClearError()
    {
        var handler = new StubHttpMessageHandler()
            .Enqueue(HttpStatusCode.NotFound, "{\"error\":\"model 'nope' not found\"}");
        var ex = await Assert.ThrowsAsync<ModelProviderException>(
            () => Chat(handler, Options(retries: 0)).CompleteAsync("sys", "hi"));
        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AuthFailure_401_MapsToCredentialsError()
    {
        var handler = new StubHttpMessageHandler().Enqueue(HttpStatusCode.Unauthorized, "{}");
        var ex = await Assert.ThrowsAsync<ModelProviderException>(
            () => Chat(handler, Options(retries: 0)).CompleteAsync("sys", "hi"));
        Assert.Contains("credentials", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConnectionRefused_MapsToUnreachable()
    {
        var handler = new StubHttpMessageHandler { Throw = new HttpRequestException("connection refused") };
        var ex = await Assert.ThrowsAsync<ModelProviderException>(
            () => Chat(handler, Options(retries: 0)).CompleteAsync("sys", "hi"));
        Assert.Contains("Couldn't reach", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Timeout_MapsToTimeoutError()
    {
        // Handler delays longer than the 1s client timeout → TaskCanceledException (not caller cancel).
        var handler = new StubHttpMessageHandler { Delay = TimeSpan.FromSeconds(5) };
        var ex = await Assert.ThrowsAsync<ModelProviderException>(
            () => Chat(handler, Options(retries: 0, timeoutSeconds: 1)).CompleteAsync("sys", "hi"));
        Assert.Contains("timed out", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancellation_PropagatesAsOperationCanceled_NotRemapped()
    {
        var handler = new StubHttpMessageHandler { Delay = TimeSpan.FromSeconds(5) };
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Chat(handler, Options(retries: 2, timeoutSeconds: 30)).CompleteAsync("sys", "hi", ct: cts.Token));
    }

    [Fact]
    public async Task MalformedJson_Throws()
    {
        var handler = new StubHttpMessageHandler().Enqueue(HttpStatusCode.OK, "this is not json at all");
        await Assert.ThrowsAnyAsync<Exception>(() => Chat(handler, Options(retries: 0)).CompleteAsync("sys", "hi"));
    }

    [Fact]
    public async Task EmbeddingDimensionMismatch_IsRejected()
    {
        // Configured for 768 but the model returns 3 → reject loudly rather than corrupt the index.
        var handler = new StubHttpMessageHandler().Enqueue(HttpStatusCode.OK, EmbeddingOk(3));
        var ex = await Assert.ThrowsAsync<ModelProviderException>(
            () => Embed(handler, Options(dims: 768)).EmbedAsync("hi"));
        Assert.Contains("dimension mismatch", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OversizedResponse_IsRejected()
    {
        var huge = "{\"choices\":[{\"message\":{\"content\":\"" + new string('x', 5000) + "\"}}]}";
        var opts = Options(retries: 0);
        opts.MaxResponseBytes = 1024; // smaller than the body
        var handler = new StubHttpMessageHandler().Enqueue(HttpStatusCode.OK, huge);
        var ex = await Assert.ThrowsAsync<ModelProviderException>(() => Chat(handler, opts).CompleteAsync("sys", "hi"));
        Assert.Contains("too large", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
