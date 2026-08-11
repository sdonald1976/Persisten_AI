namespace Companion.Core.Domain;

/// <summary>
/// Identity and isolation root. Every other record is scoped by <see cref="UserId"/>,
/// which is how the system enforces that one user's data never leaks into another's.
/// </summary>
public class UserProfile
{
    public string UserId { get; set; } = default!;

    /// <summary>The user's own display name (who they are), not the companion's.</summary>
    public string? DisplayName { get; set; }

    // ---- The companion's identity (who IT is). Null = use the configured default. ----

    /// <summary>Name the companion goes by (e.g. "Ava").</summary>
    public string? CompanionName { get; set; }

    /// <summary>Gender the companion presents as ("female", "male", "nonbinary", …).</summary>
    public string? CompanionGender { get; set; }

    /// <summary>Pronouns the companion uses for itself (e.g. "she/her").</summary>
    public string? CompanionPronouns { get; set; }

    /// <summary>
    /// Chosen personality preset key ("warm", "witty", …). Null = use the configured default.
    /// The preset is the base voice; <see cref="Persona"/> layers free-text tweaks on top of it.
    /// </summary>
    public string? PersonalityPreset { get; set; }

    /// <summary>Editable free-text style tweaks, layered on top of the active personality preset.</summary>
    public string? Persona { get; set; }

    /// <summary>
    /// The companion's own spirits in [-1, 1] — a slow trace of how conversations with this user
    /// have actually felt, nudged a little each emotional turn and decaying toward contentment.
    /// Lives on the profile with the rest of the companion's per-user state.
    /// </summary>
    public double CompanionSpirits { get; set; } = 0.2;

    /// <summary>When spirits were last nudged (anchors the decay).</summary>
    public DateTimeOffset? CompanionSpiritsNudgedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
