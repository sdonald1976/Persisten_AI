using Companion.Core.Abstractions;
using Companion.Core.Domain;

namespace Companion.Core.Services;

/// <summary>
/// Derives the companion's own inner state — <see cref="RelationshipTracker"/>'s mirror image,
/// pointed inward. Spirits are a stored, slow-moving trace of how conversations have actually
/// felt (nudged a small step per emotional turn, decaying toward contentment over days); energy
/// is derived fresh from the hour of her day plus whether she's recently had a quiet stretch to
/// think. Deterministic throughout: same history + same clock = same mood, so "how are you?"
/// always has an answer that's backed by something real.
/// </summary>
public sealed class CompanionStateTracker : ICompanionStateTracker
{
    /// <summary>How strongly one emotional moment rubs off on her.</summary>
    private const double NudgeWeight = 0.15;

    /// <summary>Spirits halve toward contentment (0) every this-many days without new signal.</summary>
    private const double DecayHalfLifeDays = 4.0;

    /// <summary>A reflection within this window counts as "recently had time to think".</summary>
    private static readonly TimeSpan RestedWindow = TimeSpan.FromHours(12);

    private readonly IProfileStore _profiles;
    private readonly IReflectionStore _reflections;
    private readonly TimeProvider _clock;
    private readonly ICompanionMoodLog? _moodLog;

    public CompanionStateTracker(
        IProfileStore profiles, IReflectionStore reflections, TimeProvider clock,
        ICompanionMoodLog? moodLog = null)
    {
        _profiles = profiles;
        _reflections = reflections;
        _clock = clock;
        _moodLog = moodLog;
    }

    public async Task<CompanionStateSnapshot> BuildAsync(string userId, CancellationToken ct = default)
    {
        var profile = await _profiles.GetOrCreateAsync(userId, ct);
        var now = _clock.GetUtcNow();

        // A recent reflection = she's had quiet time to think. Derived, not stored.
        var latest = await _reflections.GetLatestAsync(userId, ct);
        var rested = latest is not null && now - latest.CreatedAt <= RestedWindow;

        var energy = Math.Clamp(EnergyAt(_clock.GetLocalNow().Hour) + (rested ? 0.1 : 0.0), 0.0, 1.0);

        // Source 4b: the transition this reading descends from, so the state can be cited.
        var moodTransition = _moodLog is null ? null : await _moodLog.GetLatestAsync(userId, ct);

        return new CompanionStateSnapshot
        {
            Spirits = DecayedSpirits(profile.CompanionSpirits, profile.CompanionSpiritsNudgedAt, now),
            Energy = energy,
            Rested = rested,
            StateRef = moodTransition?.Id,
            Version = moodTransition?.Version ?? 0,
        };
    }

    public async Task NudgeAsync(string userId, double valence, Guid? sourceEvidenceEventId = null,
        CancellationToken ct = default)
    {
        var profile = await _profiles.GetOrCreateAsync(userId, ct);
        var now = _clock.GetUtcNow();

        // Decay first (so a nudge lands on the current effective value), then one small step
        // toward the moment's valence.
        var current = DecayedSpirits(profile.CompanionSpirits, profile.CompanionSpiritsNudgedAt, now);
        var nudged = current * (1 - NudgeWeight) + Math.Clamp(valence, -1.0, 1.0) * NudgeWeight;

        // The transition is recorded BEFORE the profile is updated, and its result is what the
        // profile then stores: under concurrency the log is the arbiter, so two simultaneous
        // nudges compose onto each other instead of one silently clobbering the other.
        if (_moodLog is not null)
        {
            var transition = await _moodLog.AppendAsync(
                userId, current, nudged, Math.Clamp(valence, -1.0, 1.0), now,
                sourceEvidenceEventId, ct);
            await _profiles.SetCompanionSpiritsAsync(userId, transition.NewSpirits, now, ct);
            return;
        }

        await _profiles.SetCompanionSpiritsAsync(userId, nudged, now, ct);
    }

    /// <summary>Spirits drift back toward contentment (0) when nothing has moved them in a while.</summary>
    public static double DecayedSpirits(double stored, DateTimeOffset? nudgedAt, DateTimeOffset now)
    {
        if (nudgedAt is null)
            return stored;
        var days = Math.Max(0, (now - nudgedAt.Value).TotalDays);
        return stored * Math.Pow(0.5, days / DecayHalfLifeDays);
    }

    /// <summary>Her energy over the day: quiet nights, a bright morning, a gentle evening taper.</summary>
    public static double EnergyAt(int hour) => hour switch
    {
        >= 23 or < 5 => 0.25,
        >= 5 and < 8 => 0.5,
        >= 8 and < 12 => 0.85,
        >= 12 and < 17 => 0.8,
        >= 17 and < 20 => 0.65,
        _ => 0.45, // 20:00–23:00
    };
}
