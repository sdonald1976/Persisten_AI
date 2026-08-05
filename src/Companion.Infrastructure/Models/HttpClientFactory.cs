using System.Net.Http.Headers;

namespace Companion.Infrastructure.Models;

/// <summary>Builds a long-lived <see cref="HttpClient"/> configured for an OpenAI-compatible endpoint.</summary>
internal static class HttpClientFactory
{
    public static HttpClient Create(EndpointOptions options)
    {
        // BaseAddress must end with "/" so relative paths like "chat/completions" combine correctly.
        var baseUrl = options.BaseUrl.EndsWith('/') ? options.BaseUrl : options.BaseUrl + "/";

        var http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds)),
        };

        if (!string.IsNullOrWhiteSpace(options.ApiKey))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        return http;
    }
}
