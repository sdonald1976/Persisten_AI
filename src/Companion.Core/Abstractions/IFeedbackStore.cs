using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>Persists reply ratings — the training signal for later style fine-tuning.</summary>
public interface IFeedbackStore
{
    Task AddAsync(FeedbackRecord feedback, CancellationToken ct = default);
    Task<int> CountAsync(string userId, CancellationToken ct = default);
}
