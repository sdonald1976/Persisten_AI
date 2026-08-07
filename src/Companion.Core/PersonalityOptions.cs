namespace Companion.Core;

/// <summary>
/// Configuration for the companion's personality, bound from the "Personality" section. Picks the
/// default preset used when a user hasn't chosen one; the preset catalog itself lives in code
/// (<see cref="Services.PersonalityCatalog"/>), and users can still layer their own free-text tweaks.
/// </summary>
public sealed class PersonalityOptions
{
    public const string SectionName = "Personality";

    /// <summary>Preset key applied by default: "warm", "witty", "direct", "playful", or "sage".</summary>
    public string Default { get; set; } = "warm";
}
