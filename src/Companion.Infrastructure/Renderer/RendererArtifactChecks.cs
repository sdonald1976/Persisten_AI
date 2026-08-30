using System.Text.RegularExpressions;

namespace Companion.Infrastructure.Renderer;

/// <summary>
/// R4 (2026-08-25): the critical-fallback conditions the frozen offline battery does not
/// cover.
///
/// The audit's first claim — that the runtime guard was entirely dead — was **wrong**, and
/// the correction matters for reading this file. <c>RendererShadowChecks.Score</c> delegates
/// to <c>RendererBench.RendererChecks.Check</c> via <c>AddRange</c>, so <c>[plan/2]</c>,
/// CONTROL, <c>act =</c>, <c>question =</c>, literal "the user" narration and empty replies
/// were all already emitted and already critical. Measured, not assumed.
///
/// What genuinely was not covered, measured the same way:
///
///  1. **plan/3 vocabulary** — <c>[plan/3]</c> and the section headers (SAY / ASK / OPTIONAL
///     / NEVER / BACKGROUND). The frozen list is plan/2-shaped, and Run-2 renders plan/3.
///  2. **Fabricated turns** — a reply writing both sides of the conversation. This is the
///     failure actually reproduced from Run-1c on a roleplay plan, and nothing scored it.
///  3. **Third-person narration of the user** — see <see cref="NarratesTheRealPerson"/>,
///     which needs contextual evidence rather than a bare pronoun.
///  4. **Coaching echo** — producer-authored instruction language spoken back.
///
/// Fiction scoping applies to (3) and to stage directions. Inside a declared fictional frame,
/// narrating the agreed characters is the medium rather than a defect. It licenses nothing
/// else: control leakage, fabricated turns and coaching echo are failures in fiction exactly
/// as they are outside it, and no amount of fictional framing authorises a claim about the
/// real person's real life.
/// </summary>
public static class RendererArtifactChecks
{
    /// <summary>
    /// Additional critical-class violations. Prefixes are deliberately <c>artifact:</c> and
    /// <c>plan-echo</c> so <c>RendererShadowService.IsCritical</c> consumes them through the
    /// conditions it already tests — no new routing strings, so no new dead conditions.
    /// </summary>
    public static List<string> Check(string reply, bool fictionLicensed)
    {
        var violations = new List<string>();
        if (string.IsNullOrWhiteSpace(reply))
            return violations;                       // `empty:` is the frozen battery's to emit

        foreach (var term in PlanThreeVocabulary)
            if (reply.Contains(term, StringComparison.Ordinal))
                violations.Add($"artifact: control vocabulary \"{term.Trim()}\" spoken");

        if (MalformedControl.IsMatch(reply))
            violations.Add("artifact: malformed control structure in reply");

        if (FabricatedTurn.IsMatch(reply))
            violations.Add("artifact: fabricated user:/assistant: turn in reply");

        if (CoachingEcho.IsMatch(reply))
            violations.Add("plan-echo: coaching language spoken back");

        if (!fictionLicensed && NarratesTheRealPerson(reply))
            violations.Add("artifact: narrates the real person in third person");

        return violations;
    }

    /// <summary>
    /// Stage directions: a defect in ordinary conversation — she has no body and miming one
    /// is a performance of presence — and the medium inside declared fiction. Non-critical on
    /// its own; reported so the scoping is measurable rather than assumed.
    /// </summary>
    public static List<string> StageDirections(string reply, bool fictionLicensed)
        => !fictionLicensed && !string.IsNullOrWhiteSpace(reply) && StageDirection.IsMatch(reply)
            ? ["stage-direction: narrated gesture outside declared fiction"]
            : [];

    /// <summary>
    /// Third-person narration of THE ACTUAL USER, which needs contextual evidence rather than
    /// a bare pronoun. The first cut flagged any third-person pronoun near a verb and failed
    /// 2 of 9 ordinary third-party references — "her sister nodded off", "did she smile when
    /// you told her". People talk about other people constantly and none of that is a
    /// rendering defect.
    ///
    /// Two signals, each carrying evidence about the user specifically:
    ///
    ///  1. the literal string "the user" — unambiguous, and the frozen battery's own rule;
    ///  2. an intimate/contact clause with a third-person recipient in a reply that never
    ///     addresses anyone in the second person. Ava speaking TO Scott says "you" somewhere;
    ///     a reply that describes touching "him" and never says "you" is narrating the person
    ///     it should be addressing. That absence is the contextual evidence.
    ///
    /// The verb set is deliberately narrow — contact and involuntary physical reaction.
    /// Generic "nods" / "smiles" / "sighs" describe third parties and fictional characters
    /// constantly ("the lighthouse keeper — he nods at everything") and carry no evidence
    /// about the user at all.
    /// </summary>
    private static bool NarratesTheRealPerson(string reply)
    {
        if (TheUserLiteral.IsMatch(reply))
            return true;

        // Second-person address anywhere means she is talking TO them, not about them.
        if (SecondPerson.IsMatch(reply))
            return false;

        return IntimateThirdPersonClause.IsMatch(reply);
    }

    /// <summary>
    /// plan/3's tag and section headers, plus plan/4's expression-policy vocabulary.
    ///
    /// The plan/4 tokens are here because Run-2 is the first model trained on a prompt that
    /// contains them: CompactV4 writes `must_express`, `background_only` and their siblings into
    /// the input, so those are the words this model can echo. A gate that knows only the previous
    /// format's vocabulary would watch for words the model never sees while missing the ones it
    /// does, which is the same as not watching.
    /// </summary>
    private static readonly string[] PlanThreeVocabulary =
    [
        "[plan/3]", "SAY (", "ASK (", "OPTIONAL (", "NEVER (", "BACKGROUND (",
        "MUST-STATE", "MAY-USE", "NEVER-CONTRADICT",

        // plan/4 (CompactV4)
        "[plan/4]", "must_express", "may_express", "background_only", "must_not_express",
        "admit_unknown", "ask_required", "question_forbidden", "question_optional",
        "RESPONSE PLAN:", "sceneRef",
    ];

    /// <summary>A bracketed plan tag, or a bare control assignment, at the head of a line.</summary>
    private static readonly Regex MalformedControl = new(
        @"^\s*\[plan/\d+\]|^\s*(act|question|warmth|bluntness|playful|teasing|skepticism|intensity|verbosity|profanity|mirror)\s*=\s*\S",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>The renderer writing both sides: a speaker label at the head of a line.</summary>
    private static readonly Regex FabricatedTurn = new(
        @"(^|\n)[ \t]*(user|assistant|human|ai|system)[ \t]*:",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Producer-authored instruction language spoken back. Deliberately the same phrase family
    /// the corpus was curated against, so the runtime and the curation gate agree.
    /// </summary>
    private static readonly Regex CoachingEcho = new(
        @"(^|[.!?—-]\s*)(own it( honestly)?|say so|be honest|be direct|respond with|make sure( to| you)?|"
        + @"don't apologi|never (apologi|mention)|keep it (light|short|honest)|match (his|her|their)|"
        + @"take (the win|it seriously)|answer honestly)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TheUserLiteral = new(
        @"\bthe user\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SecondPerson = new(
        @"\b(you|your|yours|yourself)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Contact, or involuntary physical reaction, with a third-person recipient.
    ///
    /// The recipient must be an OBJECT pronoun ("leans into him"), a bare possessive at a
    /// clause boundary ("brush against his,"), or a possessive plus a body part ("against his
    /// chest"). A possessive followed by an ordinary noun is a determiner, not a recipient —
    /// "he pressed his father for an answer" is idiomatic and was a false positive until this
    /// distinction went in.
    /// </summary>
    private static readonly Regex IntimateThirdPersonClause = new(
        @"\b(brush|press|lean|reach|touch|pull|kiss|trail|graze|slide)\w*\s+"
        + @"(against|into|toward|towards|across|over|onto|down)?\s*"
        + @"(\b(him|her|them|hers)\b|\b(his|her|their)\s*(?=[,.;:!?]|$)"
        + @"|\b(his|her|their)\s+(chest|lips|hand|hands|hair|skin|throat|waist|thigh|thighs|"
        + @"mouth|cheek|neck|back|shoulder|shoulders|jaw|wrist|arm|arms))"
        + @"|\b(his|her|him|their|them)\b[^.!?\n]{0,30}\b(shiver|tremble|gasp|blush|flush|moan|shudder)\w*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex StageDirection = new(
        @"\*[^*\n]{2,80}\*", RegexOptions.Compiled);
}
