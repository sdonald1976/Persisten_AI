using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>
/// Rolls up repeated, related low-level memories into higher-level knowledge. Consolidation
/// preserves links to the supporting evidence and never destroys the originals; it also refuses
/// to overgeneralize from just one or two remarks.
/// </summary>
public interface IMemoryConsolidator
{
    Task<ConsolidationResult> ConsolidateAsync(string userId, CancellationToken ct = default);
}
