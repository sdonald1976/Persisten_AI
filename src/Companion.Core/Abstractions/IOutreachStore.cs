using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>Persists the log of self-initiated messages. User-scoped like every store.</summary>
public interface IOutreachStore
{
    Task AddAsync(OutboundMessage message, CancellationToken ct = default);

    /// <summary>When the companion last reached out, or null if it never has. Enforces the budget.</summary>
    Task<DateTimeOffset?> GetLastSentAtAsync(string userId, CancellationToken ct = default);

    /// <summary>Recent outreaches, newest first, capped at <paramref name="count"/>.</summary>
    Task<IReadOnlyList<OutboundMessage>> GetRecentAsync(string userId, int count, CancellationToken ct = default);
}
