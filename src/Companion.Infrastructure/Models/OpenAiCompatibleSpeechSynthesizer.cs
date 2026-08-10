using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Companion.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Companion.Infrastructure.Models;

/// <summary>
/// Text-to-speech via an OpenAI-compatible <c>/v1/audio/speech</c> endpoint. Point it at a dedicated
/// audio server (Speaches, LocalAI, or a Piper HTTP wrapper) — Ollama and LM Studio don't synthesize
/// speech. The response is raw audio bytes; the content type follows the requested format.
/// </summary>
public sealed class OpenAiCompatibleSpeechSynthesizer : ISpeechSynthesizer
{
    private const string DefaultFormat = "mp3";

    private readonly Func<HttpClient> _client;
    private readonly EndpointOptions _options;
    private readonly ILogger<OpenAiCompatibleSpeechSynthesizer> _logger;

    public OpenAiCompatibleSpeechSynthesizer(
        EndpointOptions options, IHttpClientFactory httpClientFactory, string clientName,
        ILogger<OpenAiCompatibleSpeechSynthesizer> logger)
    {
        _options = options;
        _logger = logger;
        _client = () => httpClientFactory.CreateClient(clientName);
    }

    public async Task<SpeechAudio> SynthesizeAsync(string text, string? voice = null, CancellationToken ct = default)
    {
        var format = string.IsNullOrWhiteSpace(_options.AudioFormat) ? DefaultFormat : _options.AudioFormat.Trim().ToLowerInvariant();
        var request = new SpeechRequest(
            Model: _options.Model,
            Input: text,
            Voice: voice ?? _options.Voice,
            ResponseFormat: format);

        var http = _client();
        using var response = await ProviderHttp.SendAsync(
            c => http.PostAsJsonAsync("audio/speech", request, ProviderHttp.JsonOptions, c), _options, "speech", _logger, ct);

        var bytes = await ProviderHttp.ReadCappedBytesAsync(response, _options.MaxResponseBytes, ct);

        // Trust the server's audio content type when it gives one; otherwise derive it from the format.
        var contentType = response.Content.Headers.ContentType?.MediaType is { } mt
            && mt.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
                ? mt
                : ContentTypeFor(format);

        return new SpeechAudio(bytes, contentType);
    }

    private static string ContentTypeFor(string format) => format switch
    {
        "mp3" => "audio/mpeg",
        "opus" => "audio/ogg",
        "aac" => "audio/aac",
        "flac" => "audio/flac",
        "wav" => "audio/wav",
        "pcm" => "audio/pcm",
        _ => "application/octet-stream",
    };

    private sealed record SpeechRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] string Input,
        [property: JsonPropertyName("voice")] string? Voice,
        [property: JsonPropertyName("response_format")] string ResponseFormat);
}
