using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Companion.Infrastructure.Persistence;

public sealed class AttentionStore : IAttentionStore
{
    private readonly CompanionDbContext _db;
    public AttentionStore(CompanionDbContext db) => _db = db;

    public async Task<IReadOnlyList<AttentionItem>> GetActiveAsync(string userId, int limit, CancellationToken ct = default)
        => await _db.AttentionItems
            .Where(a => a.UserId == userId && a.Status == AttentionStatus.Active)
            .OrderByDescending(a => a.Strength)
            .ThenByDescending(a => a.LastActivatedAt)
            .Take(limit)
            .ToListAsync(ct);

    public async Task UpsertAsync(AttentionItem item, CancellationToken ct = default)
    {
        var existing = await _db.AttentionItems.FirstOrDefaultAsync(a =>
            a.UserId == item.UserId && a.Status == AttentionStatus.Active && a.Subject == item.Subject, ct);
        if (existing is null)
            _db.AttentionItems.Add(item);
        else
        {
            existing.Summary = item.Summary;
            existing.Strength = Math.Clamp(existing.Strength + item.Strength, 0, 1);
            existing.LastActivatedAt = item.LastActivatedAt;
            existing.ExpiresAt = item.ExpiresAt;
        }
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(AttentionItem item, CancellationToken ct = default)
    {
        _db.AttentionItems.Update(item);
        await _db.SaveChangesAsync(ct);
    }

    public async Task ExpireOldAsync(string userId, DateTimeOffset now, CancellationToken ct = default)
    {
        var expired = await _db.AttentionItems
            .Where(a => a.UserId == userId && a.Status == AttentionStatus.Active && a.ExpiresAt <= now)
            .ToListAsync(ct);
        foreach (var item in expired)
            item.Status = AttentionStatus.Expired;
        await _db.SaveChangesAsync(ct);
    }
}
