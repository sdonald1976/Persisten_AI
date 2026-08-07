namespace Companion.Core.Domain;

/// <summary>
/// Identity and isolation root. Every other record is scoped by <see cref="UserId"/>,
/// which is how the system enforces that one user's data never leaks into another's.
/// </summary>
public class UserProfile
{
    public string UserId { get; set; } = default!;
    public string? DisplayName { get; set; }

    /// <summary>
    /// Chosen personality preset key ("warm", "witty", …). Null = use the configured default.
    /// The preset is the base voice; <see cref="Persona"/> layers free-text tweaks on top of it.
    /// </summary>
    public string? PersonalityPreset { get; set; }

    /// <summary>Editable free-text style tweaks, layered on top of the active personality preset.</summary>
    public string? Persona { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
