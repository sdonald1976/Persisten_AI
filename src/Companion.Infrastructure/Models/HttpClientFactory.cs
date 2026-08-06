using System.Net.Http.Headers;

namespace Companion.Infrastructure.Models;

/// <summary>
/// Builds an <see cref="HttpClient"/> for an OpenAI-compatible endpoint over a single shared,
/// pooled <see cref="SocketsHttpHandler"/> (connection reuse + periodic DNS refresh, avoiding the
/// socket-exhaustion and stale-DNS pitfalls of a per-instance raw handler). Providers are
/// singletons, so each keeps one client for its endpoint's lifetime.
/// </summary>
internal static class HttpClientFactory
{
    // One handler process-wide; pooling and DNS rotation are handled here, not per client.
    private static readonly SocketsHttpHandler SharedHandler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        MaxConnectionsPerServer = 16,
    };

    public static HttpClient Create(EndpointOptions options)
    {
        // BaseAddress must end with "/" so relative paths like "chat/completions" combine correctly.
        var baseUrl = options.BaseUrl.EndsWith('/') ? options.BaseUrl : options.BaseUrl + "/";

        var http = new HttpClient(SharedHandler, disposeHandler: false)
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds)),
            // Secondary guard for buffered reads; the primary cap is ProviderHttp.ReadCappedJsonAsync.
            MaxResponseContentBufferSize = Math.Max(1, options.MaxResponseBytes),
        };

        if (!string.IsNullOrWhiteSpace(options.ApiKey))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        return http;
    }
}
