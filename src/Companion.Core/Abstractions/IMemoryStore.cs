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
    /// Returns the user's retrievable memories (both kinds). Excludes Deleted and Candidate
    /// memories. Superseded/Disputed are included so callers can reason about history, but
    /// they are labeled by their <see cref="MemoryStatus"/>.
    /// </summary>
    Task<IReadOnlyList<IMemory>> GetRetrievableMemoriesAsync(string userId, CancellationToken ct = default);

    Task<SemanticMemory?> GetSemanticAsync(Guid id, string userId, CancellationToken ct = default);
    Task<EpisodicMemory?> GetEpisodicAsync(Guid id, string userId, CancellationToken ct = default);

    /// <summary>Persists field changes to an existing semantic memory (e.g. confirmation/merge).</summary>
    Task UpdateSemanticAsync(SemanticMemory memory, CancellationToken ct = default);
    Task UpdateEpisodicAsync(EpisodicMemory memory, CancellationToken ct = default);

    /// <summary>Adds evidence rows to an already-persisted memory (used when merging).</summary>
    Task AddEvidenceAsync(IReadOnlyList<MemoryEvidence> evidence, CancellationToken ct = default);

    /// <summary>Evidence supporting a given memory (provenance).</summary>
    Task<IReadOnlyList<MemoryEvidence>> GetEvidenceAsync(Guid memoryId, CancellationToken ct = default);

    /// <summary>Appends an audit-trail entry.</summary>
    Task AddRevisionAsync(MemoryRevision revision, CancellationToken ct = default);

    /// <summary>The audit trail for a memory, oldest-first.</summary>
    Task<IReadOnlyList<MemoryRevision>> GetRevisionsAsync(Guid memoryId, CancellationToken ct = default);
}
