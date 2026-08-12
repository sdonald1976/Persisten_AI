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

    /// <summary>
    /// The same candidate set as <see cref="GetRetrievableMemoriesAsync"/> but WITHOUT the
    /// embedding blobs — for readers that score through the vector index or don't score at all.
    /// Embeddings are by far the largest column (a float array per memory), and loading them for
    /// a path that never reads them is the single biggest avoidable cost on the turn. The
    /// returned instances are untracked and have a null <c>Embedding</c>: never pass one to an
    /// update method, or you would erase the stored vector.
    /// </summary>
    Task<IReadOnlyList<IMemory>> GetRetrievalCandidatesAsync(string userId, CancellationToken ct = default);

    Task<SemanticMemory?> GetSemanticAsync(Guid id, string userId, CancellationToken ct = default);
    Task<EpisodicMemory?> GetEpisodicAsync(Guid id, string userId, CancellationToken ct = default);

    /// <summary>Persists field changes to an existing semantic memory (e.g. confirmation/merge).</summary>
    Task UpdateSemanticAsync(SemanticMemory memory, CancellationToken ct = default);
    Task UpdateEpisodicAsync(EpisodicMemory memory, CancellationToken ct = default);

    /// <summary>Adds evidence rows to an already-persisted memory owned by <paramref name="userId"/>.</summary>
    Task AddEvidenceAsync(string userId, IReadOnlyList<MemoryEvidence> evidence, CancellationToken ct = default);

    /// <summary>Re-points every memory referencing <paramref name="oldProject"/> to a new project name (or null).</summary>
    Task<int> ReassignProjectAsync(
        string userId, string oldProject, string? newProject, CancellationToken ct = default);

    /// <summary>
    /// Evidence supporting a memory (provenance), scoped to its owner. Ownership is enforced in
    /// the query (MemoryId AND UserId), so a foreign memory id returns an empty list.
    /// </summary>
    Task<IReadOnlyList<MemoryEvidence>> GetEvidenceAsync(
        string userId, Guid memoryId, CancellationToken ct = default);

    /// <summary>Appends an audit-trail entry for a memory owned by <paramref name="userId"/>.</summary>
    Task AddRevisionAsync(string userId, MemoryRevision revision, CancellationToken ct = default);

    /// <summary>
    /// The audit trail for a memory, oldest-first, scoped to its owner. Ownership is enforced in
    /// the query (MemoryId AND UserId), so a foreign memory id returns an empty list.
    /// </summary>
    Task<IReadOnlyList<MemoryRevision>> GetRevisionsAsync(
        string userId, Guid memoryId, CancellationToken ct = default);
}
