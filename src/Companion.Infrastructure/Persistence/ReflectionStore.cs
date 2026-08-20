using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Companion.Infrastructure.Persistence;

/// <summary>EF Core-backed diary of reflections and their curiosities. Every query is user-scoped.</summary>
public sealed class ReflectionStore : IReflectionStore
{
    private readonly CompanionDbContext _db;

    public ReflectionStore(CompanionDbContext db) => _db = db;

    public async Task AddCuriosityAsync(Curiosity curiosity, CancellationToken ct = default)
    {
        if (curiosity.Id == Guid.Empty)
            curiosity.Id = Guid.NewGuid();
        _db.Curiosities.Add(curiosity);
        await _db.SaveChangesAsync(ct);
    }

    public async Task AddAsync(
        Reflection reflection, IReadOnlyList<Curiosity> curiosities, CancellationToken ct = default)
    {
        if (reflection.Id == Guid.Empty)
            reflection.Id = Guid.NewGuid();

        _db.Reflections.Add(reflection);
        foreach (var curiosity in curiosities)
        {
            if (curiosity.Id == Guid.Empty)
                curiosity.Id = Guid.NewGuid();
            curiosity.ReflectionId = reflection.Id;
            curiosity.UserId = reflection.UserId;
            _db.Curiosities.Add(curiosity);
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task<Reflection?> GetLatestAsync(string userId, CancellationToken ct = default)
        => await _db.Reflections
            .Where(r => r.UserId == userId)
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<Reflection>> GetRecentAsync(
        string userId, int count, CancellationToken ct = default)
    {
        if (count <= 0)
            return Array.Empty<Reflection>();

        return await _db.Reflections
            .Where(r => r.UserId == userId)
            .OrderByDescending(r => r.CreatedAt)
            .Take(count)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Groups the diary into trains of thought. Only entries with a musing take part — a quiet-day
    /// watermark is bookkeeping, not a thought — and threads are ordered by when they were last
    /// developed, so what she is currently chewing on comes first.
    /// </summary>
    public async Task<IReadOnlyList<ReflectionThread>> GetThreadsAsync(
        string userId, int count, CancellationToken ct = default)
    {
        if (count <= 0)
            return Array.Empty<ReflectionThread>();

        // A generous window of recent musings, then grouped in memory: threads interleave, so
        // taking the newest N rows and grouping is what reconstructs them in one query.
        var musings = await _db.Reflections
            .Where(r => r.UserId == userId && r.Musing != null)
            .OrderByDescending(r => r.CreatedAt)
            .Take(Math.Clamp(count * 10, 20, 300))
            .ToListAsync(ct);

        return musings
            // A pre-threading row has an empty ThreadId; treat it as its own single-entry thread
            // so old diary entries still render instead of collapsing into one bogus thread.
            .GroupBy(r => r.ThreadId == Guid.Empty ? r.Id : r.ThreadId)
            .Select(g => new ReflectionThread
            {
                ThreadId = g.Key,
                Entries = g.OrderBy(r => r.CreatedAt).ToList(),
            })
            .OrderByDescending(t => t.LastDevelopedAt)
            .Take(count)
            .ToList();
    }

    public async Task<IReadOnlyList<Curiosity>> GetOpenCuriositiesAsync(
        string userId, CancellationToken ct = default)
        => await _db.Curiosities
            .Where(c => c.UserId == userId && c.Status == CuriosityStatus.Open)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);

    public async Task<Curiosity?> GetNextToVoiceAsync(
        string userId, DateTimeOffset now, TimeSpan cooldown, CancellationToken ct = default)
    {
        // The cooldown is what keeps curiosity from becoming interrogation: if anything was voiced
        // recently, hold the rest back — they stay open and get their moment later.
        var floor = now - cooldown;
        var recentlyVoiced = await _db.Curiosities.AnyAsync(
            c => c.UserId == userId && c.VoicedAt != null && c.VoicedAt > floor, ct);
        if (recentlyVoiced)
            return null;

        return await _db.Curiosities
            .Where(c => c.UserId == userId && c.Status == CuriosityStatus.Open)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    public async Task MarkVoicedAsync(
        string userId, Guid curiosityId, DateTimeOffset now, CancellationToken ct = default)
    {
        var curiosity = await _db.Curiosities
            .FirstOrDefaultAsync(c => c.Id == curiosityId && c.UserId == userId, ct);
        if (curiosity is null || curiosity.Status != CuriosityStatus.Open)
            return;

        curiosity.Status = CuriosityStatus.Voiced;
        curiosity.VoicedAt = now;
        await _db.SaveChangesAsync(ct);
    }

    public async Task MarkSatisfiedAsync(string userId, Guid curiosityId, CancellationToken ct = default)
    {
        var curiosity = await _db.Curiosities
            .FirstOrDefaultAsync(c => c.Id == curiosityId && c.UserId == userId, ct);
        if (curiosity is null || curiosity.Status is not (CuriosityStatus.Open or CuriosityStatus.Voiced))
            return;

        curiosity.Status = CuriosityStatus.Satisfied;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<int> DismissStaleAsync(
        string userId, DateTimeOffset olderThan, CancellationToken ct = default)
    {
        var stale = await _db.Curiosities
            .Where(c => c.UserId == userId
                && c.Status == CuriosityStatus.Open
                && c.CreatedAt < olderThan)
            .ToListAsync(ct);

        foreach (var curiosity in stale)
            curiosity.Status = CuriosityStatus.Dismissed;

        if (stale.Count > 0)
            await _db.SaveChangesAsync(ct);

        return stale.Count;
    }
}
