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
    /// Runs one reflection pass. Returns what was produced and persisted, or — when nothing was —
    /// which of the several very different reasons applied. Callers that only care whether
    /// anything happened can read <see cref="ReflectionOutcome.Reflected"/>.
    /// </summary>
    Task<ReflectionOutcome> ReflectAsync(string userId, CancellationToken ct = default);
}
