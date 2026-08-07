using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>Reads/writes the per-user profile, including the editable persona/style.</summary>
public interface IProfileStore
{
    Task<UserProfile> GetOrCreateAsync(string userId, CancellationToken ct = default);
    Task SetPersonaAsync(string userId, string? persona, CancellationToken ct = default);

    /// <summary>Set the chosen personality preset key (null clears it → configured default).</summary>
    Task SetPersonalityPresetAsync(string userId, string? presetName, CancellationToken ct = default);

    /// <summary>
    /// Set the companion's identity. Only non-null arguments are applied, so name and gender/pronouns
    /// can be changed independently; a null argument leaves that field as-is.
    /// </summary>
    Task SetIdentityAsync(string userId, string? name, string? gender, string? pronouns, CancellationToken ct = default);
}
