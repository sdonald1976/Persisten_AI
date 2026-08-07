namespace Companion.Core;

/// <summary>
/// The companion's own identity (who it is), bound from the "Identity" section. Distinct from the
/// personality (how it behaves): identity is name/gender/pronouns and carries across every preset
/// and into voice. Users can override it per-profile or by talking; these are the defaults.
/// </summary>
public sealed class IdentityOptions
{
    public const string SectionName = "Identity";

    /// <summary>The name the companion goes by.</summary>
    public string Name { get; set; } = "Ava";

    /// <summary>Gender the companion presents as ("female", "male", "nonbinary", or free text).</summary>
    public string Gender { get; set; } = "female";

    /// <summary>Pronouns the companion uses for itself, e.g. "she/her".</summary>
    public string Pronouns { get; set; } = "she/her";
}
