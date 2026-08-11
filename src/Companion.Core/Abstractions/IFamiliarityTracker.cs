using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>Derives how far along the relationship is (tenure + real interaction depth).</summary>
public interface IFamiliarityTracker
{
    Task<FamiliaritySnapshot> BuildAsync(string userId, CancellationToken ct = default);
}
