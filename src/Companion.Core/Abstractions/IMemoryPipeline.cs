using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>
/// Turns proposed candidates into validated, persisted memory. Separates extraction from
/// acceptance: generate → normalize → dedupe → compare against existing → score confidence
/// → require evidence → decide (accept/merge/reject/review) → persist with an audit trail.
/// </summary>
public interface IMemoryPipeline
{
    Task<MemoryExtractionResult> ProcessAsync(
        string userId, IReadOnlyList<Message> exchange, CancellationToken ct = default);
}
