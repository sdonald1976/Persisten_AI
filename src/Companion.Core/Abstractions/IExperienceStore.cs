using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>
/// Her own experiences, as they arrive. User-scoped like every other store.
///
/// Deliberately small: append, read a window, forget old ones. Anything more would start being a
/// model of where the experiences came from, which is the one thing this must not become.
/// </summary>
public interface IExperienceStore
{
    /// <summary>
    /// Records one experience, unless it is a repeat of the one just before it — a world that
    /// reports the same thing twice should not make her think it happened twice.
    /// </summary>
    Task<bool> AddAsync(Experience experience, CancellationToken ct = default);

    /// <summary>
    /// Everything since <paramref name="after"/> (or all of it when null), oldest first, capped.
    /// The shape reflection needs.
    /// </summary>
    Task<IReadOnlyList<Experience>> GetSinceAsync(
        string userId, DateTimeOffset? after, int max, CancellationToken ct = default);

    /// <summary>How many there are since a moment — enough to decide whether a day was eventful.</summary>
    Task<int> CountSinceAsync(string userId, DateTimeOffset? after, CancellationToken ct = default);

    /// <summary>Drops experiences older than <paramref name="before"/>. Called by the sleep cycle.</summary>
    Task<int> PruneAsync(DateTimeOffset before, CancellationToken ct = default);

    /// <summary>
    /// Removes what the forgotten messages produced here. EXACT message identity only, and
    /// user-scoped by the query so cross-user deletion is structurally impossible. Returns
    /// how many rows changed; forgetting twice returns zero.
    /// </summary>
    Task<int> ForgetByEvidenceAsync(
        string userId, IReadOnlyCollection<Guid> messageIds, DateTimeOffset now,
        CancellationToken ct = default);
}
