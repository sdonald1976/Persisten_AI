namespace Companion.Core.Domain;

/// <summary>
/// A named personality the companion can wear — the base "voice" prepended to the system prompt.
/// Presets are the configurable, swappable layer; a user's own free-text persona tweaks
/// (<see cref="UserProfile.Persona"/>) layer on top of whichever preset is active.
/// </summary>
public sealed record PersonalityPreset
{
    /// <summary>Short lowercase key used to select it ("warm", "witty", …).</summary>
    public required string Name { get; init; }

    /// <summary>Human-readable label ("Warm &amp; curious").</summary>
    public required string Label { get; init; }

    /// <summary>One-line description of the vibe, for menus and confirmations.</summary>
    public required string Description { get; init; }

    /// <summary>The style instructions injected into the system prompt when this preset is active.</summary>
    public required string Instructions { get; init; }
}
