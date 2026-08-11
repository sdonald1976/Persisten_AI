using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>
/// Persists the companion's own tastes. User-scoped like every store. Writes go through
/// <see cref="ApplySignalAsync"/> so evolution rules (gradual movement, contradiction erodes
/// confidence before it flips affinity) can never be bypassed by a caller.
/// </summary>
public interface IPreferenceStore
{
    /// <summary>All current preferences, most recently updated first.</summary>
    Task<IReadOnlyList<CompanionPreference>> GetAllAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// Applies one experience to the preference for <paramref name="subject"/> (matched
    /// case-insensitively): creates it gently if new, evolves it per <see cref="Services.PreferenceMath"/>
    /// otherwise. Returns the resulting preference.
    /// </summary>
    Task<CompanionPreference> ApplySignalAsync(
        string userId, string subject, double targetAffinity, string? reason,
        float[]? embedding, DateTimeOffset now, CancellationToken ct = default);
}
