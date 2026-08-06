using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Companion.Infrastructure.Persistence;

/// <summary>EF Core-backed store for reply ratings (future style-training signal).</summary>
public sealed class FeedbackStore : IFeedbackStore
{
    private readonly CompanionDbContext _db;

    public FeedbackStore(CompanionDbContext db) => _db = db;

    public async Task AddAsync(FeedbackRecord feedback, CancellationToken ct = default)
    {
        if (feedback.Id == Guid.Empty)
            feedback.Id = Guid.NewGuid();
        _db.Feedback.Add(feedback);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<int> CountAsync(string userId, CancellationToken ct = default)
        => await _db.Feedback.CountAsync(f => f.UserId == userId, ct);
}
