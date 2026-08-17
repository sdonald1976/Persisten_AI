namespace Companion.Core.Abstractions;

/// <summary>
/// Chat-completion transport. ONE call in, ONE reply out — the deciding of whether to keep going
/// (auto-continuation, completion checks) lives above this in <see cref="IReplyGenerator"/>, not
/// here, so a provider adapter stays a thin, testable request/response.
/// </summary>
public interface IChatModel
{
    /// <summary>
    /// Generates an assistant reply for a rendered system/context prompt and the user message.
    /// <paramref name="format"/> asks the provider (where supported) for JSON, optionally
    /// constrained to a schema — used by structured tasks like memory extraction. Output is still
    /// treated as untrusted and validated by the caller: a schema bounds the shape, not the truth.
    ///
    /// When <paramref name="assistantPrefix"/> is supplied, it is the assistant text produced so
    /// far and the model is asked to resume from exactly where that text left off (used to continue
    /// a cut-off or unfinished reply). The returned <see cref="ChatCompletion.Text"/> is only the
    /// new continuation, not the prefix.
    /// </summary>
    Task<ChatCompletion> CompleteAsync(
        string systemPrompt, string userMessage, ResponseFormat? format = null,
        string? assistantPrefix = null, CancellationToken ct = default);

    /// <summary>
    /// Streams the reply to <paramref name="sink"/> chunk-by-chunk (concatenated, the chunks equal
    /// <see cref="ChatCompletion.Text"/>) and returns the full text plus generation metadata once
    /// the stream ends. <paramref name="assistantPrefix"/> works as in <see cref="CompleteAsync"/>.
    /// </summary>
    Task<ChatCompletion> StreamAsync(
        string systemPrompt, string userMessage, IProgress<string> sink,
        string? assistantPrefix = null, CancellationToken ct = default);
}
