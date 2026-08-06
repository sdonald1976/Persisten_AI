using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Companion.Infrastructure.Persistence;

/// <summary>EF Core-backed store for pending clarifications. User-scoped like every other store.</summary>
public sealed class PendingClarificationStore : IPendingClarificationStore
{
    private readonly CompanionDbContext _db;

    public PendingClarificationStore(CompanionDbContext db) => _db = db;

    public async Task AddAsync(PendingClarification pending, CancellationToken ct = default)
    {
        if (pending.Id == Guid.Empty)
            pending.Id = Guid.NewGuid();
        _db.PendingClarifications.Add(pending);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<PendingClarification?> GetActiveAsync(
        string userId, Guid conversationId, CancellationToken ct = default)
        => await _db.PendingClarifications
            .Where(p => p.UserId == userId
                && p.ConversationId == conversationId
                && p.Status == ClarificationStatus.Pending)
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync(ct);

    public async Task UpdateAsync(PendingClarification pending, CancellationToken ct = default)
    {
        _db.PendingClarifications.Update(pending);
        await _db.SaveChangesAsync(ct);
    }
}
