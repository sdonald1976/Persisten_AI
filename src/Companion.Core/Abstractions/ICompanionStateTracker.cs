using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>
/// The companion's own inner state — spirits that genuinely follow how conversations have been
/// going, energy that follows her day. Read every turn (it colors her tone) and nudged whenever
/// a turn carries real emotional signal.
/// </summary>
public interface ICompanionStateTracker
{
    /// <summary>Her state right now, derived from stored spirits + the clock.</summary>
    Task<CompanionStateSnapshot> BuildAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// Lets a moment rub off on her: moves spirits a small step toward <paramref name="valence"/>
    /// ([-1, 1]). Called when a turn carried a real emotional reading — shared joy lifts her,
    /// heavy stretches weigh on her. Honest emotional contagion, one small step at a time.
    /// </summary>
    Task NudgeAsync(string userId, double valence, Guid? sourceEvidenceEventId = null,
        CancellationToken ct = default);
}
