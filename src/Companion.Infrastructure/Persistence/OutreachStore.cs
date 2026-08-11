using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Companion.Infrastructure.Persistence;

/// <summary>EF Core-backed log of the companion's self-initiated messages. User-scoped.</summary>
public sealed class OutreachStore : IOutreachStore
{
    private readonly CompanionDbContext _db;

    public OutreachStore(CompanionDbContext db) => _db = db;

    public async Task AddAsync(OutboundMessage message, CancellationToken ct = default)
    {
        if (message.Id == Guid.Empty)
            message.Id = Guid.NewGuid();
        _db.OutboundMessages.Add(message);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<DateTimeOffset?> GetLastSentAtAsync(string userId, CancellationToken ct = default)
        => await _db.OutboundMessages
            .Where(m => m.UserId == userId)
            .OrderByDescending(m => m.SentAt)
            .Select(m => (DateTimeOffset?)m.SentAt)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<OutboundMessage>> GetRecentAsync(
        string userId, int count, CancellationToken ct = default)
    {
        if (count <= 0)
            return Array.Empty<OutboundMessage>();

        return await _db.OutboundMessages
            .Where(m => m.UserId == userId)
            .OrderByDescending(m => m.SentAt)
            .Take(count)
            .ToListAsync(ct);
    }
}
