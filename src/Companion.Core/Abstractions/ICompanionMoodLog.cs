using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>
/// The append-only, versioned log of the companion's mood transitions (Source 4b). User-scoped
/// like every store. Its whole purpose is to give her mood a resolvable identity: a vote cites
/// a transition id, and that id can be looked up forever.
/// </summary>
public interface ICompanionMoodLog
{
    /// <summary>
    /// Appends one transition, assigning the next version for this user. Concurrent nudges are
    /// safe: the (UserId, Version) uniqueness constraint makes a lost update a conflict rather
    /// than silent corruption, and the append retries onto the newly-current value. Returns the
    /// row that was written.
    /// </summary>
    Task<CompanionMoodTransition> AppendAsync(
        string userId, double previousSpirits, double newSpirits, double appliedValence,
        DateTimeOffset occurredAt, Guid? sourceEvidenceEventId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Purges the applied valence of every transition produced by one of these forgotten
    /// evidence events, by EXACT id. What survives is her own state trajectory and the
    /// version chain; what goes is the stored reading of the user's moment.
    /// </summary>
    Task<int> ForgetByEvidenceAsync(
        string userId, IReadOnlyCollection<Guid> evidenceEventIds, DateTimeOffset now,
        CancellationToken ct = default);

    /// <summary>The newest transition for this user, or null when her mood has never moved.</summary>
    Task<CompanionMoodTransition?> GetLatestAsync(string userId, CancellationToken ct = default);

    /// <summary>Every transition, oldest first — the substrate deterministic replay reads.</summary>
    Task<IReadOnlyList<CompanionMoodTransition>> GetHistoryAsync(
        string userId, CancellationToken ct = default);
}
