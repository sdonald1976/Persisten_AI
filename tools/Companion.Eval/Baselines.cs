namespace Companion.Eval;

/// <summary>
/// The score each suite currently reaches, recorded so that a change which quietly makes one worse
/// is a build failure rather than something discovered months later in a conversation.
///
/// A floor, not a target. Raise one when something genuinely improves; never lower one to make a
/// change pass — that is the move that turns an evaluation harness into a rubber stamp, and it is
/// exactly how the existing unit suite came to assert supersession worked while the live path had
/// never once performed it.
/// </summary>
public static class Baselines
{
    public static readonly IReadOnlyDictionary<string, double> Floors = new Dictionary<string, double>
    {
        // Measured, not guessed. The harness paid for itself on its first run: `decision` scored
        // 0.875 with two false positives, both of them questions ("have we decided on the database
        // yet?", "if we went with PostgreSQL, would that be simpler?"), which is the same
        // question-is-not-a-claim confusion that put fabricated facts in the memory store. Gating
        // the detector on AssertionGuard took it to 1.000, with no model involved.
        ["decision"] = 1.00,

        // The one remaining miss is the case a mood test cannot reach and NLI can: "I wouldn't say
        // I've bought the timber yet" is a statement, and it does not entail the fact. Evidence for
        // adding an entailment veto rather than replacing the mood check with one.
        ["assertion"] = 0.93,

        // Precision 1.00, recall 0.50 — it never claims a replacement wrongly, and misses half of
        // the real ones ("I'm on decaf"). Two caveats on that number: it measures the WORDING
        // signal alone, and in production predicate cardinality catches "I live in Cambridge now"
        // and "it's Scott Donald" without any wording at all. So this is the floor of the half a
        // model would replace, not the pipeline's true recall — which is the honest thing to
        // benchmark, because it is the half that would change.
        ["supersession"] = 0.66,

        // The single-message decisions, measured for the first time. Before this they had no
        // evidence at all, which is a worse position than a bad score: nothing could regress
        // because nothing was watching.
        //
        // deliverable-request rose from 0.875 to 0.981 the moment it was measured — fifteen false
        // negatives, all of them plain requests ("give me a summary", "make me a story") that the
        // verb list missed because the verb doing the work was "give". Three false positives
        // remain, all self-reports containing a deliverable noun.
        ["deliverable-request"] = 0.98,

        // Clean, and the cheapest thing in the suite to keep clean.
        ["looks-unfinished"] = 1.00,

        // Two false positives, both defensible readings of an ambiguous sentence: "what can you see
        // from your window" and "are you able to come tomorrow" both parse as questions about her
        // capabilities. Left as the baseline rather than tuned away, because the fix is a judgement
        // about intent and that is exactly the kind of thing a classifier should eventually do
        // better than a regex.
        ["capability-nudge"] = 0.83,
    };
}
