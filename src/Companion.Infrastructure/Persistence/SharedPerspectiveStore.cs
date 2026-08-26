using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Companion.Infrastructure.Persistence;

public sealed class SharedPerspectiveStore : ISharedPerspectiveStore
{
    private readonly CompanionDbContext _db;
    public SharedPerspectiveStore(CompanionDbContext db) => _db = db;

    public async Task<SharedExperiencePerspective?> AddValidatedAsync(
        SharedExperiencePerspective perspective, CancellationToken ct = default)
    {
        if (perspective.Owner == MemoryOwner.Shared || string.IsNullOrWhiteSpace(perspective.Evidence))
            return null;
        var episode = await _db.EpisodicMemories.FirstOrDefaultAsync(e =>
            e.UserId == perspective.UserId && e.Id == perspective.ExperienceId && e.Owner == MemoryOwner.Shared, ct);
        if (episode is null)
            return null;
        if (perspective.Owner == MemoryOwner.User
            && !await _db.Evidence.AnyAsync(e =>
                e.UserId == perspective.UserId
                && e.MemoryId == perspective.ExperienceId
                && e.Excerpt.Contains(perspective.Evidence), ct))
            return null;

        perspective.Id = perspective.Id == Guid.Empty ? Guid.NewGuid() : perspective.Id;
        perspective.Confidence = Math.Clamp(perspective.Confidence, 0, 1);
        _db.SharedExperiencePerspectives.Add(perspective);
        await _db.SaveChangesAsync(ct);
        return perspective;
    }

    public async Task<IReadOnlyList<SharedExperiencePerspective>> GetForExperiencesAsync(
        string userId, IReadOnlyCollection<Guid> experienceIds, CancellationToken ct = default)
        => experienceIds.Count == 0
            ? Array.Empty<SharedExperiencePerspective>()
            : await _db.SharedExperiencePerspectives
                .Where(p => p.UserId == userId && experienceIds.Contains(p.ExperienceId))
                .OrderByDescending(p => p.Confidence)
                .ToListAsync(ct);

    public async Task<int> ForgetByEvidenceAsync(
        string userId, IReadOnlyCollection<Guid> messageIds, DateTimeOffset now,
        CancellationToken ct = default)
    {
        if (messageIds.Count == 0) return 0;
        var ids = messageIds.ToHashSet();

        // A perspective comments on ONE experience, so the parent answers both questions:
        // whether this forgetting reaches it, and whether it can prove independence at all.
        var experiences = await _db.Experiences
            .Where(e => e.UserId == userId)
            .ToListAsync(ct);
        var byId = experiences.ToDictionary(e => e.Id);

        var mine = await _db.SharedExperiencePerspectives
            .Where(p => p.UserId == userId)
            .ToListAsync(ct);

        var doomed = mine.Where(p =>
        {
            var parent = byId.GetValueOrDefault(p.ExperienceId);

            // Exact identity: the parent came from a message being forgotten.
            if (parent?.EvidenceMessageId is { } m && ids.Contains(m))
                return true;

            // Legacy sweep: anything that cannot prove independence goes at the moment
            // forgetting is invoked. A world-sourced parent IS such a proof.
            return !EvidenceForgetting.PerspectiveProvesIndependence(parent);
        }).ToList();

        if (doomed.Count == 0) return 0;

        _db.SharedExperiencePerspectives.RemoveRange(doomed);
        await _db.SaveChangesAsync(ct);
        return doomed.Count;
    }
}
