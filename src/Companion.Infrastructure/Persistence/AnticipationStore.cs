using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Companion.Infrastructure.Persistence;

/// <summary>EF Core-backed store of anticipations (dated events). User-scoped.</summary>
public sealed class AnticipationStore : IAnticipationStore
{
    private readonly CompanionDbContext _db;

    public AnticipationStore(CompanionDbContext db) => _db = db;

    public async Task AddAsync(Anticipation anticipation, CancellationToken ct = default)
    {
        if (anticipation.Id == Guid.Empty)
            anticipation.Id = Guid.NewGuid();
        _db.Anticipations.Add(anticipation);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<Anticipation>> GetOpenAsync(string userId, CancellationToken ct = default)
        => await _db.Anticipations
            .Where(a => a.UserId == userId
                && (a.Status == AnticipationStatus.Pending || a.Status == AnticipationStatus.Encouraged))
            .OrderBy(a => a.EventAt)
            .ToListAsync(ct);

    public async Task MarkEncouragedAsync(
        string userId, Guid id, DateTimeOffset now, CancellationToken ct = default)
    {
        var anticipation = await _db.Anticipations
            .FirstOrDefaultAsync(a => a.Id == id && a.UserId == userId, ct);
        if (anticipation is null || anticipation.Status != AnticipationStatus.Pending)
            return;

        anticipation.Status = AnticipationStatus.Encouraged;
        anticipation.EncouragedAt = now;
        await _db.SaveChangesAsync(ct);
    }

    public async Task MarkFollowedUpAsync(
        string userId, Guid id, DateTimeOffset now, CancellationToken ct = default)
    {
        var anticipation = await _db.Anticipations
            .FirstOrDefaultAsync(a => a.Id == id && a.UserId == userId, ct);
        if (anticipation is null || !anticipation.IsOpen)
            return;

        anticipation.Status = AnticipationStatus.FollowedUp;
        anticipation.FollowedUpAt = now;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<int> ExpireStaleAsync(
        string userId, DateTimeOffset eventBefore, CancellationToken ct = default)
    {
        var stale = await _db.Anticipations
            .Where(a => a.UserId == userId
                && (a.Status == AnticipationStatus.Pending || a.Status == AnticipationStatus.Encouraged)
                && a.EventAt < eventBefore)
            .ToListAsync(ct);

        foreach (var anticipation in stale)
            anticipation.Status = AnticipationStatus.Expired;

        if (stale.Count > 0)
            await _db.SaveChangesAsync(ct);

        return stale.Count;
    }
}
