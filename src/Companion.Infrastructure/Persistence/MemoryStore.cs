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

    public MemoryStore(CompanionDbContext db) => _db = db;

    public async Task AddSemanticAsync(SemanticMemory memory, CancellationToken ct = default)
    {
        _db.SemanticMemories.Add(memory);
        await PersistEvidenceAsync(memory.Evidence, memory.Id, MemoryKind.Semantic, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task AddEpisodicAsync(EpisodicMemory memory, CancellationToken ct = default)
    {
        _db.EpisodicMemories.Add(memory);
        await PersistEvidenceAsync(memory.Evidence, memory.Id, MemoryKind.Episodic, ct);
        await _db.SaveChangesAsync(ct);
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

        return semantic.Cast<IMemory>().Concat(episodic).ToList();
    }

    public async Task<SemanticMemory?> GetSemanticAsync(Guid id, string userId, CancellationToken ct = default)
        => await _db.SemanticMemories.FirstOrDefaultAsync(m => m.Id == id && m.UserId == userId, ct);

    public async Task<EpisodicMemory?> GetEpisodicAsync(Guid id, string userId, CancellationToken ct = default)
        => await _db.EpisodicMemories.FirstOrDefaultAsync(m => m.Id == id && m.UserId == userId, ct);

    public async Task UpdateSemanticAsync(SemanticMemory memory, CancellationToken ct = default)
    {
        _db.SemanticMemories.Update(memory);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateEpisodicAsync(EpisodicMemory memory, CancellationToken ct = default)
    {
        _db.EpisodicMemories.Update(memory);
        await _db.SaveChangesAsync(ct);
    }

    public async Task AddEvidenceAsync(IReadOnlyList<MemoryEvidence> evidence, CancellationToken ct = default)
    {
        foreach (var e in evidence)
        {
            if (e.Id == Guid.Empty)
                e.Id = Guid.NewGuid();
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
        Guid memoryId, CancellationToken ct = default)
        => await _db.Evidence.Where(e => e.MemoryId == memoryId).ToListAsync(ct);

    public async Task AddRevisionAsync(MemoryRevision revision, CancellationToken ct = default)
    {
        if (revision.Id == Guid.Empty)
            revision.Id = Guid.NewGuid();
        _db.Revisions.Add(revision);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<MemoryRevision>> GetRevisionsAsync(
        Guid memoryId, CancellationToken ct = default)
        => await _db.Revisions
            .Where(r => r.MemoryId == memoryId)
            .OrderBy(r => r.Timestamp)
            .ToListAsync(ct);

    private Task PersistEvidenceAsync(
        IEnumerable<MemoryEvidence> evidence, Guid memoryId, MemoryKind kind, CancellationToken ct)
    {
        foreach (var e in evidence)
        {
            if (e.Id == Guid.Empty)
                e.Id = Guid.NewGuid();
            e.MemoryId = memoryId;
            e.MemoryKind = kind;
            _db.Evidence.Add(e);
        }
        return Task.CompletedTask;
    }
}
