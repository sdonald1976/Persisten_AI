using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>
/// Persistence for knowledge gaps. Observation dedupes by (kind, subject): re-observing an
/// existing gap bumps its occurrence count rather than creating a row, so recurrence is a
/// measured signal instead of table spam.
/// </summary>
public interface IGapStore
{
    /// <summary>Mints the gap or bumps the existing one's occurrences. Returns the current
    /// row and whether it was newly created. Never touches Satisfied/Declined/Expired gaps
    /// — a settled question does not quietly reopen.</summary>
    Task<(KnowledgeGap Gap, bool Created)> ObserveAsync(
        string userId, GapKind kind, string subject, GapSource source, Guid? sourceRef,
        DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Open gaps, strongest first (occurrences desc, then oldest first — the
    /// longest-standing gap wins a tie, deterministically).</summary>
    Task<IReadOnlyList<KnowledgeGap>> GetOpenAsync(string userId, CancellationToken ct = default);

    /// <summary>Recent gaps in any status, newest activity first — the /gaps endpoint.</summary>
    Task<IReadOnlyList<KnowledgeGap>> GetRecentAsync(
        string userId, int count, CancellationToken ct = default);

    /// <summary>Marks the gap Pursuing and links its one-and-only curiosity.</summary>
    Task PromoteAsync(string userId, Guid gapId, Guid curiosityId, CancellationToken ct = default);

    /// <summary>Closes matching Open/Pursuing gaps for a learned subject (Satisfied, with a
    /// resolution note) and closes their linked curiosities with them. Returns how many
    /// gaps were satisfied.</summary>
    Task<int> SatisfyBySubjectAsync(
        string userId, string subject, string resolutionNote, CancellationToken ct = default);

    /// <summary>Ages out Open/Pursuing gaps untouched since the cutoff. Returns the count.</summary>
    Task<int> ExpireStaleAsync(string userId, DateTimeOffset olderThan, CancellationToken ct = default);
}
