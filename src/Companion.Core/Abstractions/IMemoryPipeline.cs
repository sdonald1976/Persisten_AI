using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>
/// Turns proposed candidates into validated, persisted memory. Separates extraction from
/// acceptance: generate → normalize → dedupe → compare against existing → score confidence
/// → require evidence → decide (accept/merge/reject/review) → persist with an audit trail.
/// </summary>
public interface IMemoryPipeline
{
    /// <param name="resolution">The turn's consumable reference resolution, when working
    /// context produced one sound enough for durable memory (see
    /// <see cref="ReferenceResolution"/>) — guesses are never passed here.</param>
    Task<MemoryExtractionResult> ProcessAsync(
        string userId, IReadOnlyList<Message> exchange,
        ReferenceResolution? resolution = null, CancellationToken ct = default);
}
