namespace Companion.Core.Abstractions;

/// <summary>
/// The seam where similarity search plugs in. Phase 2 backs this with in-process cosine
/// over embeddings stored in SQLite; a dedicated ANN store (sqlite-vec, Qdrant, ...) can
/// replace it later without touching callers. It is a derived index, never authoritative.
/// </summary>
public interface IVectorIndex
{
    /// <summary>Returns the most similar memory ids for a query embedding, scoped to a user.</summary>
    Task<IReadOnlyList<VectorHit>> SearchAsync(
        string userId, float[] query, int topK, CancellationToken ct = default);
}

/// <summary>A single similarity hit: the memory id and its cosine similarity in [0, 1].</summary>
public readonly record struct VectorHit(Guid MemoryId, double Similarity);
