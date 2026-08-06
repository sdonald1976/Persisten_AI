using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Companion.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Companion.Infrastructure.Models;

/// <summary>
/// Chat completion against any OpenAI-compatible server — Ollama (<c>/v1</c>) or LM Studio
/// (<c>/v1</c>). Sends the assembled context as the system prompt and the user's message, and
/// returns the assistant's reply.
///
/// Completion detection: the server reports WHY generation stopped (<c>finish_reason</c>).
/// <c>"stop"</c> means the model finished; <c>"length"</c> means it hit the output-token limit
/// mid-answer. On <c>"length"</c> this adapter automatically asks the model to continue exactly
/// where it stopped and stitches the parts together (bounded by
/// <see cref="EndpointOptions.MaxContinuations"/>), so a long task completes in one turn instead
/// of the user having to say "keep going".
/// </summary>
public sealed class OpenAiCompatibleChatModel : IChatModel
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private const string ContinuePrompt =
        "Continue your previous answer from exactly where it stopped. Do not repeat anything you " +
        "already wrote, do not summarize, and do not add any preamble — output only the continuation.";

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
        var http = _client();
        var buffer = new StringBuilder();

        for (var round = 0; ; round++)
        {
            var request = ChatRequest.Build(
                _options, BuildMessages(systemPrompt, userMessage, round == 0 ? null : buffer.ToString()),
                stream: false, jsonMode: jsonMode);

            using var response = await ProviderHttp.SendAsync(
                c => http.PostAsJsonAsync("chat/completions", request, c), _options, "chat", _logger, ct);
            var body = await ProviderHttp.ReadCappedJsonAsync<ChatResponse>(response, _options.MaxResponseBytes, ct);
            var choice = body?.Choices?.FirstOrDefault();
            buffer.Append(choice?.Message?.Content);

            // Structured (JSON-mode) output is never stitched — half a JSON document is useless,
            // and the extraction path treats an unparseable reply as "no candidates" anyway.
            if (!ShouldContinue(choice?.FinishReason, round, jsonMode))
                break;
        }

        var text = buffer.ToString();
        return string.IsNullOrWhiteSpace(text) ? "(the model returned an empty response)" : text.Trim();
    }

    public async IAsyncEnumerable<string> StreamAsync(
        string systemPrompt, string userMessage, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var http = _client();
        var accumulated = new StringBuilder();

        for (var round = 0; ; round++)
        {
            var request = ChatRequest.Build(
                _options, BuildMessages(systemPrompt, userMessage, round == 0 ? null : accumulated.ToString()),
                stream: true);

            // Streaming reads the body incrementally (not buffered), so it uses the resilient send
            // for the initial request/headers but its own read loop for the SSE stream.
            var response = await ProviderHttp.SendAsync(
                c => http.SendAsync(
                    new HttpRequestMessage(HttpMethod.Post, "chat/completions") { Content = JsonContent.Create(request) },
                    HttpCompletionOption.ResponseHeadersRead, c),
                _options, "chat", _logger, ct);

            string? finishReason = null;
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
                        var choice = JsonSerializer.Deserialize<StreamChunk>(payload, Json)?.Choices?.FirstOrDefault();
                        content = choice?.Delta?.Content;
                        finishReason = choice?.FinishReason ?? finishReason;
                    }
                    catch (JsonException)
                    {
                        // ignore keep-alive / non-JSON lines
                    }

                    if (!string.IsNullOrEmpty(content))
                    {
                        accumulated.Append(content);
                        yield return content;
                    }
                }
            }

            if (!ShouldContinue(finishReason, round, jsonMode: false))
                yield break;
        }
    }

    /// <summary>Base messages, plus the continuation exchange when resuming a cut-off answer.</summary>
    private static object BuildMessages(string systemPrompt, string userMessage, string? partialSoFar)
        => partialSoFar is null
            ? new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage },
            }
            : new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage },
                new { role = "assistant", content = partialSoFar },
                new { role = "user", content = ContinuePrompt },
            };

    private bool ShouldContinue(string? finishReason, int round, bool jsonMode)
    {
        var truncated = string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase);
        if (!truncated || jsonMode || !_options.AutoContinue)
            return false;

        if (round >= _options.MaxContinuations)
        {
            _logger.LogWarning(
                "Reply from {Model} still truncated after {Rounds} auto-continuations; stopping (raise MaxTokens or MaxContinuations).",
                _options.Model, round + 1);
            return false;
        }

        _logger.LogDebug("Reply from {Model} was cut off (finish_reason=length); auto-continuing (round {Round}).",
            _options.Model, round + 1);
        return true;
    }

    private sealed record ChatResponse([property: JsonPropertyName("choices")] List<Choice>? Choices);
    private sealed record Choice(
        [property: JsonPropertyName("message")] ChatMessage? Message,
        [property: JsonPropertyName("finish_reason")] string? FinishReason);
    private sealed record ChatMessage([property: JsonPropertyName("content")] string? Content);

    private sealed record StreamChunk([property: JsonPropertyName("choices")] List<StreamChoice>? Choices);
    private sealed record StreamChoice(
        [property: JsonPropertyName("delta")] Delta? Delta,
        [property: JsonPropertyName("finish_reason")] string? FinishReason);
    private sealed record Delta([property: JsonPropertyName("content")] string? Content);
}
