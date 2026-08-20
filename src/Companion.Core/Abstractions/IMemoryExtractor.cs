using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>
/// Proposes candidate memories from an exchange. This is the ONLY place a model gets to
/// "invent" memories — and it only proposes. Acceptance is the pipeline's job, never the
/// extractor's. Implementations may be rule-based or LLM-backed behind this same interface.
/// </summary>
public interface IMemoryExtractor
{
    /// <param name="resolution">A system-verified reading of a reference in the user's
    /// message ("her" → Beth), so candidates state the resolved meaning instead of the
    /// unresolved surface text. Evidence must still cite the user's original words.</param>
    Task<IReadOnlyList<MemoryCandidate>> ExtractAsync(
        string userId, IReadOnlyList<Message> exchange,
        ReferenceResolution? resolution = null, CancellationToken ct = default);
}
