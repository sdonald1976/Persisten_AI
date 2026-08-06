using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Companion.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Companion.Infrastructure.Models;

/// <summary>
/// Chat completion against any OpenAI-compatible server — Ollama (<c>/v1</c>) or LM Studio
/// (<c>/v1</c>). Sends the assembled context as the system prompt and the user's message, and
/// returns the assistant's reply. Point it at a base URL and model via configuration.
/// </summary>
public sealed class OpenAiCompatibleChatModel : IChatModel
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly Func<HttpClient> _client;
    private readonly EndpointOptions _options;
    private readonly ILogger<OpenAiCompatibleChatModel> _logger;

    public OpenAiCompatibleChatModel(
        EndpointOptions options, IHttpClientFactory httpClientFactory, string clientName,
        ILogger<OpenAiCompatibleChatModel> logger)
        : this(options, () => httpClientFactory.CreateClient(clientName), logger) { }

    /// <summary>Test seam: supply the client factory directly (e.g. a client over a mock handler).</summary>
    internal OpenAiCompatibleChatModel(EndpointOptions options, Func<HttpClient> client, ILogger<OpenAiCompatibleChatModel> logger)
    {
        _options = options;
        _logger = logger;
        _client = client;
    }

    /// <summary>The configured model name (which model this instance talks to).</summary>
    public string ModelName => _options.Model;

    public async Task<string> CompleteAsync(
        string systemPrompt, string userMessage, bool jsonMode = false, CancellationToken ct = default)
    {
        var request = ChatRequest.Build(_options, new[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user", content = userMessage },
        }, stream: false, jsonMode: jsonMode);

        var http = _client();
        using var response = await ProviderHttp.SendAsync(
            c => http.PostAsJsonAsync("chat/completions", request, c), _options, "chat", _logger, ct);
        var body = await ProviderHttp.ReadCappedJsonAsync<ChatResponse>(response, _options.MaxResponseBytes, ct);
        var content = body?.Choices?.FirstOrDefault()?.Message?.Content;
        return string.IsNullOrWhiteSpace(content) ? "(the model returned an empty response)" : content.Trim();
    }

    public async IAsyncEnumerable<string> StreamAsync(
        string systemPrompt, string userMessage, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var request = ChatRequest.Build(_options, new[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user", content = userMessage },
        }, stream: true);

        // Streaming reads the body incrementally (not buffered), so it uses the resilient send for
        // the initial request/headers but its own read loop for the SSE stream.
        var http = _client();
        var response = await ProviderHttp.SendAsync(
            c => http.SendAsync(
                new HttpRequestMessage(HttpMethod.Post, "chat/completions") { Content = JsonContent.Create(request) },
                HttpCompletionOption.ResponseHeadersRead, c),
            _options, "chat", _logger, ct);

        using (response)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync(ct);
                if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.Ordinal))
                    continue;

                var payload = line["data:".Length..].Trim();
                if (payload == "[DONE]")
                    break;

                string? content = null;
                try
                {
                    content = JsonSerializer.Deserialize<StreamChunk>(payload, Json)?.Choices?.FirstOrDefault()?.Delta?.Content;
                }
                catch (JsonException)
                {
                    // ignore keep-alive / non-JSON lines
                }

                if (!string.IsNullOrEmpty(content))
                    yield return content;
            }
        }
    }

    private sealed record ChatResponse([property: JsonPropertyName("choices")] List<Choice>? Choices);
    private sealed record Choice([property: JsonPropertyName("message")] ChatMessage? Message);
    private sealed record ChatMessage([property: JsonPropertyName("content")] string? Content);

    private sealed record StreamChunk([property: JsonPropertyName("choices")] List<StreamChoice>? Choices);
    private sealed record StreamChoice([property: JsonPropertyName("delta")] Delta? Delta);
    private sealed record Delta([property: JsonPropertyName("content")] string? Content);
}
