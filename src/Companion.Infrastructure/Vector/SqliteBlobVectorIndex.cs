using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Companion.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Companion.Infrastructure.Vector;

/// <summary>
/// Phase-2 vector index: reads embedding BLOBs straight from the authoritative memory
/// tables and computes cosine similarity in process. It is the swap-point for a real ANN
/// store later. Because embeddings live on the memory rows, there is nothing separate to
/// keep in sync — and soft-deleted/candidate memories are filtered here too, so they can
/// never resurface through the similarity path.
/// </summary>
public sealed class SqliteBlobVectorIndex : IVectorIndex
{
    private readonly CompanionDbContext _db;

    public SqliteBlobVectorIndex(CompanionDbContext db) => _db = db;

    public async Task<IReadOnlyList<VectorHit>> SearchAsync(
        string userId, float[] query, int topK, CancellationToken ct = default)
    {
        if (topK <= 0)
            return Array.Empty<VectorHit>();

        var semantic = await _db.SemanticMemories
            .Where(m => m.UserId == userId
                && m.Status != MemoryStatus.Deleted
                && m.Status != MemoryStatus.Candidate
                && m.Embedding != null)
            .Select(m => new { m.Id, m.Embedding })
            .ToListAsync(ct);

        var episodic = await _db.EpisodicMemories
            .Where(m => m.UserId == userId
                && m.Status != MemoryStatus.Deleted
                && m.Status != MemoryStatus.Candidate
                && m.Embedding != null)
            .Select(m => new { m.Id, m.Embedding })
            .ToListAsync(ct);

        return semantic.Concat(episodic)
            .Select(m => new VectorHit(m.Id, ScoreMath.Cosine(query, m.Embedding)))
            .Where(h => h.Similarity > 0)
            .OrderByDescending(h => h.Similarity)
            .Take(topK)
            .ToList();
    }
}
