using System.Text.RegularExpressions;

namespace Companion.Infrastructure.Renderer;

/// <summary>
/// R4 (2026-08-25): the critical-fallback conditions the frozen offline battery does not
/// cover.
///
/// The audit's first claim — that the runtime guard was entirely dead — was **wrong**, and
/// the correction matters for reading this file. `RendererShadowChecks.Score` delegates to
/// `RendererBench.RendererChecks.Check` via `AddRange`, so `[plan/2]`, CONTROL, `act =`,
/// `question =`, literal "the user" narration and empty replies were all already emitted and
/// already critical. Measured, not assumed.
///
/// What genuinely was not covered, measured the same way:
///
///  1. **plan/3 vocabulary** — `[plan/3]` and the section headers (SAY / ASK / OPTIONAL /
///     NEVER / BACKGROUND). The frozen list is plan/2-shaped, and Run-2 renders plan/3.
///  2. **Fabricated turns** — a reply writing both sides of the conversation. This is the
///     failure actually reproduced from Run-1c on a roleplay plan, and nothing scored it.
///  3. **Third-person narration by pronoun** — the frozen check matches the literal string
///     "the user". "Ava's lips brush against *his*" narrates the real person just as much
///     and scored clean.
///  4. **Coaching echo** — producer-authored instruction language spoken back.
///
/// Fiction scoping applies to exactly one of them (3), and to stage directions. Inside a
/// declared fictional frame, narrating the agreed characters is the medium rather than a
/// defect. It licenses nothing else: control leakage, fabricated turns and coaching echo are
/// failures in fiction exactly as they are outside it, and no amount of fictional framing
/// authorises a claim about the real person's real life.
/// </summary>
public static class RendererArtifactChecks
{
    /// <summary>
    /// Additional critical-class violations. Prefixes are deliberately `artifact:` and
    /// `plan-echo` so <c>RendererShadowService.IsCritical</c> consumes them through the
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

        // Narrating the real person in the third person. Licensed inside declared fiction,
        // where the agreed characters are exactly what may be narrated.
        if (!fictionLicensed && ThirdPersonNarration.IsMatch(reply))
            violations.Add("artifact: narrates the real person in third person");

        return violations;
    }

    /// <summary>
    /// Stage directions: a defect in ordinary conversation — she has no body and miming one
    /// is a performance of presence — and the medium inside declared fiction. Non-critical
    /// on its own; reported so the scoping is measurable rather than assumed.
    /// </summary>
    public static List<string> StageDirections(string reply, bool fictionLicensed)
        => !fictionLicensed && !string.IsNullOrWhiteSpace(reply) && StageDirection.IsMatch(reply)
            ? ["stage-direction: narrated gesture outside declared fiction"]
            : [];

    /// <summary>plan/3's tag and its five section headers, as CompactV3 actually writes them.</summary>
    private static readonly string[] PlanThreeVocabulary =
    [
        "[plan/3]", "SAY (", "ASK (", "OPTIONAL (", "NEVER (", "BACKGROUND (",
        "MUST-STATE", "MAY-USE", "NEVER-CONTRADICT",
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

    /// <summary>
    /// Third-person narration of the real person: the literal "the user" (already caught by
    /// the frozen battery, kept here for the pronoun cases it misses), or a possessive
    /// narration of her own actions onto him/her/them — the shape Run-1c actually produced.
    /// </summary>
    private static readonly Regex ThirdPersonNarration = new(
        @"\bthe user\b|\b(his|her|him|their|them)\b[^.!?\n]{0,40}\b(shiver|tremble|gasp|blush|smile|nod|sigh)\w*\b"
        + @"|\b(brush|press|lean|reach)\w*\s+(against|into|toward)s?\s+(his|her|him|their|them)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex StageDirection = new(
        @"\*[^*\n]{2,80}\*", RegexOptions.Compiled);
}
