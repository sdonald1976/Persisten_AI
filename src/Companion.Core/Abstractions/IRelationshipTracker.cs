using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>
/// Derives a read-only <see cref="RelationshipSnapshot"/> — how things have been feeling lately —
/// from the emotional-signal log. Purely computed on demand, so it can never drift from the truth.
/// </summary>
public interface IRelationshipTracker
{
    Task<RelationshipSnapshot> BuildAsync(string userId, CancellationToken ct = default);
}
