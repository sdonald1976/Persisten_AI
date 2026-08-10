using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>
/// One full "moment of thought" while the user is away: reflect (the inner monologue), then do the
/// memory housekeeping that used to wait for an explicit command — consolidation and curiosity
/// hygiene. The idle worker drives this; each piece stays independently usable.
/// </summary>
public interface ISleepCycle
{
    /// <summary>Runs one cycle and reports what happened (all parts are safe to re-run).</summary>
    Task<SleepCycleResult> RunAsync(string userId, CancellationToken ct = default);
}
