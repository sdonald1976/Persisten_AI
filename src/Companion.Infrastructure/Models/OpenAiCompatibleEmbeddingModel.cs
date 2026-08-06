using System.Net.Http.Json;
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
    private readonly HttpClient _http;
    private readonly EndpointOptions _options;
    private readonly ILogger<OpenAiCompatibleEmbeddingModel> _logger;

    public OpenAiCompatibleEmbeddingModel(EndpointOptions options, ILogger<OpenAiCompatibleEmbeddingModel> logger)
        : this(options, HttpClientFactory.Create(options), logger) { }

    /// <summary>Test seam: supply a pre-built <see cref="HttpClient"/> (e.g. over a mock handler).</summary>
    internal OpenAiCompatibleEmbeddingModel(EndpointOptions options, HttpClient http, ILogger<OpenAiCompatibleEmbeddingModel> logger)
    {
        _options = options;
        _logger = logger;
        _http = http;
    }

    public int Dimensions => _options.Dimensions; // informational; real length comes from the model

    /// <summary>The configured embedding model name.</summary>
    public string ModelName => _options.Model;

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var request = new { model = _options.Model, input = text };

        using var response = await ProviderHttp.SendAsync(
            c => _http.PostAsJsonAsync("embeddings", request, c), _options, "embeddings", _logger, ct);
        var body = await ProviderHttp.ReadCappedJsonAsync<EmbeddingResponse>(response, _options.MaxResponseBytes, ct);

        var vector = body?.Data?.FirstOrDefault()?.Embedding;
        if (vector is null || vector.Length == 0)
            throw new ModelProviderException(
                $"The embedding model at {_options.BaseUrl} (model '{_options.Model}') returned no vector.");

        // Guard against a silently-swapped model: a wrong vector length breaks all similarity
        // math and would corrupt the index. Reject it loudly instead of storing mismatched vectors.
        if (_options.Dimensions > 0 && vector.Length != _options.Dimensions)
            throw new ModelProviderException(
                $"Embedding dimension mismatch from model '{_options.Model}': got {vector.Length}, " +
                $"configured {_options.Dimensions}. Re-embed after changing the embedding model, " +
                "or fix Models.Embeddings.Dimensions.");

        return vector;
    }

    public void Dispose() => _http.Dispose();

    private sealed record EmbeddingResponse([property: JsonPropertyName("data")] List<EmbeddingData>? Data);
    private sealed record EmbeddingData([property: JsonPropertyName("embedding")] float[]? Embedding);
}
