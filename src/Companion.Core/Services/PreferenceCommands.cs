using System.Text.RegularExpressions;

namespace Companion.Core.Services;

/// <summary>
/// The ONLY thing that turns a user's words into a structured preference (Source 3), and
/// it is deliberately a closed list: each pattern is an explicit instruction with exactly
/// one reading, mapped to a closed-set register value from the plan/3 schema. Anything it
/// does not recognize produces NOTHING durable — the legacy persona blob still gets its
/// line, unchanged, but no record is created. Ambiguity is not resolved here; it is
/// declined here.
///
/// This is not an intent parser and adds no routing: it only interprets directives that
/// the existing intent rules already deliver to the style path. A bare "don't swear" is
/// a chat turn today and stays one — capturing preferences from open conversation is a
/// cognition-layer job, recorded as a blocker, not simulated with more phrases.
/// </summary>
public static partial class PreferenceCommands
{
    /// <summary>A recognized explicit command: set a register preference, or revoke one.</summary>
    public sealed record Command(
        CommandAction Action,
        string Dimension,
        string? Value,          // closed-set token; null for Revoke
        bool Restrictive);

    public enum CommandAction { Set, Revoke }

    private const RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // Revocation FIRST: "you can swear again" contains "swear" and must never read as a
    // new profanity preference — it is the end of one.
    [GeneratedRegex(@"\b(?:you\s+(?:can|may)|it'?s\s+(?:ok(?:ay)?|fine)\s+to|feel\s+free\s+to)\s+(?:swear|curse|cuss)\b|\bswearing\s+is\s+(?:ok(?:ay)?|fine)\b", O)]
    private static partial Regex ProfanityRevokeRx();

    [GeneratedRegex(@"\b(?:don'?t|do\s+not|stop|never|no\s+more)\s+(?:swear|curs|cuss)(?:e|ing)?\b|\bno\s+(?:swearing|profanity|cursing)\b", O)]
    private static partial Regex ProfanityForbidRx();

    [GeneratedRegex(@"\bmirror\s+my\s+(?:profanity|swearing|language)\b", O)]
    private static partial Regex ProfanityMirrorRx();

    [GeneratedRegex(@"\bbe\s+(?:more\s+)?(?:concise|brief)\b|\bbe\s+shorter\b|\bkeep\s+(?:it|your\s+(?:answers|replies|responses))\s+(?:short|brief|concise)\b", O)]
    private static partial Regex VerbosityShortRx();

    [GeneratedRegex(@"\b(?:give\s+me|i\s+(?:want|prefer))\s+more\s+detail(?:s)?\b|\bbe\s+more\s+(?:detailed|thorough)\b", O)]
    private static partial Regex VerbosityExpansiveRx();

    [GeneratedRegex(@"\bbe\s+(?:more\s+)?warm(?:er)?\b", O)]
    private static partial Regex WarmthRx();

    /// <summary>The single interpretation entry point. Null = not an explicit preference
    /// command; nothing durable happens.</summary>
    public static Command? Interpret(string? directive)
    {
        var text = (directive ?? string.Empty).Trim();
        if (text.Length == 0)
            return null;

        if (ProfanityRevokeRx().IsMatch(text))
            return new Command(CommandAction.Revoke, "profanity", null, Restrictive: false);
        if (ProfanityForbidRx().IsMatch(text))
            return new Command(CommandAction.Set, "profanity", "forbidden", Restrictive: true);
        if (ProfanityMirrorRx().IsMatch(text))
            return new Command(CommandAction.Set, "profanity", "mirror-only", Restrictive: false);
        if (VerbosityShortRx().IsMatch(text))
            return new Command(CommandAction.Set, "verbosity", "short", Restrictive: false);
        if (VerbosityExpansiveRx().IsMatch(text))
            return new Command(CommandAction.Set, "verbosity", "expansive", Restrictive: false);
        if (WarmthRx().IsMatch(text))
            return new Command(CommandAction.Set, "warmth", "warm", Restrictive: false);

        return null;
    }
}
