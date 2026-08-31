using System.Diagnostics;
using Companion.Core.Abstractions;
using Companion.Core.Domain;

namespace Companion.Infrastructure.Models;

/// <summary>
/// Wraps any chat model with call telemetry: role, model, duration, sizes, token usage, and
/// outcome go to the diagnostics store; the call itself passes through untouched (failures are
/// recorded and rethrown). One decorator at the seam covers every role — conversation,
/// extraction, summarizer, reranker, safety, task-auditor — with no per-caller code.
/// </summary>
public sealed class LoggingChatModel : IChatModel
{
    private readonly string _role;
    private readonly IDiagnosticsStore _diagnostics;
    private readonly TimeProvider _clock;

    public LoggingChatModel(IChatModel inner, string role, IDiagnosticsStore diagnostics, TimeProvider clock)
    {
        Inner = inner;
        _role = role;
        _diagnostics = diagnostics;
        _clock = clock;
    }

    /// <summary>The wrapped model (tests unwrap to assert per-role configuration).</summary>
    public IChatModel Inner { get; }

    public Task<ChatCompletion> CompleteAsync(
        string systemPrompt, string userMessage, ResponseFormat? format = null,
        string? assistantPrefix = null, CancellationToken ct = default)
        => MeasureAsync("complete", systemPrompt, userMessage,
            () => Inner.CompleteAsync(systemPrompt, userMessage, format, assistantPrefix, ct), ct);

    public Task<ChatCompletion> StreamAsync(
        string systemPrompt, string userMessage, IProgress<string> sink,
        string? assistantPrefix = null, CancellationToken ct = default)
        => MeasureAsync("stream", systemPrompt, userMessage,
            () => Inner.StreamAsync(systemPrompt, userMessage, sink, assistantPrefix, ct), ct);

    private async Task<ChatCompletion> MeasureAsync(
        string operation, string systemPrompt, string userMessage,
        Func<Task<ChatCompletion>> call, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var completion = await call();
            stopwatch.Stop();
            await _diagnostics.RecordModelCallAsync(new ModelCallRecord
            {
                Id = Guid.NewGuid(),
                Role = _role,
                Operation = operation,
                Model = completion.Model,
                Ok = true,
                DurationMs = stopwatch.ElapsedMilliseconds,
                PromptChars = systemPrompt.Length + userMessage.Length,
                CompletionChars = completion.Text.Length,
                PromptTokens = completion.PromptTokens,
                CompletionTokens = completion.CompletionTokens,
                Timestamp = _clock.GetUtcNow(),
            }, CancellationToken.None);
            Companion.Core.Abstractions.ModelCallScope.Record(_role, completion.Model, ok: true);
            return completion;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            await _diagnostics.RecordModelCallAsync(new ModelCallRecord
            {
                Id = Guid.NewGuid(),
                Role = _role,
                Operation = operation,
                Ok = false,
                Error = ex is OperationCanceledException ? "Canceled" : ex.GetType().Name,
                DurationMs = stopwatch.ElapsedMilliseconds,
                PromptChars = systemPrompt.Length + userMessage.Length,
                Timestamp = _clock.GetUtcNow(),
            }, CancellationToken.None);
            Companion.Core.Abstractions.ModelCallScope.Record(_role, model: null, ok: false);
            throw;
        }
    }
}

/// <summary>Telemetry wrapper for the embedding model — same pattern, "embed" operation.</summary>
public sealed class LoggingEmbeddingModel : IEmbeddingModel
{
    private readonly IDiagnosticsStore _diagnostics;
    private readonly TimeProvider _clock;

    public LoggingEmbeddingModel(IEmbeddingModel inner, IDiagnosticsStore diagnostics, TimeProvider clock)
    {
        Inner = inner;
        _diagnostics = diagnostics;
        _clock = clock;
    }

    /// <summary>The wrapped model (tests unwrap to assert configuration).</summary>
    public IEmbeddingModel Inner { get; }

    public int Dimensions => Inner.Dimensions;

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var vector = await Inner.EmbedAsync(text, ct);
            stopwatch.Stop();
            await _diagnostics.RecordModelCallAsync(new ModelCallRecord
            {
                Id = Guid.NewGuid(), Role = "embeddings", Operation = "embed", Ok = true,
                DurationMs = stopwatch.ElapsedMilliseconds, PromptChars = text.Length,
                Timestamp = _clock.GetUtcNow(),
            }, CancellationToken.None);
            return vector;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            await _diagnostics.RecordModelCallAsync(new ModelCallRecord
            {
                Id = Guid.NewGuid(), Role = "embeddings", Operation = "embed", Ok = false,
                Error = ex is OperationCanceledException ? "Canceled" : ex.GetType().Name,
                DurationMs = stopwatch.ElapsedMilliseconds, PromptChars = text.Length,
                Timestamp = _clock.GetUtcNow(),
            }, CancellationToken.None);
            throw;
        }
    }
}
