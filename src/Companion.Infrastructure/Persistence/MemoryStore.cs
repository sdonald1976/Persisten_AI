using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Companion.Infrastructure.Persistence;

/// <summary>
/// EF Core-backed memory store. This is the boundary that enforces user isolation and
/// soft-delete filtering: <see cref="MemoryStatus.Deleted"/> and <see cref="MemoryStatus.Candidate"/>
/// memories are never returned to retrieval.
/// </summary>
public sealed class MemoryStore : IMemoryStore
{
    private readonly CompanionDbContext _db;
    private readonly IVectorIndexMaintenance _vectorIndex;

    public MemoryStore(CompanionDbContext db, IVectorIndexMaintenance vectorIndex)
    {
        _db = db;
        _vectorIndex = vectorIndex;
    }

    public async Task AddSemanticAsync(SemanticMemory memory, CancellationToken ct = default)
    {
        _db.SemanticMemories.Add(memory);
        await PersistEvidenceAsync(memory.Evidence, memory.Id, MemoryKind.Semantic, memory.UserId, ct);
        await _db.SaveChangesAsync(ct);
        _vectorIndex.Sync(memory); // write-through: keep the derived index in step with the tables
    }

    public async Task AddEpisodicAsync(EpisodicMemory memory, CancellationToken ct = default)
    {
        _db.EpisodicMemories.Add(memory);
        await PersistEvidenceAsync(memory.Evidence, memory.Id, MemoryKind.Episodic, memory.UserId, ct);
        await _db.SaveChangesAsync(ct);
        _vectorIndex.Sync(memory);
    }

    public async Task<IReadOnlyList<IMemory>> GetRetrievableMemoriesAsync(
        string userId, CancellationToken ct = default)
    {
        // Candidate and Deleted are excluded; Active/Superseded/Disputed are returned so
        // callers can reason about history (they are labeled by Status downstream).
        var semantic = await _db.SemanticMemories
            .Where(m => m.UserId == userId
                && m.Status != MemoryStatus.Deleted
                && m.Status != MemoryStatus.Candidate)
            .ToListAsync(ct);

        var episodic = await _db.EpisodicMemories
            .Where(m => m.UserId == userId
                && m.Status != MemoryStatus.Deleted
                && m.Status != MemoryStatus.Candidate)
            .ToListAsync(ct);

        // Concept assertions ride along as MemoryKind.Concept, ACTIVE only — a superseded
        // definition is history to a curator, but never current knowledge to a retriever.
        var concepts = await _db.ConceptAssertions
            .Where(a => a.UserId == userId && a.Status == MemoryStatus.Active)
            .ToListAsync(ct);

        return semantic.Cast<IMemory>().Concat(episodic).Concat(concepts).ToList();
    }

    /// <summary>
    /// Retrieval candidates projected WITHOUT their embeddings (see the interface for why).
    /// Every other scalar is carried across so downstream scoring, kind checks
    /// (<see cref="EpisodicMemory.IsOpenLoop"/>, <see cref="SemanticMemory.Validity"/>) and
    /// rendering behave identically — a projection test asserts this stays exhaustive as the
    /// entities grow.
    /// </summary>
    public async Task<IReadOnlyList<IMemory>> GetRetrievalCandidatesAsync(
        string userId, CancellationToken ct = default)
    {
        var semantic = await _db.SemanticMemories
            .AsNoTracking()
            .Where(m => m.UserId == userId
                && m.Status != MemoryStatus.Deleted
                && m.Status != MemoryStatus.Candidate)
            .Select(m => new SemanticMemory
            {
                Id = m.Id,
                UserId = m.UserId,
                Subject = m.Subject,
                Predicate = m.Predicate,
                Value = m.Value,
                NormalizedFact = m.NormalizedFact,
                Confidence = m.Confidence,
                Importance = m.Importance,
                Validity = m.Validity,
                Status = m.Status,
                Owner = m.Owner,
                Origin = m.Origin,
                FirstObserved = m.FirstObserved,
                LastConfirmed = m.LastConfirmed,
                CreatedAt = m.CreatedAt,
                SupersededById = m.SupersededById,
                RelatedProject = m.RelatedProject,
            })
            .ToListAsync(ct);

        var episodic = await _db.EpisodicMemories
            .AsNoTracking()
            .Where(m => m.UserId == userId
                && m.Status != MemoryStatus.Deleted
                && m.Status != MemoryStatus.Candidate)
            .Select(m => new EpisodicMemory
            {
                Id = m.Id,
                UserId = m.UserId,
                Description = m.Description,
                EventTime = m.EventTime,
                TimePrecision = m.TimePrecision,
                MentionedAt = m.MentionedAt,
                CreatedAt = m.CreatedAt,
                EpisodeStatus = m.EpisodeStatus,
                Importance = m.Importance,
                Confidence = m.Confidence,
                EmotionalSignificance = m.EmotionalSignificance,
                Status = m.Status,
                Owner = m.Owner,
                RelatedProject = m.RelatedProject,
            })
            .ToListAsync(ct);

        // Active concept knowledge scores alongside memories (embedding-free projection,
        // same as above; the vector index holds the vectors).
        var concepts = await _db.ConceptAssertions
            .AsNoTracking()
            .Where(a => a.UserId == userId && a.Status == MemoryStatus.Active)
            .Select(a => new ConceptAssertion
            {
                Id = a.Id,
                UserId = a.UserId,
                ConceptId = a.ConceptId,
                Relation = a.Relation,
                TargetConceptId = a.TargetConceptId,
                Value = a.Value,
                NormalizedText = a.NormalizedText,
                Origin = a.Origin,
                Confidence = a.Confidence,
                Importance = a.Importance,
                Validity = a.Validity,
                Status = a.Status,
                SupersededById = a.SupersededById,
                FirstObserved = a.FirstObserved,
                LastConfirmed = a.LastConfirmed,
                CreatedAt = a.CreatedAt,
            })
            .ToListAsync(ct);

        return semantic.Cast<IMemory>().Concat(episodic).Concat(concepts).ToList();
    }

    public async Task<SemanticMemory?> GetSemanticAsync(Guid id, string userId, CancellationToken ct = default)
        => await _db.SemanticMemories.FirstOrDefaultAsync(m => m.Id == id && m.UserId == userId, ct);

    public async Task<EpisodicMemory?> GetEpisodicAsync(Guid id, string userId, CancellationToken ct = default)
        => await _db.EpisodicMemories.FirstOrDefaultAsync(m => m.Id == id && m.UserId == userId, ct);

    public async Task UpdateSemanticAsync(SemanticMemory memory, CancellationToken ct = default)
    {
        _db.SemanticMemories.Update(memory);
        await _db.SaveChangesAsync(ct);
        _vectorIndex.Sync(memory); // re-embed, soft-delete, or status change is reflected immediately
    }

    public async Task UpdateEpisodicAsync(EpisodicMemory memory, CancellationToken ct = default)
    {
        _db.EpisodicMemories.Update(memory);
        await _db.SaveChangesAsync(ct);
        _vectorIndex.Sync(memory);
    }

    public async Task AddEvidenceAsync(
        string userId, IReadOnlyList<MemoryEvidence> evidence, CancellationToken ct = default)
    {
        foreach (var e in evidence)
        {
            if (e.Id == Guid.Empty)
                e.Id = Guid.NewGuid();
            e.UserId = userId; // ownership is set by the caller's trusted context, never the model
            _db.Evidence.Add(e);
        }
        await _db.SaveChangesAsync(ct);
    }

    public async Task<int> ReassignProjectAsync(
        string userId, string oldProject, string? newProject, CancellationToken ct = default)
    {
        var semantic = await _db.SemanticMemories
            .Where(m => m.UserId == userId && m.RelatedProject == oldProject)
            .ToListAsync(ct);
        var episodic = await _db.EpisodicMemories
            .Where(m => m.UserId == userId && m.RelatedProject == oldProject)
            .ToListAsync(ct);

        foreach (var m in semantic) m.RelatedProject = newProject;
        foreach (var m in episodic) m.RelatedProject = newProject;

        await _db.SaveChangesAsync(ct);
        return semantic.Count + episodic.Count;
    }

    public async Task<IReadOnlyList<MemoryEvidence>> GetEvidenceAsync(
        string userId, Guid memoryId, CancellationToken ct = default)
        => await _db.Evidence.Where(e => e.MemoryId == memoryId && e.UserId == userId).ToListAsync(ct);

    public async Task AddRevisionAsync(string userId, MemoryRevision revision, CancellationToken ct = default)
    {
        if (revision.Id == Guid.Empty)
            revision.Id = Guid.NewGuid();
        revision.UserId = userId; // ownership stamped from trusted context
        _db.Revisions.Add(revision);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<MemoryRevision>> GetRevisionsAsync(
        string userId, Guid memoryId, CancellationToken ct = default)
        => await _db.Revisions
            .Where(r => r.MemoryId == memoryId && r.UserId == userId)
            .OrderBy(r => r.Timestamp)
            .ToListAsync(ct);

    private Task PersistEvidenceAsync(
        IEnumerable<MemoryEvidence> evidence, Guid memoryId, MemoryKind kind, string userId, CancellationToken ct)
    {
        foreach (var e in evidence)
        {
            if (e.Id == Guid.Empty)
                e.Id = Guid.NewGuid();
            e.MemoryId = memoryId;
            e.MemoryKind = kind;
            e.UserId = userId; // authoritative: evidence inherits the owning memory's user
            _db.Evidence.Add(e);
        }
        return Task.CompletedTask;
    }
}
