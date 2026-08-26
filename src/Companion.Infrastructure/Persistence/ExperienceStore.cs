using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Companion.Infrastructure.Persistence;

/// <summary>EF Core-backed store for her own experiences. Every query is scoped by userId.</summary>
public sealed class ExperienceStore : IExperienceStore
{
    private readonly CompanionDbContext _db;

    public ExperienceStore(CompanionDbContext db) => _db = db;

    public async Task<bool> AddAsync(Experience experience, CancellationToken ct = default)
    {
        // A world that reports the same thing twice — a reconnect replaying, a duplicate frame —
        // should not make her believe it happened twice. Only the immediately preceding one is
        // checked: the same thing happening again later is a real event, not a duplicate.
        var previous = await _db.Experiences
            .Where(e => e.UserId == experience.UserId)
            .OrderByDescending(e => e.At)
            .ThenByDescending(e => e.Id)
            .FirstOrDefaultAsync(ct);

        if (previous is not null
            && previous.Kind == experience.Kind
            && previous.Text == experience.Text)
        {
            return false;
        }

        _db.Experiences.Add(experience);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<Experience>> GetSinceAsync(
        string userId, DateTimeOffset? after, int max, CancellationToken ct = default)
        => await _db.Experiences
            .Where(e => e.UserId == userId && (after == null || e.At > after))
            .OrderBy(e => e.At)
            .ThenBy(e => e.Id)
            .Take(Math.Max(0, max))
            .ToListAsync(ct);

    public async Task<int> CountSinceAsync(
        string userId, DateTimeOffset? after, CancellationToken ct = default)
        => await _db.Experiences
            .CountAsync(e => e.UserId == userId && (after == null || e.At > after), ct);

    public async Task<int> PruneAsync(DateTimeOffset before, CancellationToken ct = default)
        => await _db.Experiences.Where(e => e.At < before).ExecuteDeleteAsync(ct);

    public async Task<int> ForgetByEvidenceAsync(
        string userId, IReadOnlyCollection<Guid> messageIds, DateTimeOffset now,
        CancellationToken ct = default)
    {
        if (messageIds.Count == 0) return 0;
        var ids = messageIds.ToHashSet();

        // User-scoped in the QUERY: another user's rows are never loaded, so the rule below
        // cannot reach them however it is called.
        var rows = await _db.Experiences
            .Where(e => e.UserId == userId && !e.EvidenceForgotten && e.EvidenceMessageId != null)
            .ToListAsync(ct);

        var n = EvidenceForgetting.ForgetExperiences(rows, ids);
        if (n > 0) await _db.SaveChangesAsync(ct);
        return n;
    }
}
