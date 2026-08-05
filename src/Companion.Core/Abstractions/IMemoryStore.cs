using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>
/// Persists and reads memories. The store is the boundary that enforces two invariants:
/// user isolation (everything is scoped by userId) and soft-delete filtering (deleted
/// memories are never returned).
/// </summary>
public interface IMemoryStore
{
    Task AddSemanticAsync(SemanticMemory memory, CancellationToken ct = default);
    Task AddEpisodicAsync(EpisodicMemory memory, CancellationToken ct = default);

    /// <summary>
    /// Returns the user's retrievable memories (both kinds). Excludes Deleted memories.
    /// Candidate memories are excluded; Superseded/Disputed are included so callers can
    /// reason about history, but they are labeled by their <see cref="MemoryStatus"/>.
    /// </summary>
    Task<IReadOnlyList<IMemory>> GetRetrievableMemoriesAsync(string userId, CancellationToken ct = default);

    /// <summary>Evidence supporting a given memory (provenance).</summary>
    Task<IReadOnlyList<MemoryEvidence>> GetEvidenceAsync(Guid memoryId, CancellationToken ct = default);
}
