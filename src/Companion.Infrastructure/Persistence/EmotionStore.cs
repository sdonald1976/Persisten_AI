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

        // Forgotten signals are excluded at the source: a redacted row is metadata for audit,
        // never material for a snapshot.
        return await _db.EmotionalSignals
            .Where(s => s.UserId == userId && !s.EvidenceForgotten)
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

    public async Task<int> ForgetByEvidenceAsync(
        string userId, IReadOnlyCollection<Guid> messageIds, IReadOnlyCollection<Guid> evidenceEventIds,
        DateTimeOffset now, CancellationToken ct = default)
    {
        if (messageIds.Count == 0 && evidenceEventIds.Count == 0)
            return 0;

        var messages = messageIds.ToHashSet();
        var events = evidenceEventIds.ToHashSet();

        // EXACT identity, user-scoped, already-forgotten rows excluded (idempotence).
        var doomed = await _db.EmotionalSignals
            .Where(s => s.UserId == userId
                        && !s.EvidenceForgotten
                        && (messages.Contains(s.MessageId) || events.Contains(s.EvidenceEventId)))
            .ToListAsync(ct);

        foreach (var s in doomed)
        {
            s.EvidenceForgotten = true;
            s.ForgottenAt = now;
            // The user's own words go with the evidence. What stays is metadata the privacy
            // contract permits: when, how it read, how strong — plus the lexicon label, which
            // is a dictionary token rather than anything the user wrote.
            s.Evidence = null;
            s.Topic = null;
            // A redacted concern can never be surfaced again.
            s.FollowedUp = true;
        }

        if (doomed.Count > 0)
            await _db.SaveChangesAsync(ct);
        return doomed.Count;
    }

    public async Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct = default)
        => await _db.EmotionalSignals
            .Where(s => s.Timestamp < olderThan)
            .ExecuteDeleteAsync(ct);
}
