using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Companion.Infrastructure.Persistence;

public sealed class MemoryAssociationStore : IMemoryAssociationStore
{
    private readonly CompanionDbContext _db;
    public MemoryAssociationStore(CompanionDbContext db) => _db = db;

    public async Task<IReadOnlyList<MemoryAssociation>> GetFromSourcesAsync(
        string userId, IReadOnlyCollection<Guid> sourceIds, CancellationToken ct = default)
        => sourceIds.Count == 0
            ? Array.Empty<MemoryAssociation>()
            : await _db.MemoryAssociations
                .Where(a => a.UserId == userId && sourceIds.Contains(a.SourceMemoryId))
                .OrderByDescending(a => a.Strength)
                .ToListAsync(ct);

    public async Task<MemoryAssociation?> AddValidatedAsync(MemoryAssociation association, CancellationToken ct = default)
    {
        if (association.SourceMemoryId == association.TargetMemoryId || string.IsNullOrWhiteSpace(association.Evidence))
            return null;
        var sourceExists = await MemoryExistsAsync(association.UserId, association.SourceMemoryId, ct);
        var targetExists = await MemoryExistsAsync(association.UserId, association.TargetMemoryId, ct);
        if (!sourceExists || !targetExists)
            return null;

        association.Id = association.Id == Guid.Empty ? Guid.NewGuid() : association.Id;
        association.Strength = Math.Clamp(association.Strength, 0, 1);
        _db.MemoryAssociations.Add(association);
        await _db.SaveChangesAsync(ct);
        return association;
    }

    private async Task<bool> MemoryExistsAsync(string userId, Guid id, CancellationToken ct)
        => await _db.SemanticMemories.AnyAsync(m => m.UserId == userId && m.Id == id, ct)
           || await _db.EpisodicMemories.AnyAsync(m => m.UserId == userId && m.Id == id, ct);
}
