using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>
/// Resolves a user's reference to a project, evidence-based and confidence-aware. It ranks
/// candidates and, when they're too close to call, signals that a clarifying question is
/// needed rather than guessing. It never silently merges uncertain matches.
/// </summary>
public interface IEntityResolver
{
    Task<EntityResolution> ResolveProjectAsync(string userId, string reference, CancellationToken ct = default);
}
