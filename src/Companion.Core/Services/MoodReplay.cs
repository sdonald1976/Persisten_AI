using Companion.Core.Domain;

namespace Companion.Core.Services;

/// <summary>
/// Replays her spirits from the transition log, and — the point of existing as a named thing —
/// says honestly when it CANNOT.
///
/// Privacy compaction deletes the rows behind a forgotten moment and replaces them with an
/// opaque baseline. Replay from that baseline forward is exact; replay across it is
/// permanently unavailable, because the rows it would need are precisely the rows whose
/// arithmetic leaked the forgotten valence. That gap is reported rather than approximated: a
/// number produced by guessing at deleted history would be worse than no number.
/// </summary>
public static class MoodReplay
{
    /// <param name="Spirits">The replayed value, or null when there is nothing to replay.</param>
    /// <param name="CoversFullHistory">
    /// True when the replay ran from her very first transition. False when it began at a
    /// compaction baseline — the log no longer contains what came before, by design.
    /// </param>
    /// <param name="Diagnosis">Content-safe reason token when the history is not complete.</param>
    public sealed record Result(double? Spirits, bool CoversFullHistory, string? Diagnosis);

    /// <summary>How strongly one moment moves her — must match the tracker's nudge weight.</summary>
    public const double NudgeWeight = 0.15;

    public static Result Replay(IReadOnlyList<CompanionMoodTransition> history)
    {
        if (history.Count == 0)
            return new Result(null, CoversFullHistory: true, Diagnosis: null);

        var ordered = history.OrderBy(t => t.Version).ToList();
        var baseline = ordered.LastOrDefault(t => t.IsBaseline);
        var start = baseline is null ? ordered[0] : baseline;
        var from = ordered.IndexOf(start);

        // A baseline IS the starting value; an ordinary first row starts from its predecessor.
        var spirits = baseline is not null
            ? baseline.NewSpirits
            : start.PreviousSpirits ?? start.NewSpirits;

        for (var i = baseline is not null ? from + 1 : from; i < ordered.Count; i++)
        {
            var t = ordered[i];
            if (t.AppliedValence is not { } valence)
            {
                // A row whose reading was purged but which was not compacted away: replay
                // cannot continue through it truthfully.
                return new Result(spirits, false,
                    $"replay stopped at version {t.Version}: applied valence unavailable");
            }
            spirits = spirits * (1 - NudgeWeight) + valence * NudgeWeight;
        }

        return baseline is null
            ? new Result(spirits, true, null)
            : new Result(spirits, false,
                $"history compacted at version {baseline.Version}; "
                + "transitions before it were removed to sever a forgotten moment");
    }
}
