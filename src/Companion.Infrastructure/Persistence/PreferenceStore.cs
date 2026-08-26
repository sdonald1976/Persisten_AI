using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Companion.Infrastructure.Persistence;

/// <summary>EF Core-backed store of the companion's own tastes. Evolution rules are enforced here.</summary>
public sealed class PreferenceStore : IPreferenceStore
{
    private readonly CompanionDbContext _db;

    public PreferenceStore(CompanionDbContext db) => _db = db;

    public async Task<IReadOnlyList<CompanionPreference>> GetAllAsync(
        string userId, CancellationToken ct = default)
        => await _db.CompanionPreferences
            .Where(p => p.UserId == userId)
            .OrderByDescending(p => p.UpdatedAt)
            .ToListAsync(ct);

    public async Task<CompanionPreference> ApplySignalAsync(
        string userId, string subject, double targetAffinity, string? reason,
        float[]? embedding, DateTimeOffset now,
        IReadOnlyCollection<Guid>? evidenceMessageIds = null,
        CancellationToken ct = default)
    {
        var normalized = subject.Trim();
        var existing = await _db.CompanionPreferences.FirstOrDefaultAsync(
            p => p.UserId == userId && p.Subject.ToLower() == normalized.ToLower(), ct);

        if (existing is null)
        {
            // A brand-new taste starts gently: partway toward the signal, low confidence.
            var fresh = new CompanionPreference
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Subject = normalized,
                Affinity = Math.Clamp(targetAffinity * 0.4, -1, 1),
                Confidence = 0.3,
                Reason = reason,
                Observations = 1,
                CreatedAt = now,
                UpdatedAt = now,
                Embedding = embedding,
                EvidenceMessageIdsJson = EvidenceForgetting.WriteIds(
                    evidenceMessageIds ?? []),
            };
            _db.CompanionPreferences.Add(fresh);
            await _db.SaveChangesAsync(ct);
            return fresh;
        }

        var (affinity, confidence) = PreferenceMath.Apply(existing.Affinity, existing.Confidence, targetAffinity);
        existing.Affinity = affinity;
        existing.Confidence = confidence;
        existing.Observations++;
        existing.UpdatedAt = now;
        if (!string.IsNullOrWhiteSpace(reason))
            existing.Reason = reason;
        if (embedding is not null)
            existing.Embedding = embedding;

        // Affinity accumulates across observations, so the evidence does too: each new
        // signal adds the turns that produced it.
        if (evidenceMessageIds is { Count: > 0 })
        {
            var ids = EvidenceForgetting.ReadIds(existing.EvidenceMessageIdsJson);
            ids.AddRange(evidenceMessageIds);
            existing.EvidenceMessageIdsJson = EvidenceForgetting.WriteIds(ids);
        }

        await _db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task<int> ForgetByEvidenceAsync(
        string userId, IReadOnlyCollection<Guid> messageIds, DateTimeOffset now,
        CancellationToken ct = default)
    {
        if (messageIds.Count == 0) return 0;
        var ids = messageIds.ToHashSet();

        var rows = await _db.CompanionPreferences
            .Where(p => p.UserId == userId && !p.EvidenceForgotten)
            .ToListAsync(ct);

        var n = EvidenceForgetting.ForgetCompanionPreferences(rows, ids);
        if (n > 0)
        {
            foreach (var p in rows.Where(p => p.EvidenceForgotten))
                p.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);
        }
        return n;
    }
}
