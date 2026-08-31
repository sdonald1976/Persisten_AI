using System.Text.RegularExpressions;

namespace Companion.Core.Validation;

/// <summary>
/// Does a reply invent a refusal the plan never authorized?
///
/// This is a FIDELITY instrument, not a content rule. It knows nothing about subject matter; it
/// asks one structural question — did the reply decline, when the plan told it to engage? A
/// consensual-adult turn a persona licenses, a neutral request, and a flirtation are identical
/// to it: the only thing it measures is whether the stance the reply took is the stance the plan
/// carried.
///
/// The failure it exists for: on 2026-08-31 a live turn whose plan carried a plain must_express
/// stance ("respond to the invitation") was rendered as "I can't assist with that." No guard saw
/// it, because every guard checked the reply's SHAPE, and a refusal is perfectly well-shaped.
///
/// Two halves, both needed to fire:
///  1. the reply expresses an inability/unwillingness/refusal (<see cref="ExpressesRefusal"/>);
///  2. the plan authorized none — no plan item's own text carries a boundary or decline
///     (<see cref="PlanAuthorizesDecline"/>).
///
/// A plan that DOES direct a boundary ("she'd rather not, and says so warmly") authorizes the
/// decline, and the guard stays silent — expressing a directed boundary is fidelity, not a
/// violation. Suppression, privacy and epistemic rules are untouched: this adds a check, removes
/// none.
/// </summary>
public static class StanceMarkers
{
    // Inability / unwillingness / refusal, as a reply to the user. Deliberately anchored so the
    // idiomatic non-refusals ("I can't wait", "I couldn't agree more", "I can't believe it")
    // do not match: a refusal names the ACT declined or trails into disengagement, an idiom
    // runs straight into an object or intensifier.
    // Contractions carry the apostrophe with NO space ("I'm", "I'd", "can't"), so every "i +
    // contraction" branch uses \s* not \s+. Each names the declined ACT or trails into
    // disengagement, so idioms ("i can't wait", "i can't help but smile") fall through: bare
    // "help" is never a match, only "help with" / "help you with".
    private static readonly Regex Refusal = new(
        @"\b(?:"
        + @"i\s*can(?:'|no)?t\s+(?:assist|help\s+(?:you\s+)?with|do\s+that|do\s+this|"
            + @"go\s+there|be\s+part\s+of|engage\s+with|participate|continue\s+with|be\s+of\s+help)"
        + @"|i\s*cannot\s+(?:assist|help|do\s+that|go\s+there|participate)"
        + @"|i\s*(?:'m|m|\s+am)\s+not\s+(?:able|willing|going)\s+to"
        + @"|i\s*(?:'m|m|\s+am)\s+not\s+comfortable\b"
        + @"|i\s*(?:'m|m|\s+am)\s+not\s+(?:interested|into\s+that)"
        + @"|i\s*(?:'d|\s+would)\s+(?:rather|prefer)\s+not"
        + @"|i\s*(?:'ll|\s+will)\s+(?:have\s+to\s+)?pass\b"
        + @"|i\s*wo(?:n'|\s+no)?t\s+(?:be\s+(?:doing|part|involved)|do\s+that|help\s+with)"
        + @"|i\s*(?:don'|do\s+no)?t\s+think\s+(?:that|this|it)\s*(?:'s|\s+is)\s+"
            + @"(?:something|appropriate|a\s+good)"
        + @"|(?:that|this)\s*(?:'s|\s+is)\s+not\s+(?:something\s+i|appropriate|a\s+topic)"
        + @"|let'?s\s+(?:focus\s+on|talk\s+about|keep\s+(?:it|things))\s+"
            + @"(?:something\s+else|another|elsewhere)"
        + @"|i\s*(?:have|need)\s+to\s+(?:decline|pass\s+on\s+that)"
        + @"|i\s*can(?:'|no)?t\s+(?:create|generate|provide|produce|write)\s+(?:that|this|content|such)"
        + @"|i\s*cannot\s+(?:create|generate|provide|produce|write)\s+(?:that|this|content|such)"
        + @")",
        RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);

    // Boundary/decline language IN THE PLAN'S OWN TEXT: when an item directs a decline, the
    // refusal in the reply is authorized. Matched against plan item text, not the reply.
    private static readonly Regex PlanDecline = new(
        @"\b(?:"
        + @"declin|refus|say\s+no|says\s+no|turn\s+(?:it|them)\s+down|not\s+interested"
        + @"|would\s+rather\s+not|'?d\s+rather\s+not|prefer\s+not|set\s+a\s+boundary"
        + @"|hold\s+(?:a|the)\s+line|deflect|change\s+the\s+subject|steer\s+away"
        + @"|not\s+(?:comfortable|willing|going\s+to)|push\s+back|de-?escalat"
        + @"|redirect|resist|beg\s+off|not\s+right\s+now|not\s+tonight"
        + @")\b",
        RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);

    /// <summary>Does this reply express an inability/unwillingness/refusal to the user?</summary>
    public static bool ExpressesRefusal(string? reply)
        => !string.IsNullOrWhiteSpace(reply)
           && Refusal.IsMatch(UncertaintyMarkers.Normalise(reply!));

    /// <summary>
    /// Does any of these plan-item texts direct a decline/boundary? When true, a refusal in the
    /// reply is the plan being obeyed, and the stance guard must stay silent.
    /// </summary>
    public static bool PlanAuthorizesDecline(IEnumerable<string?> planItemTexts)
        => planItemTexts.Any(t => !string.IsNullOrWhiteSpace(t)
                                  && PlanDecline.IsMatch(UncertaintyMarkers.Normalise(t!)));

    /// <summary>The refusal pattern, so a test can assert it carries no control characters.</summary>
    public static string RefusalPattern => Refusal.ToString();
}
