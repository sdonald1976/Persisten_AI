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
        float[]? embedding, DateTimeOffset now,
        IReadOnlyCollection<Guid>? evidenceMessageIds = null,
        CancellationToken ct = default);

    /// <summary>
    /// Removes what the forgotten messages produced here. EXACT message identity only, and
    /// user-scoped by the query so cross-user deletion is structurally impossible. Returns
    /// how many rows changed; forgetting twice returns zero.
    /// </summary>
    Task<int> ForgetByEvidenceAsync(
        string userId, IReadOnlyCollection<Guid> messageIds, DateTimeOffset now,
        CancellationToken ct = default);
}
