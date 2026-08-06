using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Companion.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Companion.Infrastructure.Models;

/// <summary>
/// Chat completion against any OpenAI-compatible server — Ollama (<c>/v1</c>) or LM Studio
/// (<c>/v1</c>). A thin transport: ONE request per call, returning the reply text plus the signals
/// around it (<c>finish_reason</c>, token usage, model). It does not loop or decide whether to keep
/// going — that policy lives in <see cref="ReplyGenerator"/> — but it knows how to <em>format</em> a
/// continuation: when given a partial assistant answer it asks the model to resume from exactly
/// where that text stopped.
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

    public async Task<ChatCompletion> CompleteAsync(
        string systemPrompt, string userMessage, bool jsonMode = false,
        string? assistantPrefix = null, CancellationToken ct = default)
    {
        var http = _client();
        LogRequest(systemPrompt, userMessage, assistantPrefix, streaming: false);

        var request = ChatRequest.Build(
            _options, BuildMessages(systemPrompt, userMessage, assistantPrefix), stream: false, jsonMode: jsonMode);

        using var response = await ProviderHttp.SendAsync(
            c => http.PostAsJsonAsync("chat/completions", request, c), _options, "chat", _logger, ct);
        var body = await ProviderHttp.ReadCappedJsonAsync<ChatResponse>(response, _options.MaxResponseBytes, ct);
        var choice = body?.Choices?.FirstOrDefault();
        var text = choice?.Message?.Content ?? string.Empty;

        LogReply(text, choice?.FinishReason, body?.Usage);
        return new ChatCompletion
        {
            Text = text,
            FinishReason = choice?.FinishReason,
            Model = body?.Model ?? _options.Model,
            PromptTokens = body?.Usage?.PromptTokens,
            CompletionTokens = body?.Usage?.CompletionTokens,
        };
    }

    public async Task<ChatCompletion> StreamAsync(
        string systemPrompt, string userMessage, IProgress<string> sink,
        string? assistantPrefix = null, CancellationToken ct = default)
    {
        var http = _client();
        LogRequest(systemPrompt, userMessage, assistantPrefix, streaming: true);

        var request = ChatRequest.Build(
            _options, BuildMessages(systemPrompt, userMessage, assistantPrefix), stream: true);

        // Streaming reads the body incrementally (not buffered), so it uses the resilient send for
        // the initial request/headers but its own read loop for the SSE stream.
        var response = await ProviderHttp.SendAsync(
            c => http.SendAsync(
                new HttpRequestMessage(HttpMethod.Post, "chat/completions") { Content = JsonContent.Create(request) },
                HttpCompletionOption.ResponseHeadersRead, c),
            _options, "chat", _logger, ct);

        var accumulated = new StringBuilder();
        string? finishReason = null;
        string? model = null;
        Usage? usage = null;

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
                    var chunk = JsonSerializer.Deserialize<StreamChunk>(payload, Json);
                    var choice = chunk?.Choices?.FirstOrDefault();
                    content = choice?.Delta?.Content;
                    finishReason = choice?.FinishReason ?? finishReason;
                    model ??= chunk?.Model;
                    usage ??= chunk?.Usage;
                }
                catch (JsonException)
                {
                    // ignore keep-alive / non-JSON lines
                }

                if (!string.IsNullOrEmpty(content))
                {
                    accumulated.Append(content);
                    sink.Report(content);
                }
            }
        }

        var text = accumulated.ToString();
        LogReply(text, finishReason, usage);
        return new ChatCompletion
        {
            Text = text,
            FinishReason = finishReason,
            Model = model ?? _options.Model,
            PromptTokens = usage?.PromptTokens,
            CompletionTokens = usage?.CompletionTokens,
        };
    }

    /// <summary>Base messages, plus the continuation exchange when resuming a cut-off answer.</summary>
    private static object BuildMessages(string systemPrompt, string userMessage, string? assistantPrefix)
        => string.IsNullOrEmpty(assistantPrefix)
            ? new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage },
            }
            : new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage },
                new { role = "assistant", content = assistantPrefix },
                new { role = "user", content = ContinuePrompt },
            };

    private void LogRequest(string systemPrompt, string userMessage, string? assistantPrefix, bool streaming)
    {
        if (!_options.LogPayloads || !_logger.IsEnabled(LogLevel.Information))
            return;
        _logger.LogInformation(
            "LLM request → {Model} (stream={Stream}, continuation={Continuation})\n--- system ---\n{System}\n--- user ---\n{User}",
            _options.Model, streaming, assistantPrefix is not null, systemPrompt, userMessage);
    }

    private void LogReply(string text, string? finishReason, Usage? usage)
    {
        _logger.LogDebug(
            "LLM reply ← {Model}: finish_reason={Finish}, chars={Chars}, promptTokens={Prompt}, completionTokens={Completion}",
            _options.Model, finishReason ?? "(none)", text.Length, usage?.PromptTokens, usage?.CompletionTokens);

        if (_options.LogPayloads && _logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("LLM reply ← {Model} (finish_reason={Finish})\n{Text}", _options.Model, finishReason ?? "(none)", text);
    }

    private sealed record ChatResponse(
        [property: JsonPropertyName("choices")] List<Choice>? Choices,
        [property: JsonPropertyName("model")] string? Model,
        [property: JsonPropertyName("usage")] Usage? Usage);
    private sealed record Choice(
        [property: JsonPropertyName("message")] ChatMessage? Message,
        [property: JsonPropertyName("finish_reason")] string? FinishReason);
    private sealed record ChatMessage([property: JsonPropertyName("content")] string? Content);

    private sealed record StreamChunk(
        [property: JsonPropertyName("choices")] List<StreamChoice>? Choices,
        [property: JsonPropertyName("model")] string? Model,
        [property: JsonPropertyName("usage")] Usage? Usage);
    private sealed record StreamChoice(
        [property: JsonPropertyName("delta")] Delta? Delta,
        [property: JsonPropertyName("finish_reason")] string? FinishReason);
    private sealed record Delta([property: JsonPropertyName("content")] string? Content);

    private sealed record Usage(
        [property: JsonPropertyName("prompt_tokens")] int? PromptTokens,
        [property: JsonPropertyName("completion_tokens")] int? CompletionTokens);
}
