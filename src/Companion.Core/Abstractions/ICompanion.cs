using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>Orchestrates a single conversation turn end-to-end and returns a diagnostic trace.</summary>
public interface ICompanion
{
    /// <param name="tokenSink">
    /// When provided, the assistant reply is streamed and each chunk is reported here as it
    /// arrives. The full reply is still stored and returned in the trace either way.
    /// </param>
    Task<TurnTrace> RespondAsync(
        string userId,
        Guid conversationId,
        string userMessage,
        IProgress<string>? tokenSink = null,
        CancellationToken ct = default);
}
