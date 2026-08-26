using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

public interface ISharedPerspectiveStore
{
    Task<SharedExperiencePerspective?> AddValidatedAsync(SharedExperiencePerspective perspective, CancellationToken ct = default);
    Task<IReadOnlyList<SharedExperiencePerspective>> GetForExperiencesAsync(string userId, IReadOnlyCollection<Guid> experienceIds, CancellationToken ct = default);

    /// <summary>
    /// Removes what the forgotten messages produced here. EXACT message identity only, and
    /// user-scoped by the query so cross-user deletion is structurally impossible. Returns
    /// how many rows changed; forgetting twice returns zero.
    /// </summary>
    Task<int> ForgetByEvidenceAsync(
        string userId, IReadOnlyCollection<Guid> messageIds, DateTimeOffset now,
        CancellationToken ct = default);
}
