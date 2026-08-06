using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Companion.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Companion.Infrastructure.Models;

/// <summary>
/// Vision via an OpenAI-compatible multimodal endpoint (e.g. Ollama llama3.2-vision, or an LM
/// Studio vision GGUF). Sends the prompt plus one or more images as a content-array message and
/// returns the model's description.
/// </summary>
public sealed class OpenAiCompatibleVisionModel : IVisionModel
{
    private readonly Func<HttpClient> _client;
    private readonly EndpointOptions _options;
    private readonly ILogger<OpenAiCompatibleVisionModel> _logger;

    public OpenAiCompatibleVisionModel(
        EndpointOptions options, IHttpClientFactory httpClientFactory, string clientName,
        ILogger<OpenAiCompatibleVisionModel> logger)
    {
        _options = options;
        _logger = logger;
        _client = () => httpClientFactory.CreateClient(clientName);
    }

    public string ModelName => _options.Model;

    public async Task<string> DescribeAsync(
        string prompt, IReadOnlyList<ImageInput> images, CancellationToken ct = default)
    {
        var parts = new List<object> { new { type = "text", text = prompt } };
        foreach (var image in images)
        {
            var dataUrl = $"data:{image.MediaType};base64,{Convert.ToBase64String(image.Data)}";
            parts.Add(new { type = "image_url", image_url = new { url = dataUrl } });
        }

        var request = ChatRequest.Build(
            _options, new object[] { new { role = "user", content = parts } }, stream: false);

        var http = _client();
        using var response = await ProviderHttp.SendAsync(
            c => http.PostAsJsonAsync("chat/completions", request, c), _options, "vision", _logger, ct);
        var body = await ProviderHttp.ReadCappedJsonAsync<VisionResponse>(response, _options.MaxResponseBytes, ct);
        var content = body?.Choices?.FirstOrDefault()?.Message?.Content;
        return string.IsNullOrWhiteSpace(content) ? "(the vision model returned no description)" : content.Trim();
    }

    private sealed record VisionResponse([property: JsonPropertyName("choices")] List<Choice>? Choices);
    private sealed record Choice([property: JsonPropertyName("message")] Msg? Message);
    private sealed record Msg([property: JsonPropertyName("content")] string? Content);
}
