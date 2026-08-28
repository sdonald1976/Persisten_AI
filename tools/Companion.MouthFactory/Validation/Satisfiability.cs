using Companion.MouthFactory.Schema;

namespace Companion.MouthFactory.Validation;

/// <summary>Whether a scenario can be answered at all, and why not when it cannot.</summary>
public sealed record SatisfiabilityResult(bool Satisfiable, string? Code, string? Detail)
{
    public static readonly SatisfiabilityResult Ok = new(true, null, null);
}

/// <summary>
/// Can this scenario produce a reply that satisfies its own plan?
///
/// WHAT THE CONTRACT ACTUALLY SAYS. A plan carrying no items is structurally valid — Validate
/// requires none, and CompactV4 emits CONTROL and STYLE alone. The frozen run-1 corpus settles
/// what that means in practice: 127 of its 730 rows (17.4%) have no SAY items and
/// question = none, and every one has a full, natural target. One is nothing but an act and a
/// register, and its reply is "Forty bucks and a heat shield — that's the automotive equivalent
/// of a warning shot."
///
/// So an empty reply is NOT a legitimate shipping response, and no generic acknowledgment
/// permission needs inventing: those rows are answerable because the act is exercised against a
/// user message that CARRIES something. "I got the promotion!" can be acknowledged. "any news?"
/// cannot — there is nothing in the row to acknowledge, no item to state, no question licensed,
/// and no scene to narrate.
///
/// That is the fault this check names. It is a defect in scenario construction, not a limit of
/// Plan/4: the factory was generating rows where every compliant answer is contentless, then
/// counting the writer's attempt to fill the vacuum (usually by asking a question the plan
/// forbids) against its acceptance rate.
/// </summary>
public static class ScenarioSatisfiability
{
    /// <summary>
    /// Words too generic to count as something to talk about. Deliberately short: the test is
    /// "does any content word survive", not "is this interesting".
    /// </summary>
    private static readonly HashSet<string> Filler = new(StringComparer.OrdinalIgnoreCase)
    {
        "news", "any", "the", "and", "so", "what", "how", "hows", "look", "looking", "story",
        "give", "short", "version", "about", "then", "well", "okay", "yeah", "still", "going",
        "get", "got", "let", "know", "want", "one", "other", "thing", "much", "some", "that",
        "this", "there", "here", "just", "like", "with", "from", "your", "you", "for",
    };

    public static SatisfiabilityResult Check(ScenarioTruth scenario)
    {
        // 1. Anything the plan licenses the mouth to SAY.
        //    background_only is deliberately excluded: it may colour tone and must not surface,
        //    so it is not something to be about.
        var speakable = scenario.ApprovedFacts.Any(f =>
            f.Policy is FactPolicy.MustExpress or FactPolicy.MayExpress
                or FactPolicy.AdmitUnknown or FactPolicy.AskRequired);

        // 2. Things the turn must handle even with no facts attached.
        var hasCorrection = scenario.Superseded.Count > 0;
        var hasUnknown = scenario.EpistemicUnknowns.Count > 0;
        var hasAmbiguity = scenario.IntentionalAmbiguities.Count > 0;

        // 3. A licensed question is itself a speech act with content.
        var mayAsk = !scenario.Question.Policy.Equals("none", StringComparison.OrdinalIgnoreCase);

        // 4. A fiction frame licenses invented scene content (R5 §5).
        var hasFrame = scenario.Frame is not null;

        // 5. Otherwise the conversation must carry something to respond to. This is the case the
        //    frozen corpus relies on, and the one the factory was failing to provide.
        var conversational = HasContent(scenario.UserMessage)
                             || scenario.History.Any(t => HasContent(t.Text));

        if (speakable || hasCorrection || hasUnknown || hasAmbiguity || mayAsk || hasFrame || conversational)
            return SatisfiabilityResult.Ok;

        return new SatisfiabilityResult(false, "unsatisfiable",
            "no expressible item, no correction, unknown or ambiguity, no permitted question, "
            + "no frame, and nothing substantive in the conversation to respond to - every "
            + "compliant reply would be contentless");
    }

    /// <summary>
    /// Does this utterance carry anything to be about? A bare prompt ("any news?", "and?") does
    /// not; a statement ("I got the promotion!") does.
    /// </summary>
    private static bool HasContent(string? text)
        => !string.IsNullOrWhiteSpace(text)
           && text.Split([' ', ',', '.', '!', '?', ';', ':', '\n', '\t', '-'],
                   StringSplitOptions.RemoveEmptyEntries)
               .Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray()))
               .Any(w => w.Length > 2 && !Filler.Contains(w));
}
