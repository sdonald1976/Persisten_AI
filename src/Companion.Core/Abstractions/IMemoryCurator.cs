using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>
/// Temporal revision and user corrections for memories. Nothing here erases history silently:
/// supersession keeps the old fact (marked not-current), deletion is soft (and purges the
/// embedding so it can't resurface), and every operation writes a <see cref="MemoryRevision"/>.
/// </summary>
public interface IMemoryCurator
{
    /// <summary>
    /// Marks <paramref name="oldId"/> as superseded by a new current fact, keeping the old one
    /// as history (not presented as current). Persists <paramref name="replacement"/> as Active.
    /// </summary>
    Task SupersedeSemanticAsync(
        string userId, Guid oldId, SemanticMemory replacement, string reason, CancellationToken ct = default);

    /// <summary>Corrects a semantic memory's value in place (a fix, not a temporal change).</summary>
    Task<bool> CorrectSemanticAsync(
        string userId, Guid id, string newValue, string newNormalizedFact, CancellationToken ct = default);

    /// <summary>Re-associates a memory with a different project (or none). Used to fix mis-attribution.</summary>
    Task<bool> ReassignMemoryProjectAsync(
        string userId, Guid memoryId, string? newProject, CancellationToken ct = default);

    /// <summary>Soft-deletes a memory: status Deleted, embedding purged, audit entry written.</summary>
    Task<bool> ForgetAsync(string userId, Guid memoryId, string reason, CancellationToken ct = default);

    /// <summary>Flags a memory as disputed ("that's wrong") pending resolution; demoted from retrieval.</summary>
    Task<bool> DisputeAsync(string userId, Guid memoryId, string reason, CancellationToken ct = default);

    /// <summary>Merges a duplicate memory into another: moves evidence to the target, soft-deletes the source.</summary>
    Task<bool> MergeAsync(string userId, Guid sourceId, Guid targetId, CancellationToken ct = default);

    /// <summary>
    /// Resolves a parked <see cref="MemoryStatus.Candidate"/> (from a Phase-3 contradiction):
    /// accept promotes it to Active and supersedes the conflicting slot fact; reject soft-deletes it.
    /// </summary>
    Task<bool> ResolveReviewAsync(string userId, Guid candidateId, bool accept, CancellationToken ct = default);
}
