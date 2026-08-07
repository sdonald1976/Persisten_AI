using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Microsoft.Extensions.Options;

namespace Companion.Core.Services;

/// <summary>
/// Default <see cref="IPersonalityService"/>: presets come from <see cref="PersonalityCatalog"/>,
/// the default preset from <see cref="PersonalityOptions"/>, and the composed persona is the active
/// preset's instructions with the user's own free-text tweaks appended (so "be more concise" still
/// stacks on top of, say, the Witty personality).
/// </summary>
public sealed class PersonalityService : IPersonalityService
{
    private readonly PersonalityOptions _options;
    private readonly IdentityOptions _identity;

    public PersonalityService(IOptions<PersonalityOptions> options, IOptions<IdentityOptions> identity)
    {
        _options = options.Value;
        _identity = identity.Value;
    }

    public IReadOnlyList<PersonalityPreset> Presets => PersonalityCatalog.All;

    public PersonalityPreset? Find(string? name) => PersonalityCatalog.Find(name);

    public PersonalityPreset Active(UserProfile profile)
        => PersonalityCatalog.Find(profile.PersonalityPreset)
            ?? PersonalityCatalog.Find(_options.Default)
            ?? PersonalityCatalog.Fallback;

    public CompanionIdentity Identity(UserProfile profile) => new()
    {
        Name = Coalesce(profile.CompanionName, _identity.Name),
        Gender = Coalesce(profile.CompanionGender, _identity.Gender),
        Pronouns = Coalesce(profile.CompanionPronouns, _identity.Pronouns),
    };

    public string Compose(UserProfile profile)
    {
        var identity = Identity(profile).Describe();
        var preset = Active(profile);
        var custom = profile.Persona?.Trim();

        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(identity))
            sb.Append(identity).Append("\n\n");
        sb.Append(preset.Instructions);
        if (!string.IsNullOrWhiteSpace(custom))
            sb.Append("\n\nExtra style the user asked for:\n").Append(custom);
        return sb.ToString();
    }

    private static string Coalesce(string? a, string b) => string.IsNullOrWhiteSpace(a) ? b : a.Trim();
}
