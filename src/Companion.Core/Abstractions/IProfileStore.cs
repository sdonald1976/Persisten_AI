using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>Reads/writes the per-user profile, including the editable persona/style.</summary>
public interface IProfileStore
{
    Task<UserProfile> GetOrCreateAsync(string userId, CancellationToken ct = default);
    Task SetPersonaAsync(string userId, string? persona, CancellationToken ct = default);
}
