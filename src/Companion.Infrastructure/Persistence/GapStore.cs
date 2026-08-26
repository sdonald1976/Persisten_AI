using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Companion.Infrastructure.Persistence;

public sealed class GapStore : IGapStore
{
    private readonly CompanionDbContext _db;

    public GapStore(CompanionDbContext db) => _db = db;

    public async Task<(KnowledgeGap Gap, bool Created)> ObserveAsync(
        string userId, GapKind kind, string subject, GapSource source, Guid? sourceRef,
        DateTimeOffset now, Guid? evidenceMessageId = null, CancellationToken ct = default)
    {
        var existing = await _db.KnowledgeGaps.FirstOrDefaultAsync(
            g => g.UserId == userId && g.Kind == kind && g.Subject == subject
                && (g.Status == GapStatus.Open || g.Status == GapStatus.Pursuing), ct);
        if (existing is not null)
        {
            existing.Occurrences++;
            existing.LastSeen = now;
            // A gap accumulates evidence: every turn that showed it adds a parent, and
            // Occurrences is the count of them. Forgetting one leaves the rest standing.
            if (evidenceMessageId is { } add)
            {
                var ids = EvidenceForgetting.ReadIds(existing.EvidenceMessageIdsJson);
                if (!ids.Contains(add))
                {
                    ids.Add(add);
                    existing.EvidenceMessageIdsJson = EvidenceForgetting.WriteIds(ids);
                }
            }
            await _db.SaveChangesAsync(ct);
            return (existing, false);
        }

        var gap = new KnowledgeGap
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Kind = kind,
            Subject = subject,
            Source = source,
            SourceRef = sourceRef,
            FirstSeen = now,
            LastSeen = now,
            EvidenceMessageIdsJson = evidenceMessageId is { } first
                ? EvidenceForgetting.WriteIds([first])
                : "[]",
        };
        _db.KnowledgeGaps.Add(gap);
        await _db.SaveChangesAsync(ct);
        return (gap, true);
    }

    public async Task<IReadOnlyList<KnowledgeGap>> GetOpenAsync(
        string userId, CancellationToken ct = default)
        => await _db.KnowledgeGaps.AsNoTracking()
            .Where(g => g.UserId == userId && g.Status == GapStatus.Open)
            .OrderByDescending(g => g.Occurrences).ThenBy(g => g.FirstSeen)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<KnowledgeGap>> GetRecentAsync(
        string userId, int count, CancellationToken ct = default)
        => await _db.KnowledgeGaps.AsNoTracking()
            .Where(g => g.UserId == userId)
            .OrderByDescending(g => g.LastSeen)
            .Take(Math.Clamp(count, 1, 200))
            .ToListAsync(ct);

    public async Task PromoteAsync(
        string userId, Guid gapId, Guid curiosityId, CancellationToken ct = default)
    {
        var gap = await _db.KnowledgeGaps.FirstOrDefaultAsync(
            g => g.Id == gapId && g.UserId == userId, ct);
        if (gap is null)
            return;
        gap.Status = GapStatus.Pursuing;
        gap.CuriosityId = curiosityId;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<int> SatisfyBySubjectAsync(
        string userId, string subject, string resolutionNote, CancellationToken ct = default)
    {
        var gaps = await _db.KnowledgeGaps
            .Where(g => g.UserId == userId && g.Subject == subject
                && (g.Status == GapStatus.Open || g.Status == GapStatus.Pursuing))
            .ToListAsync(ct);
        foreach (var gap in gaps)
        {
            gap.Status = GapStatus.Satisfied;
            gap.ResolutionNote = resolutionNote;
            if (gap.CuriosityId is { } curiosityId)
            {
                var curiosity = await _db.Curiosities.FirstOrDefaultAsync(
                    c => c.Id == curiosityId && c.UserId == userId, ct);
                if (curiosity is not null && curiosity.Status != CuriosityStatus.Satisfied)
                    curiosity.Status = CuriosityStatus.Satisfied;
            }
        }
        if (gaps.Count > 0)
            await _db.SaveChangesAsync(ct);
        return gaps.Count;
    }

    public async Task<int> ExpireStaleAsync(
        string userId, DateTimeOffset olderThan, CancellationToken ct = default)
    {
        var stale = await _db.KnowledgeGaps
            .Where(g => g.UserId == userId && g.LastSeen < olderThan
                && (g.Status == GapStatus.Open || g.Status == GapStatus.Pursuing))
            .ToListAsync(ct);
        foreach (var gap in stale)
        {
            gap.Status = GapStatus.Expired;
            gap.ResolutionNote ??= "aged out unpursued";
        }
        if (stale.Count > 0)
            await _db.SaveChangesAsync(ct);
        return stale.Count;
    }

    public async Task<int> ForgetByEvidenceAsync(
        string userId, IReadOnlyCollection<Guid> messageIds, DateTimeOffset now,
        CancellationToken ct = default)
    {
        if (messageIds.Count == 0) return 0;
        var ids = messageIds.ToHashSet();

        var rows = await _db.KnowledgeGaps
            .Where(g => g.UserId == userId && g.Status != GapStatus.EvidenceForgotten)
            .ToListAsync(ct);

        var n = EvidenceForgetting.ForgetKnowledgeGaps(rows, ids);
        if (n > 0) await _db.SaveChangesAsync(ct);
        return n;
    }
}
