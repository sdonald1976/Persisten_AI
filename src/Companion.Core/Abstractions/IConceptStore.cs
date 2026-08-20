using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>
/// Persistence for Ava-owned concept knowledge. Deliberately small: find, create, read
/// assertions, and the three lifecycle writes. Every write persists its evidence rows and a
/// MemoryRevision — provenance and audit are not optional extras here any more than they are
/// for autobiographical memory.
/// </summary>
public interface IConceptStore
{
    /// <summary>Exact lookup by canonical name or alias (both normalized). Null = never named.</summary>
    Task<Concept?> FindByNameAsync(string userId, string canonicalName, CancellationToken ct = default);

    Task AddConceptAsync(Concept concept, CancellationToken ct = default);

    /// <summary>All assertions for the concept, newest first, excluding Deleted.</summary>
    Task<IReadOnlyList<ConceptAssertion>> GetAssertionsAsync(
        string userId, Guid conceptId, CancellationToken ct = default);

    /// <summary>Persists the assertion, its evidence rows, and a Created revision; syncs the
    /// vector index.</summary>
    Task AddAssertionAsync(ConceptAssertion assertion, CancellationToken ct = default);

    /// <summary>Re-taught unchanged: bump confidence/LastConfirmed, write a Confirmed revision.</summary>
    Task ConfirmAssertionAsync(ConceptAssertion assertion, double confidence,
        DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Non-destructive replacement: the old assertion keeps its history
    /// (Validity.Superseded + SupersededById), the new one becomes current.</summary>
    Task SupersedeAssertionAsync(ConceptAssertion old, ConceptAssertion replacement,
        CancellationToken ct = default);
}
