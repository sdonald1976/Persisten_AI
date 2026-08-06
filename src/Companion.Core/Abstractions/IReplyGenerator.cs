namespace Companion.Core.Abstractions;

/// <summary>
/// Produces the assistant's reply for a turn and owns the decision of <em>when to keep going</em>.
/// It calls the <see cref="IChatModel"/> transport once per round and continues when the reply was
/// cut off by the token limit (<c>finish_reason: "length"</c>) or when a semantic completion check
/// (<see cref="ICompletionJudge"/>) says a self-stopped reply isn't actually finished — feeding the
/// text produced so far back in each round so the model resumes the <em>same</em> task instead of
/// starting over. Bounded so it can never run away.
/// </summary>
public interface IReplyGenerator
{
    /// <summary>
    /// Generate a reply. When <paramref name="sink"/> is non-null the reply streams to it as it is
    /// produced (across continuation rounds); either way the full text and generation metadata are
    /// returned once the reply is complete.
    /// </summary>
    Task<ChatCompletion> GenerateAsync(
        string systemPrompt, string userMessage, IProgress<string>? sink = null, CancellationToken ct = default);
}
