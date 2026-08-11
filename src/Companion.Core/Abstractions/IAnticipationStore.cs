using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>Persists anticipations (dated events the companion holds). User-scoped throughout.</summary>
public interface IAnticipationStore
{
    Task AddAsync(Anticipation anticipation, CancellationToken ct = default);

    /// <summary>Open anticipations (Pending or Encouraged), soonest event first.</summary>
    Task<IReadOnlyList<Anticipation>> GetOpenAsync(string userId, CancellationToken ct = default);

    /// <summary>Records that encouragement was voiced (Pending → Encouraged). No-op otherwise.</summary>
    Task MarkEncouragedAsync(string userId, Guid id, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Records that the follow-up was voiced — the arc is complete (→ FollowedUp).</summary>
    Task MarkFollowedUpAsync(string userId, Guid id, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Expires open anticipations whose event day is before <paramref name="eventBefore"/>. Returns how many.</summary>
    Task<int> ExpireStaleAsync(string userId, DateTimeOffset eventBefore, CancellationToken ct = default);
}
