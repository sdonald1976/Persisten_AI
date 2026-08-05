using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>
/// Proposes candidate memories from an exchange. This is the ONLY place a model gets to
/// "invent" memories — and it only proposes. Acceptance is the pipeline's job, never the
/// extractor's. Implementations may be rule-based or LLM-backed behind this same interface.
/// </summary>
public interface IMemoryExtractor
{
    Task<IReadOnlyList<MemoryCandidate>> ExtractAsync(
        string userId, IReadOnlyList<Message> exchange, CancellationToken ct = default);
}
