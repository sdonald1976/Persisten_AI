using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Companion.Infrastructure.Persistence;

/// <summary>
/// EF-backed append-only mood transition log. The (UserId, Version) unique index is the
/// concurrency guard: two simultaneous nudges cannot both claim the same version, so one of
/// them loses the insert and retries onto the value the winner just wrote. The result is that
/// concurrent nudges COMPOSE rather than clobber — both moments rub off on her.
/// </summary>
internal sealed class CompanionMoodLog(IServiceScopeFactory scopes) : ICompanionMoodLog
{
    private const int MaxAttempts = 5;

    public async Task<CompanionMoodTransition> AppendAsync(
        string userId, double previousSpirits, double newSpirits, double appliedValence,
        DateTimeOffset occurredAt, Guid? sourceEvidenceEventId = null,
        CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CompanionDbContext>();

            var latest = await db.CompanionMoodTransitions.AsNoTracking()
                .Where(t => t.UserId == userId)
                .OrderByDescending(t => t.Version)
                .FirstOrDefaultAsync(ct);

            // Re-derive from whatever is current: a nudge that lost the race must land on the
            // winner's value, not on the stale one it originally read.
            var from = latest?.NewSpirits ?? previousSpirits;
            var applied = attempt == 1 && latest is null
                ? newSpirits
                : from + (newSpirits - previousSpirits);

            var row = new CompanionMoodTransition
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Version = (latest?.Version ?? 0) + 1,
                PreviousSpirits = from,
                NewSpirits = Math.Clamp(applied, -1.0, 1.0),
                AppliedValence = appliedValence,
                SourceEvidenceEventId = sourceEvidenceEventId,
                OccurredAt = occurredAt,
            };

            db.CompanionMoodTransitions.Add(row);
            try
            {
                await db.SaveChangesAsync(ct);
                return row;
            }
            catch (DbUpdateException) when (attempt < MaxAttempts)
            {
                // Somebody else took this version. Read again and land on their result.
            }
        }
    }

    public async Task<CompanionMoodTransition?> GetLatestAsync(
        string userId, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CompanionDbContext>();
        return await db.CompanionMoodTransitions.AsNoTracking()
            .Where(t => t.UserId == userId)
            .OrderByDescending(t => t.Version)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<int> ForgetByEvidenceAsync(
        string userId, IReadOnlyCollection<Guid> evidenceEventIds, DateTimeOffset now,
        CancellationToken ct = default)
    {
        if (evidenceEventIds.Count == 0)
            return 0;

        var events = evidenceEventIds.ToHashSet();
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CompanionDbContext>();

        var doomed = await db.CompanionMoodTransitions
            .Where(t => t.UserId == userId
                        && !t.EvidenceForgotten
                        && t.SourceEvidenceEventId != null
                        && events.Contains(t.SourceEvidenceEventId!.Value))
            .ToListAsync(ct);

        foreach (var t in doomed)
        {
            t.EvidenceForgotten = true;
            // The stored reading of the user's moment goes. Her own trajectory
            // (PreviousSpirits/NewSpirits) and the version chain stay — they are her state,
            // and the chain is what keeps concurrency and audit honest.
            t.AppliedValence = null;
        }

        if (doomed.Count > 0)
            await db.SaveChangesAsync(ct);
        return doomed.Count;
    }

    public async Task<IReadOnlyList<CompanionMoodTransition>> GetHistoryAsync(
        string userId, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CompanionDbContext>();
        return await db.CompanionMoodTransitions.AsNoTracking()
            .Where(t => t.UserId == userId)
            .OrderBy(t => t.Version)
            .ToListAsync(ct);
    }
}
