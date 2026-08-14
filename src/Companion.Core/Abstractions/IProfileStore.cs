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

    /// <summary>
    /// Set what the user is called. Null or blank clears it, and she falls back to "the user".
    ///
    /// This had no writer at all: the field was read when projecting prompt identity and set by
    /// nothing — no endpoint, no configuration, no conversational path — so it was permanently
    /// null and she addressed the person she has known longest as "dear user".
    /// </summary>
    Task SetDisplayNameAsync(string userId, string? displayName, CancellationToken ct = default);

    /// <summary>Persists the companion's spirits value and the moment it was nudged.</summary>
    Task SetCompanionSpiritsAsync(string userId, double spirits, DateTimeOffset nudgedAt, CancellationToken ct = default);
}
