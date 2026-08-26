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

        // A perspective is commentary on ONE experience. Its lineage runs through that
        // experience, so the experiences forgotten in this same sweep are what it follows.
        var forgottenExperiences = await _db.Experiences
            .Where(e => e.UserId == userId && e.EvidenceMessageId != null)
            .Select(e => new { e.Id, e.EvidenceMessageId })
            .ToListAsync(ct);
        var doomedExperienceIds = forgottenExperiences
            .Where(e => ids.Contains(e.EvidenceMessageId!.Value))
            .Select(e => e.Id)
            .ToHashSet();
        if (doomedExperienceIds.Count == 0) return 0;

        var doomed = await _db.SharedExperiencePerspectives
            .Where(p => p.UserId == userId)
            .ToListAsync(ct);
        doomed = doomed.Where(p => doomedExperienceIds.Contains(p.ExperienceId)).ToList();
        if (doomed.Count == 0) return 0;

        // Deleted: commentary on a forgotten experience has nothing left to comment on.
        _db.SharedExperiencePerspectives.RemoveRange(doomed);
        await _db.SaveChangesAsync(ct);
        return doomed.Count;
    }
}
