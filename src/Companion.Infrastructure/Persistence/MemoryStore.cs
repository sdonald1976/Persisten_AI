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

    public async Task<IReadOnlyList<MemoryEvidence>> GetEvidenceAsync(
        Guid memoryId, CancellationToken ct = default)
        => await _db.Evidence.Where(e => e.MemoryId == memoryId).ToListAsync(ct);

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
