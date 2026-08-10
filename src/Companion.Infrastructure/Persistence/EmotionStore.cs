using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Companion.Infrastructure.Persistence;

/// <summary>EF Core-backed, append-only log of emotional readings (the relational-memory substrate).</summary>
public sealed class EmotionStore : IEmotionStore
{
    private readonly CompanionDbContext _db;

    public EmotionStore(CompanionDbContext db) => _db = db;

    public async Task AddSignalAsync(EmotionalSignal signal, CancellationToken ct = default)
    {
        if (signal.Id == Guid.Empty)
            signal.Id = Guid.NewGuid();
        _db.EmotionalSignals.Add(signal);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<EmotionalSignal>> GetRecentSignalsAsync(
        string userId, int count, CancellationToken ct = default)
    {
        if (count <= 0)
            return Array.Empty<EmotionalSignal>();

        return await _db.EmotionalSignals
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.Timestamp)
            .Take(count)
            .ToListAsync(ct);
    }

    public async Task<int> MarkTopicFollowedUpAsync(string userId, string topic, CancellationToken ct = default)
    {
        var norm = topic.Trim().ToLowerInvariant();
        if (norm.Length == 0)
            return 0;

        var open = await _db.EmotionalSignals
            .Where(s => s.UserId == userId
                && !s.FollowedUp
                && s.Topic != null
                && s.Topic.ToLower() == norm)
            .ToListAsync(ct);

        foreach (var s in open)
            s.FollowedUp = true;

        if (open.Count > 0)
            await _db.SaveChangesAsync(ct);

        return open.Count;
    }
}
