using System.Text.RegularExpressions;

namespace Companion.Core.Services;

/// <summary>
/// Deterministic tool selection for unambiguous phrasings — the same rules-first philosophy as
/// the intent parser, applied to lookups. When the user plainly asks "can you see images?" or
/// "why did you say that?", the right tool is not a judgment call, and a small model's judgment
/// is exactly what can't be relied on (it politely declines and then confabulates). A matched
/// nudge runs BEFORE the model is consulted, so the obvious cases work even if the model never
/// cooperates with the decision protocol; the model loop still runs afterwards for anything
/// the rules can't see. Deliberately narrow: a missed nudge costs nothing (the loop remains),
/// a wrong nudge wastes one read-only lookup.
/// </summary>
public static partial class ToolNudge
{
    public sealed record Match(string Tool, string ArgumentsJson);

    [GeneratedRegex(
        @"\b(?:what (?:can|do) you (?:actually |really )?do\b|are you (?:able|capable) (?:to|of)|" +
        @"can you (?:actually |really )?(?:see|view|look at|hear|listen|speak|talk)\b|" +
        @"what are you capable of|what are your (?:capabilities|abilities))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CapabilityRx();

    [GeneratedRegex(
        @"\b(?:why did you (?:say|mention|bring|ask)|what (?:were|are) you (?:working|going) (?:from|off)|" +
        @"where did (?:that|this) come from|what made you (?:say|bring|mention))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DiagnosticsRx();

    [GeneratedRegex(
        @"\b(?:anything (?:unfinished|open|pending|unresolved)|what(?:'s| is) (?:unfinished|open|pending|unresolved)|" +
        @"what are you (?:wondering|curious) about|open loops?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OpenLoopsRx();

    [GeneratedRegex(
        @"\b(?:what do you (?:actually |really )?(?:like|enjoy|prefer)|what are your (?:favorite|favourites|tastes|opinions)|" +
        @"your favorite things|what do you think about doing)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PreferencesRx();

    // Mid-sentence recall phrasings only — messages STARTING with "what do you remember…" are
    // already a whole-turn Recall intent and never reach the tool loop.
    [GeneratedRegex(
        @"\b(?:do you remember|did we (?:ever )?(?:talk|chat) about|remind me (?:what|about|how))\s+(?<topic>[^.!?\n]{3,200})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MemoryRx();

    [GeneratedRegex(
        @"\b(?:what procedures? (?:have i|did i) (?:taught|teach)|how do i (?:normally|usually|like to)|" +
        @"the way i (?:taught|showed) you)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProcedureRx();

    [GeneratedRegex(
        @"\b(?:what projects? (?:am i|are we) (?:working on|tracking)|list (?:my|the) projects)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProjectsRx();

    public static Match? Detect(string? userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return null;

        if (CapabilityRx().IsMatch(userMessage))
            return new Match("capability.list", "{}");
        if (DiagnosticsRx().IsMatch(userMessage))
            return new Match("diagnostics.last_turn", "{}");
        if (OpenLoopsRx().IsMatch(userMessage))
            return new Match("openloop.list", "{}");
        if (PreferencesRx().IsMatch(userMessage))
            return new Match("preference.list", "{}");
        if (ProjectsRx().IsMatch(userMessage))
            return new Match("project.get", "{}");
        if (ProcedureRx().IsMatch(userMessage))
            return new Match("procedure.search",
                System.Text.Json.JsonSerializer.Serialize(
                    new { query = userMessage.Length <= 200 ? userMessage : userMessage[..200] }));

        var memory = MemoryRx().Match(userMessage);
        if (memory.Success)
        {
            var topic = memory.Groups["topic"].Value.Trim().TrimEnd('?', '.', '!', ',');
            if (topic.Length >= 3)
                return new Match("memory.search",
                    System.Text.Json.JsonSerializer.Serialize(new { query = topic }));
        }

        return null;
    }
}
