using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>
/// Resolves the companion's active personality and composes the persona text for the system prompt.
/// The active preset comes from the user's choice, falling back to the configured default; the
/// user's free-text persona tweaks layer on top of it.
/// </summary>
public interface IPersonalityService
{
    /// <summary>The available presets, in menu order.</summary>
    IReadOnlyList<PersonalityPreset> Presets { get; }

    /// <summary>Resolve a preset by key or label; null if nothing matches.</summary>
    PersonalityPreset? Find(string? name);

    /// <summary>The preset currently active for this profile (their choice, else the configured default).</summary>
    PersonalityPreset Active(UserProfile profile);

    /// <summary>The companion's identity for this profile (their overrides, else the configured defaults).</summary>
    CompanionIdentity Identity(UserProfile profile);

    /// <summary>
    /// The full persona text for the system prompt: the companion's identity, then the active
    /// personality preset, then any free-text tweaks.
    /// </summary>
    string Compose(UserProfile profile);
}
