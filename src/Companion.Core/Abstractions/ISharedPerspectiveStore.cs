using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

public interface ISharedPerspectiveStore
{
    Task<SharedExperiencePerspective?> AddValidatedAsync(SharedExperiencePerspective perspective, CancellationToken ct = default);
    Task<IReadOnlyList<SharedExperiencePerspective>> GetForExperiencesAsync(string userId, IReadOnlyCollection<Guid> experienceIds, CancellationToken ct = default);
}
