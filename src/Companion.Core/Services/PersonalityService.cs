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

    public PersonalityService(IOptions<PersonalityOptions> options) => _options = options.Value;

    public IReadOnlyList<PersonalityPreset> Presets => PersonalityCatalog.All;

    public PersonalityPreset? Find(string? name) => PersonalityCatalog.Find(name);

    public PersonalityPreset Active(UserProfile profile)
        => PersonalityCatalog.Find(profile.PersonalityPreset)
            ?? PersonalityCatalog.Find(_options.Default)
            ?? PersonalityCatalog.Fallback;

    public string Compose(UserProfile profile)
    {
        var preset = Active(profile);
        var custom = profile.Persona?.Trim();
        return string.IsNullOrWhiteSpace(custom)
            ? preset.Instructions
            : preset.Instructions + "\n\nExtra style the user asked for:\n" + custom;
    }
}
