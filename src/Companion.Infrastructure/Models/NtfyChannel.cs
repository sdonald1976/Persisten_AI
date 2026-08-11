using System.Net.Http;
using System.Text;
using Companion.Core;
using Companion.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Companion.Infrastructure.Models;

/// <summary>
/// Outbound channel over ntfy (https://ntfy.sh or self-hosted): one HTTP POST per message to the
/// configured topic URL; the ntfy phone/desktop app subscribed to that topic gets the push. Kept
/// deliberately thin — failures return false and the caller decides (outreach just waits for a
/// later check).
/// </summary>
public sealed class NtfyChannel : IOutboundChannel
{
    public const string HttpClientName = "companion-ntfy";

    private readonly IHttpClientFactory _http;
    private readonly OutreachOptions _options;
    private readonly ILogger<NtfyChannel> _logger;

    public NtfyChannel(IHttpClientFactory http, IOptions<OutreachOptions> options, ILogger<NtfyChannel> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public bool Configured => !string.IsNullOrWhiteSpace(_options.NtfyUrl);

    public async Task<bool> SendAsync(string title, string body, CancellationToken ct = default)
    {
        if (!Configured)
            return false;

        try
        {
            var client = _http.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, _options.NtfyUrl)
            {
                Content = new StringContent(body, Encoding.UTF8),
            };
            request.Headers.TryAddWithoutValidation("X-Title", title);
            request.Headers.TryAddWithoutValidation("X-Tags", "speech_balloon");
            if (!string.IsNullOrWhiteSpace(_options.AuthToken))
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _options.AuthToken.Trim());

            using var response = await client.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
                return true;

            _logger.LogWarning("ntfy returned {Status} for outreach.", (int)response.StatusCode);
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning("ntfy unreachable: {Reason}", ex.Message);
            return false;
        }
    }
}
