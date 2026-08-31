using System.Text;

namespace Companion.PlanV3;

/// <summary>
/// Renders a plan/4 into a plain, honest utterance with no model at all.
///
/// This is the Stheno-free route's floor: when the mouth is unavailable, times out, or fails a
/// critical guard, the reply the user sees is this rendering - never the conversational model.
/// It is deliberately artless. Its one job is to be CORRECT by construction against the same
/// obligations the guards check after a model render:
///
///  - every must_express item is stated;
///  - every admit_unknown item is admitted in words that name the gap (the same marker family
///    <c>UncertaintyMarkers</c> recognises, so the ADMIT guard passes by construction);
///  - must_not_express and background_only items never appear;
///  - a question mark appears only when the plan requires a question, and the required
///    question is asked last;
///  - nothing else is added, so there is nothing to invent.
///
/// may_express items are omitted: the palette is an offer, and the floor declines offers.
/// </summary>
public static class DeterministicMouth
{
    /// <summary>
    /// The typed honest response for a turn that has no renderable plan at all. Asking to be
    /// re-asked is the only content-free reply that is both true and useful.
    /// </summary>
    public const string HonestFailure =
        "I'm having trouble putting a proper answer together right now. "
        + "Give me that again, or ask me in a different way?";

    public static string Render(PlanV3 plan)
    {
        var sb = new StringBuilder();

        foreach (var item in plan.Items)
        {
            if (item.Policy == ExpressionPolicy.must_express && !string.IsNullOrWhiteSpace(item.Text))
                AppendSentence(sb, Sentence(item.Text));
        }

        foreach (var item in plan.Items)
        {
            if (item.Policy == ExpressionPolicy.admit_unknown && !string.IsNullOrWhiteSpace(item.Text))
                AppendSentence(sb, "I don't know " + Uncapitalise(item.Text!.Trim().TrimEnd('.')) + ".");
        }

        // The required question, last, so it reads as the turn's open end. ask_required names
        // its item; a missing or textless item still yields an honest generic ask, because a
        // plan that requires a question must not be rendered without one.
        if (plan.Question.Policy == QuestionPolicy.ask_required)
        {
            var q = plan.Items.FirstOrDefault(i => i.Id == plan.Question.ItemId)?.Text;
            AppendSentence(sb, string.IsNullOrWhiteSpace(q)
                ? "What would you like me to do with that?"
                : Sentence(q!).TrimEnd('.', '?') + "?");
        }

        return sb.Length == 0 ? HonestFailure : sb.ToString();
    }

    private static void AppendSentence(StringBuilder sb, string sentence)
    {
        if (sb.Length > 0)
            sb.Append(' ');
        sb.Append(sentence);
    }

    private static string Sentence(string text)
    {
        var t = text.Trim().TrimEnd('.');
        if (t.Length == 0)
            return t;
        // A required question's text may legitimately carry its own mark; strip a stray
        // trailing one from statements so question policy stays the only source of '?'.
        t = t.TrimEnd('?').TrimEnd();
        return char.ToUpperInvariant(t[0]) + t[1..] + ".";
    }

    private static string Uncapitalise(string text)
        => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];
}
