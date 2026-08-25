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

    public async Task<MoodCompactionResult> CompactForgottenAsync(
        string userId, IReadOnlyCollection<Guid> evidenceEventIds, double currentSpirits,
        DateTimeOffset now, CancellationToken ct = default)
    {
        if (evidenceEventIds.Count == 0)
            return new MoodCompactionResult(false, 0, null);

        var events = evidenceEventIds.ToHashSet();

        for (var attempt = 1; ; attempt++)
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CompanionDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var all = await db.CompanionMoodTransitions
                .Where(t => t.UserId == userId)
                .OrderBy(t => t.Version)
                .ToListAsync(ct);

            // The boundary is the NEWEST forgotten transition. Everything at or before it goes:
            // a nulled row's neighbours reconstruct it exactly, so partial severing is not
            // severing at all.
            var boundary = all
                .Where(t => t.SourceEvidenceEventId is { } id && events.Contains(id))
                .Select(t => (int?)t.Version)
                .DefaultIfEmpty(null)
                .Max();

            if (boundary is not { } cut)
            {
                await tx.RollbackAsync(ct);
                return new MoodCompactionResult(false, 0, null);
            }

            // TOTAL, not partial. Cutting only at-or-before the boundary looks tidier and does
            // not work: the row immediately AFTER the cut still carries PreviousSpirits (the
            // boundary's own result) and its own applied valence, from which the forgotten
            // value falls straight out. Severing that too costs the successor's history
            // anyway, so the honest move is the complete one — every transition goes, and a
            // single opaque baseline carries where she actually stands.
            var doomed = all;
            db.CompanionMoodTransitions.RemoveRange(doomed);

            // Her mood is NOT rewound. The baseline carries where she actually stands, with
            // no predecessor and no applied valence — nothing to solve the arithmetic with.
            var baseline = new CompanionMoodTransition
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                // Continue the version sequence rather than restarting it: versions stay
                // monotonic across compactions, which keeps the audit trail readable.
                Version = all[^1].Version,
                PreviousSpirits = null,
                NewSpirits = Math.Clamp(currentSpirits, -1.0, 1.0),
                AppliedValence = null,
                SourceEvidenceEventId = null,
                OccurredAt = now,
                IsBaseline = true,
                CompactedAt = now,
            };
            db.CompanionMoodTransitions.Add(baseline);

            try
            {
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return new MoodCompactionResult(true, doomed.Count, baseline.Version);
            }
            catch (DbUpdateException) when (attempt < MaxAttempts)
            {
                // A nudge landed while we were compacting. Read again and redo the cut.
                await tx.RollbackAsync(ct);
            }
        }
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
