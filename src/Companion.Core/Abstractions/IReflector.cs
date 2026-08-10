using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>
/// The between-session reflection pass — the companion's inner monologue. One call reads
/// everything new since the last pass and, when there is enough to think about, writes a diary
/// entry (musing) and mints curiosities. Runs while the user is away (idle worker) or on demand.
/// </summary>
public interface IReflector
{
    /// <summary>
    /// Runs one reflection pass. Returns what was produced and persisted, or null when the pass
    /// didn't run at all (not enough new material, or the model's output was unusable —
    /// distinguishable by logs; callers only need "nothing new happened").
    /// </summary>
    Task<ReflectionResult?> ReflectAsync(string userId, CancellationToken ct = default);
}
