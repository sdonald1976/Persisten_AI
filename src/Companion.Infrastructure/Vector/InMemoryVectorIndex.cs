using System.Collections.Concurrent;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Companion.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Companion.Infrastructure.Vector;

/// <summary>
/// Phase-2 vector index that keeps embeddings resident in memory instead of re-reading and
/// re-deserializing every embedding BLOB on every turn. Cold-loads a user's embeddings from the
/// authoritative tables the first time they are searched, then stays exactly in step through the
/// <see cref="IVectorIndexMaintenance.Sync"/> write-through hook the memory store calls after each
/// save. Similarity is still <em>exact</em> cosine — nothing here is approximate; the win is
/// purely that the O(n) scan runs over cached float arrays rather than fresh SQLite reads.
/// <para>
/// It remains a derived index, never authoritative: the cache is always (re)built from the tables,
/// so a cold start or a dropped process loses nothing. Soft-deleted / candidate / embedding-less
/// memories are excluded here, exactly as they were by the pure-DB implementation, so they can
/// never resurface through the similarity path.
/// </para>
/// <para>
/// Registered as a singleton (it caches across scopes) and it reaches the database only for the
/// cold load, through a fresh scope from <see cref="IServiceScopeFactory"/> — it never holds a
/// scoped <see cref="CompanionDbContext"/>. Per-user state is guarded so a concurrent cold load
/// and a write can never drop an entry.
/// </para>
/// </summary>
public sealed class InMemoryVectorIndex : IVectorIndex, IVectorIndexMaintenance
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<InMemoryVectorIndex> _logger;
    private readonly ConcurrentDictionary<string, UserVectors> _users = new(StringComparer.Ordinal);

    public InMemoryVectorIndex(IServiceScopeFactory scopes, ILogger<InMemoryVectorIndex> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    public async Task<IReadOnlyList<VectorHit>> SearchAsync(
        string userId, float[] query, int topK, CancellationToken ct = default)
    {
        if (topK <= 0)
            return Array.Empty<VectorHit>();

        var user = _users.GetOrAdd(userId, static _ => new UserVectors());
        await user.EnsureLoadedAsync(userId, _scopes, _logger, ct);

        // Snapshot under the lock, score outside it: cosine over cached arrays never mutates them,
        // and holding the lock during the O(n) scan would serialize unrelated writes needlessly.
        var snapshot = user.Snapshot();

        var hits = new List<VectorHit>(snapshot.Count);
        foreach (var (id, embedding) in snapshot)
        {
            var sim = ScoreMath.Cosine(query, embedding);
            if (sim > 0)
                hits.Add(new VectorHit(id, sim));
        }

        hits.Sort(static (a, b) => b.Similarity.CompareTo(a.Similarity));
        return hits.Count <= topK ? hits : hits.GetRange(0, topK);
    }

    public void Sync(IMemory memory)
    {
        // Only touch a user whose set is already resident: an unloaded set is rebuilt in full from
        // the tables on its next search (which happens-after this save committed), so it will see
        // this memory then. Forcing a cold load here would put database I/O on the write path for
        // no correctness gain. The per-user lock makes the "loaded?" check race-free against a
        // concurrent cold load, so a memory saved mid-load is never dropped.
        if (!_users.TryGetValue(memory.UserId, out var user))
            return;

        var eligible = memory.Embedding is { Length: > 0 }
            && memory.Status != MemoryStatus.Deleted
            && memory.Status != MemoryStatus.Candidate;

        user.Apply(memory.Id, eligible ? memory.Embedding : null);
    }

    /// <summary>Per-user cache of id → embedding, with a gate that serializes cold load and writes.</summary>
    private sealed class UserVectors
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Dictionary<Guid, float[]> _vectors = new();
        private bool _loaded;

        public async Task EnsureLoadedAsync(
            string userId, IServiceScopeFactory scopes, ILogger logger, CancellationToken ct)
        {
            if (_loaded)
                return;

            await _gate.WaitAsync(ct);
            try
            {
                if (_loaded)
                    return;

                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<CompanionDbContext>();

                var semantic = await db.SemanticMemories
                    .Where(m => m.UserId == userId
                        && m.Status != MemoryStatus.Deleted
                        && m.Status != MemoryStatus.Candidate
                        && m.Embedding != null)
                    .Select(m => new { m.Id, m.Embedding })
                    .ToListAsync(ct);

                var episodic = await db.EpisodicMemories
                    .Where(m => m.UserId == userId
                        && m.Status != MemoryStatus.Deleted
                        && m.Status != MemoryStatus.Candidate
                        && m.Embedding != null)
                    .Select(m => new { m.Id, m.Embedding })
                    .ToListAsync(ct);

                // Concept assertions index only while ACTIVE: a superseded definition is
                // history, not something to surface as her current knowledge.
                var concepts = await db.ConceptAssertions
                    .Where(a => a.UserId == userId
                        && a.Status == MemoryStatus.Active
                        && a.Embedding != null)
                    .Select(a => new { a.Id, a.Embedding })
                    .ToListAsync(ct);

                _vectors.Clear();
                foreach (var m in semantic)
                    _vectors[m.Id] = m.Embedding!;
                foreach (var m in episodic)
                    _vectors[m.Id] = m.Embedding!;
                foreach (var a in concepts)
                    _vectors[a.Id] = a.Embedding!;

                _loaded = true;
                logger.LogDebug("Vector index cold-loaded {Count} embeddings for user {UserId}",
                    _vectors.Count, userId);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Indexes (embedding non-null) or removes (null) a single memory. No-op if unloaded.</summary>
        public void Apply(Guid id, float[]? embedding)
        {
            _gate.Wait();
            try
            {
                if (!_loaded)
                    return; // a full cold load will pick up the committed row on next search

                if (embedding is { Length: > 0 })
                    _vectors[id] = embedding;
                else
                    _vectors.Remove(id);
            }
            finally
            {
                _gate.Release();
            }
        }

        public IReadOnlyList<KeyValuePair<Guid, float[]>> Snapshot()
        {
            _gate.Wait();
            try
            {
                return _vectors.ToArray();
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
