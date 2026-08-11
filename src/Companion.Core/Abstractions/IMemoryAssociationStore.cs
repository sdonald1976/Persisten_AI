using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

public interface IMemoryAssociationStore
{
    Task<IReadOnlyList<MemoryAssociation>> GetFromSourcesAsync(string userId, IReadOnlyCollection<Guid> sourceIds, CancellationToken ct = default);
    Task<MemoryAssociation?> AddValidatedAsync(MemoryAssociation association, CancellationToken ct = default);
}
