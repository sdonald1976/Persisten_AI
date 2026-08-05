using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>Orchestrates a single conversation turn end-to-end and returns a diagnostic trace.</summary>
public interface ICompanion
{
    Task<TurnTrace> RespondAsync(
        string userId, Guid conversationId, string userMessage, CancellationToken ct = default);
}
