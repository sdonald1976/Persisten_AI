using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Companion.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Companion.Infrastructure.Models;

/// <summary>
/// Speech-to-text via an OpenAI-compatible <c>/v1/audio/transcriptions</c> endpoint (Whisper).
/// Point it at a dedicated audio server such as whisper.cpp's server, faster-whisper-server, or
/// LocalAI — Ollama and LM Studio don't serve audio.
/// </summary>
public sealed class OpenAiCompatibleTranscriber : ITranscriber, IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly EndpointOptions _options;
    private readonly ILogger<OpenAiCompatibleTranscriber> _logger;

    public OpenAiCompatibleTranscriber(EndpointOptions options, ILogger<OpenAiCompatibleTranscriber> logger)
    {
        _options = options;
        _logger = logger;
        _http = HttpClientFactory.Create(options);
    }

    public async Task<string> TranscribeAsync(Stream audio, string fileName, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        var fileContent = new StreamContent(audio);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", fileName);
        form.Add(new StringContent(_options.Model), "model");

        try
        {
            using var response = await _http.PostAsync("audio/transcriptions", form, ct);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<TranscriptionResponse>(Json, ct);
            return body?.Text?.Trim() ?? string.Empty;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "Transcription request to {BaseUrl} (model {Model}) failed", _options.BaseUrl, _options.Model);
            throw new InvalidOperationException(
                $"Couldn't reach the transcription server at {_options.BaseUrl} (model '{_options.Model}'). " +
                "Whisper needs a separate audio server (e.g. whisper.cpp, faster-whisper-server, LocalAI).", ex);
        }
    }

    public void Dispose() => _http.Dispose();

    private sealed record TranscriptionResponse([property: JsonPropertyName("text")] string? Text);
}
