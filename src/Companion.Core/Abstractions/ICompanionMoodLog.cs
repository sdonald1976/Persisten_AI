using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>
/// The append-only, versioned log of the companion's mood transitions (Source 4b). User-scoped
/// like every store. Its whole purpose is to give her mood a resolvable identity: a vote cites
/// a transition id, and that id can be looked up forever.
/// </summary>
/// <summary>What a compaction did. Content-safe: counts, versions, and a reason token.</summary>
/// <param name="Compacted">False when nothing matched — no forgotten transition, nothing to do.</param>
/// <param name="RowsRemoved">How many transitions were deleted, including the forgotten ones.</param>
/// <param name="BaselineVersion">Version of the opaque baseline written in their place.</param>
public sealed record MoodCompactionResult(bool Compacted, int RowsRemoved, int? BaselineVersion);

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
    /// PRIVACY COMPACTION (contract decision, 2026-08-25). Forgetting an evidence removes the
    /// record of the moment and every row from which its valence could be reconstructed — but
    /// it does NOT rewind her present mood, because being affected happened whether or not the
    /// record of it survives.
    ///
    /// Redacting a transition in place was not enough: her spirits trajectory is a
    /// deterministic function of the valences that moved it, so the NEIGHBOURING rows bracket
    /// a nulled one exactly. Compaction is therefore total up to the boundary — every
    /// transition at or before the newest forgotten one is deleted and replaced by a single
    /// opaque BASELINE carrying <paramref name="currentSpirits"/> and nothing else. Later
    /// transitions continue from it.
    ///
    /// The cost is deliberate and diagnosed rather than hidden: exact replay across a baseline
    /// is unavailable, because the rows it would need are exactly the rows that leaked.
    /// </summary>
    Task<MoodCompactionResult> CompactForgottenAsync(
        string userId, IReadOnlyCollection<Guid> evidenceEventIds, double currentSpirits,
        DateTimeOffset now, CancellationToken ct = default);

    /// <summary>The newest transition for this user, or null when her mood has never moved.</summary>
    Task<CompanionMoodTransition?> GetLatestAsync(string userId, CancellationToken ct = default);

    /// <summary>Every transition, oldest first — the substrate deterministic replay reads.</summary>
    Task<IReadOnlyList<CompanionMoodTransition>> GetHistoryAsync(
        string userId, CancellationToken ct = default);
}
