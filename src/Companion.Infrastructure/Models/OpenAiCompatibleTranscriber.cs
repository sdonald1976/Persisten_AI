using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using Companion.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Companion.Infrastructure.Models;

/// <summary>
/// Speech-to-text via an OpenAI-compatible <c>/v1/audio/transcriptions</c> endpoint (Whisper).
/// Point it at a dedicated audio server such as whisper.cpp's server, faster-whisper-server, or
/// LocalAI — Ollama and LM Studio don't serve audio.
/// </summary>
public sealed class OpenAiCompatibleTranscriber : ITranscriber
{
    private readonly Func<HttpClient> _client;
    private readonly EndpointOptions _options;
    private readonly ILogger<OpenAiCompatibleTranscriber> _logger;

    public OpenAiCompatibleTranscriber(
        EndpointOptions options, IHttpClientFactory httpClientFactory, string clientName,
        ILogger<OpenAiCompatibleTranscriber> logger)
    {
        _options = options;
        _logger = logger;
        _client = () => httpClientFactory.CreateClient(clientName);
    }

    public async Task<string> TranscribeAsync(Stream audio, string fileName, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        var fileContent = new StreamContent(audio);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", fileName);
        form.Add(new StringContent(_options.Model), "model");

        var http = _client();
        using var response = await ProviderHttp.SendAsync(
            c => http.PostAsync("audio/transcriptions", form, c), _options, "transcription", _logger, ct);
        var body = await ProviderHttp.ReadCappedJsonAsync<TranscriptionResponse>(response, _options.MaxResponseBytes, ct);
        return body?.Text?.Trim() ?? string.Empty;
    }

    private sealed record TranscriptionResponse([property: JsonPropertyName("text")] string? Text);
}
