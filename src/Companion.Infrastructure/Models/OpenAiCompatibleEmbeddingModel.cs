using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Companion.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Companion.Infrastructure.Models;

/// <summary>
/// Embeddings against any OpenAI-compatible server (Ollama / LM Studio). Requires an embedding
/// model to be available at the endpoint (e.g. "nomic-embed-text").
/// Note: if you change the embedding model, re-seed / re-embed — vectors of different lengths
/// won't compare, so old memories would stop matching.
/// </summary>
public sealed class OpenAiCompatibleEmbeddingModel : IEmbeddingModel, IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly EndpointOptions _options;
    private readonly ILogger<OpenAiCompatibleEmbeddingModel> _logger;

    public OpenAiCompatibleEmbeddingModel(EndpointOptions options, ILogger<OpenAiCompatibleEmbeddingModel> logger)
    {
        _options = options;
        _logger = logger;
        _http = HttpClientFactory.Create(options);
    }

    public int Dimensions => _options.Dimensions; // informational; real length comes from the model

    /// <summary>The configured embedding model name.</summary>
    public string ModelName => _options.Model;

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var request = new { model = _options.Model, input = text };

        try
        {
            using var response = await _http.PostAsJsonAsync("embeddings", request, ct);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(Json, ct);
            var vector = body?.Data?.FirstOrDefault()?.Embedding;
            if (vector is null || vector.Length == 0)
                throw new InvalidOperationException("The embedding model returned no vector.");
            return vector;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "Embedding request to {BaseUrl} (model {Model}) failed", _options.BaseUrl, _options.Model);
            throw new InvalidOperationException(
                $"Couldn't reach the embedding model at {_options.BaseUrl} (model '{_options.Model}'). " +
                "Is the server running and an embedding model available?", ex);
        }
    }

    public void Dispose() => _http.Dispose();

    private sealed record EmbeddingResponse([property: JsonPropertyName("data")] List<EmbeddingData>? Data);
    private sealed record EmbeddingData([property: JsonPropertyName("embedding")] float[]? Embedding);
}
