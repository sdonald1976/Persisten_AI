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
public sealed class OpenAiCompatibleChatModel : IChatModel, IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly EndpointOptions _options;
    private readonly ILogger<OpenAiCompatibleChatModel> _logger;

    public OpenAiCompatibleChatModel(EndpointOptions options, ILogger<OpenAiCompatibleChatModel> logger)
    {
        _options = options;
        _logger = logger;
        _http = HttpClientFactory.Create(options);
    }

    /// <summary>The configured model name (which model this instance talks to).</summary>
    public string ModelName => _options.Model;

    public async Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct = default)
    {
        var request = ChatRequest.Build(_options, new[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user", content = userMessage },
        }, stream: false);

        try
        {
            using var response = await _http.PostAsJsonAsync("chat/completions", request, ct);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<ChatResponse>(Json, ct);
            var content = body?.Choices?.FirstOrDefault()?.Message?.Content;
            return string.IsNullOrWhiteSpace(content) ? "(the model returned an empty response)" : content.Trim();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "Chat request to {BaseUrl} (model {Model}) failed", _options.BaseUrl, _options.Model);
            throw new InvalidOperationException(
                $"Couldn't reach the chat model at {_options.BaseUrl} (model '{_options.Model}'). " +
                "Is the server running and the model pulled/loaded?", ex);
        }
    }

    public async IAsyncEnumerable<string> StreamAsync(
        string systemPrompt, string userMessage, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var request = ChatRequest.Build(_options, new[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user", content = userMessage },
        }, stream: true);

        HttpResponseMessage response;
        try
        {
            var message = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
            {
                Content = JsonContent.Create(request),
            };
            response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "Streaming chat request to {BaseUrl} (model {Model}) failed", _options.BaseUrl, _options.Model);
            throw new InvalidOperationException(
                $"Couldn't reach the chat model at {_options.BaseUrl} (model '{_options.Model}'). " +
                "Is the server running and the model pulled/loaded?", ex);
        }

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

    public void Dispose() => _http.Dispose();

    private sealed record ChatResponse([property: JsonPropertyName("choices")] List<Choice>? Choices);
    private sealed record Choice([property: JsonPropertyName("message")] ChatMessage? Message);
    private sealed record ChatMessage([property: JsonPropertyName("content")] string? Content);

    private sealed record StreamChunk([property: JsonPropertyName("choices")] List<StreamChoice>? Choices);
    private sealed record StreamChoice([property: JsonPropertyName("delta")] Delta? Delta);
    private sealed record Delta([property: JsonPropertyName("content")] string? Content);
}
