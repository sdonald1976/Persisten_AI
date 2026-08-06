using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Extensions.Http;

namespace Companion.Infrastructure.Models;

/// <summary>
/// Registers the model provider HTTP clients through Microsoft's <see cref="IHttpClientFactory"/>
/// (named clients, per-role configuration, managed handler lifetime) with a Polly retry policy for
/// transient failures. Replaces the hand-rolled HttpClient factory while keeping the provider
/// interfaces unchanged.
/// </summary>
internal static class ProviderHttpClients
{
    /// <summary>Stable named-client keys, one per role/endpoint.</summary>
    public static string Name(string role) => $"companion-model-{role}";

    public const string Conversation = "conversation";
    public const string Extraction = "extraction";
    public const string Summarizer = "summarizer";
    public const string Embeddings = "embeddings";
    public const string Vision = "vision";
    public const string Transcription = "transcription";

    public static void AddModelHttpClients(this IServiceCollection services, ModelOptions options)
    {
        if (!options.UsesRealModel)
            return;

        Register(services, Conversation, options.Chat);
        Register(services, Extraction, options.ExtractionOrChat);
        Register(services, Summarizer, options.SummarizerOrChat);
        Register(services, Embeddings, options.Embeddings);
        if (options.Vision is { } vision) Register(services, Vision, vision);
        if (options.Transcription is { } transcription) Register(services, Transcription, transcription);
    }

    private static void Register(IServiceCollection services, string role, EndpointOptions ep)
    {
        services.AddHttpClient(Name(role), client =>
            {
                var baseUrl = ep.BaseUrl.EndsWith('/') ? ep.BaseUrl : ep.BaseUrl + "/";
                client.BaseAddress = new Uri(baseUrl);
                client.Timeout = TimeSpan.FromSeconds(Math.Max(1, ep.TimeoutSeconds));
                client.MaxResponseContentBufferSize = Math.Max(1, ep.MaxResponseBytes);
                if (!string.IsNullOrWhiteSpace(ep.ApiKey))
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ep.ApiKey);
            })
            // IHttpClientFactory manages handler pooling + rotation (DNS refresh) for us.
            .SetHandlerLifetime(TimeSpan.FromMinutes(5))
            .AddPolicyHandler((sp, _) =>
                RetryPolicy(ep, role, sp.GetRequiredService<ILoggerFactory>().CreateLogger("Companion.Model.Retry")));
    }

    /// <summary>
    /// Bounded exponential-backoff retry for transient failures (HttpRequestException, 408, 5xx,
    /// and 429), honoring a <c>Retry-After</c> header when present. Request timeouts are NOT retried
    /// (the client's overall timeout has already elapsed). No-op when retries are disabled.
    /// </summary>
    public static IAsyncPolicy<HttpResponseMessage> RetryPolicy(EndpointOptions ep, string role, ILogger logger)
    {
        if (ep.MaxRetries <= 0)
            return Policy.NoOpAsync<HttpResponseMessage>();

        return HttpPolicyExtensions
            .HandleTransientHttpError()                                   // HttpRequestException, 5xx, 408
            .OrResult(r => r.StatusCode == HttpStatusCode.TooManyRequests) // 429
            .WaitAndRetryAsync(
                ep.MaxRetries,
                (attempt, outcome, _) => SleepFor(attempt, outcome),
                (outcome, delay, attempt, _) =>
                {
                    logger.LogWarning(
                        "model call transient failure: role={Role} model={Model} status={Status} retry={Attempt}/{Max} in={Delay}ms",
                        role, ep.Model, (int?)outcome.Result?.StatusCode ?? 0, attempt, ep.MaxRetries, delay.TotalMilliseconds);
                    return Task.CompletedTask;
                });
    }

    private static TimeSpan SleepFor(int attempt, DelegateResult<HttpResponseMessage> outcome)
    {
        // Prefer the server's Retry-After when it gives one.
        var ra = outcome.Result?.Headers.RetryAfter;
        if (ra?.Delta is { } delta && delta > TimeSpan.Zero)
            return Cap(delta);
        if (ra?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) return Cap(wait);
        }
        // Otherwise exponential backoff: 0.5s, 1s, 2s, … capped at 10s.
        return Cap(TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1)));
    }

    private static TimeSpan Cap(TimeSpan t)
    {
        var cap = TimeSpan.FromSeconds(10);
        return t > cap ? cap : t;
    }
}
