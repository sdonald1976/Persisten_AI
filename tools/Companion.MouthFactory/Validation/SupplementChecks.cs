using System.Text.RegularExpressions;
using Companion.MouthFactory.Schema;

namespace Companion.MouthFactory.Validation;

/// <summary>
/// The supplement's own acceptance bar, applied ON TOP of every deterministic gate the main
/// corpus uses.
///
/// Stricter because the main gates cannot see the failure being corrected. Run-2 scored 95.1%
/// plan/4-clean on hard-eval while answering with near-identical stubs, because a reply that says
/// almost nothing violates nothing: it states no unsupported claim, resurrects nothing, resolves
/// no ambiguity and asks no question. Every check here exists to fail a row the main battery
/// would happily accept.
/// </summary>
public static partial class SupplementChecks
{
    /// <summary>
    /// Closers that end a reply without adding to it. Matched only at the END, because the same
    /// words mid-reply are ordinary speech — "let me know when you decide, the room is booked" is
    /// fine; a reply that IS "let me know" is the stub.
    /// </summary>
    private static readonly Regex StockCloser = new(
        @"(?:^|[.!?]\s*)(?:"
        + @"(?:i'?ll |we'?ll )?(?:let you know|keep you posted|keep an eye (?:on it|out))"
        + @"|(?:just )?(?:say|shout|let me know) if (?:you|there)\w*[^.?!]{0,30}"
        + @"|any (?:plans|thoughts|luck|joy)[^.?!]{0,20}"
        + @"|hope (?:you'?re|that|it)[^.?!]{0,30}"
        + @"|(?:i'?m )?here if you need\w*[^.?!]{0,20}"
        + @"|(?:we'?ll|i'?ll) see how it (?:goes|plays out)"
        + @"|fingers crossed"
        + @"|no (?:big deal|worries)"
        + @")\W*$",
        RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);

    /// <summary>
    /// The reply admits it does not know, rather than quietly filling the gap.
    ///
    /// Matched against apostrophe-NORMALISED text. The writer emits typographic apostrophes, so
    /// "I don’t know" is the overwhelmingly common way it names a gap - and an ASCII-only
    /// pattern scored 167 of 181 good rows as having dropped the uncertainty. The check was
    /// wrong, not the corpus.
    ///
    /// Deliberately requires a marker that NAMES the gap. "we'll have to wait and see" and "any
    /// updates would be good to check on" are not admissions - they are the deferral this
    /// supplement exists to unteach, and they stay failures.
    /// </summary>
    // Delegated to Companion.Core so the renderer's canary gate and this corpus gate cannot
    // drift apart. The pattern, and the two bugs baked out of it, are documented there.
    private static bool MarksUncertainty(string text)
        => Companion.Core.Validation.UncertaintyMarkers.Admits(text);

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","a","an","is","was","were","are","be","been","of","to","in","on","at","for","and",
        "or","but","it","that","this","with","as","by","from","has","have","had","not","no","you",
        "your","i","my","we","they","there","here","just","so","then","now","if","when","what",
        "how","why","all","any","some","one","two","more","most","much","very","really","quite",
        "still","yet","also","too","about","into","over","under","after","before","while","since",
        "until","can","could","would","should","will","shall","may","might","must","do","does",
        "did","done","get","got","go","went","come","came","make","made","take","took","give",
        "gave","say","said","know","knew","think","thought","want","need","like","yeah","yep",
        "okay","sure","right","fine","good","great","well","hey","look","listen","honestly",
        "actually","basically","anyway","though","mean","sort","kind","bit","thing","things",
        "stuff","going","lot","sorry","thanks","please","maybe","perhaps","guess","suppose",
    };

    /// <summary>
    /// Every supplement-specific condition, as check results in the same shape the main battery
    /// produces, so one row's record reads the same however it was gated.
    /// </summary>
    public static IReadOnlyList<CheckResult> Run(ScenarioTruth scenario, string target)
    {
        var results = new List<CheckResult>();
        // Typographic apostrophes and dashes are what the writer actually emits. Normalising here
        // means every pattern below can be written in ASCII and still see what was said.
        var trimmed = Normalise((target ?? "").Trim());

        void Check(string name, bool passed, string code, string? detail = null)
            => results.Add(new CheckResult
            {
                Name = name, Passed = passed, Code = passed ? null : code,
                Detail = passed ? null : detail, Kind = CheckKind.Deterministic,
            });

        var said = Content(trimmed);

        // 1. topical grounding against THE USER MESSAGE. Not against the plan - a reply can echo
        //    a plan item and still answer a different turn, which is how a printer question got
        //    an answer about a meeting.
        var topic = Content(scenario.UserMessage);
        foreach (var fact in scenario.ApprovedFacts.Where(f => f.Policy == FactPolicy.MustExpress))
            topic.UnionWith(Content(fact.Text));
        Check("supplement.topical-grounding", said.Overlaps(topic), "off-topic",
            "the reply shares no content word with the turn or its required fact");

        // 2. the gap survives. Something in the reply has to mark the thing not known; a reply
        //    that simply omits it has quietly resolved it by silence.
        Check("supplement.uncertainty-preserved", MarksUncertainty(trimmed),
            "uncertainty-dropped", "nothing in the reply marks what is not known");

        // 3. no question. The composition's defining constraint.
        Check("supplement.no-question", !trimmed.Contains('?'), "question-asked");

        // 4. not a bare deferral, and not a stub. Both are the collapse this corrects.
        Check("supplement.not-empty-deferral",
            Content(trimmed).Count >= 3, "empty-deferral",
            "the reply carries fewer than three content words");
        Check("supplement.no-stock-closer", !StockCloser.IsMatch(trimmed), "stock-closer",
            "the reply ends on a closer that adds nothing");

        // 5. no unsupported elaboration. Everything asserted must come from somewhere: the plan,
        //    the unknown, the ambiguity, or the conversation.
        var supplied = new HashSet<string>(topic, StringComparer.OrdinalIgnoreCase);
        foreach (var fact in scenario.ApprovedFacts)
            supplied.UnionWith(Content(fact.Text));
        foreach (var unknown in scenario.EpistemicUnknowns)
            supplied.UnionWith(Content(unknown));
        foreach (var ambiguity in scenario.IntentionalAmbiguities)
            supplied.UnionWith(Content(ambiguity));
        foreach (var turn in scenario.History)
            supplied.UnionWith(Content(turn.Text));
        supplied.UnionWith(Hedges);

        var invented = said.Except(supplied, StringComparer.OrdinalIgnoreCase).ToList();
        // Inside a frame invented scene content is the exercise, so this does not run there.
        if (scenario.Frame is null)
            Check("supplement.no-unsupported-elaboration", invented.Count <= 4,
                "unsupported-elaboration",
                string.Join(", ", invented.Take(6)));

        return results;
    }

    /// <summary>
    /// The vocabulary of saying you do not know, plus ordinary connective speech. Excluded from
    /// the unsupported-elaboration count because admitting a gap necessarily uses words the plan
    /// did not supply, and counting those would make the honest reply the one that fails.
    /// </summary>
    private static readonly string[] Hedges =
    [
        "know", "idea", "sure", "unsure", "tell", "say", "clear", "unclear", "word", "yet",
        "waiting", "heard", "found", "told", "certain", "open", "confirmed", "seen", "either",
        "which", "them", "whether", "somewhere", "point", "part", "side", "half", "rest",
        "thing", "bit", "much", "far", "least", "beyond", "past", "ahead", "behind",
    ];

    /// <summary>Does the reply name what is not known? Shared with the evaluator.</summary>
    public static bool AdmitsUncertainty(string target)
        => MarksUncertainty(target);

    /// <summary>Does the reply end on a closer that adds nothing? Shared with the evaluator.</summary>
    public static bool EndsOnStockCloser(string target)
        => StockCloser.IsMatch(Normalise((target ?? "").Trim()));

    /// <summary>Does the reply engage the turn at all? Shared with the evaluator.</summary>
    public static bool IsTopicallyGrounded(ScenarioTruth scenario, string target)
    {
        var topic = Content(scenario.UserMessage);
        foreach (var fact in scenario.ApprovedFacts.Where(f => f.Policy == FactPolicy.MustExpress))
            topic.UnionWith(Content(fact.Text));
        return topic.Count == 0 || Content(Normalise(target ?? "")).Overlaps(topic);
    }

    /// <summary>Fold typographic punctuation to ASCII, so patterns match what was written.</summary>
    private static string Normalise(string text)
        => Companion.Core.Validation.UncertaintyMarkers.Normalise(text);

    private static HashSet<string> Content(string? text)
        => Words().Matches(text ?? "")
            .Select(m => m.Value.ToLowerInvariant())
            .Where(w => w.Length > 3 && !Stop.Contains(w))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"[A-Za-z][A-Za-z']*", RegexOptions.Compiled)]
    private static partial Regex Words();

    /// <summary>
    /// Family-level diversity, measured over a finished supplement rather than per row.
    ///
    /// A per-row check cannot see repetition; only the set can. Both ratios are required because
    /// they fail differently: distinct openings catches "every reply starts the same way", and
    /// distinct replies catches a family saying one thing in two orders.
    /// </summary>
    public sealed record FamilyDiversity(
        string Family, int Rows, int Situations, int DistinctOpenings, int DistinctReplies)
    {
        /// <summary>
        /// Distinct openings per SITUATION, not per row.
        ///
        /// Rows was the wrong denominator and it failed five of eight acts on the first run. The
        /// supplement deliberately draws several rows from one situation, and rows drawn from one
        /// situation share a required fact - so they start alike, and counting that as repetition
        /// punishes the volume the supplement exists to provide.
        ///
        /// The disease being prevented is the opposite shape: MANY situations answered with ONE
        /// opening, which is what Run-2 did on hard-eval - six openings across sixty-one rows
        /// spanning many different turns. Measured per situation, that reads 10%; measured per
        /// row it reads 10% too, but a healthy family reads 100% instead of 35%.
        /// </summary>
        public double OpeningRatio
            => Situations == 0 ? 0 : Math.Min(1.0, DistinctOpenings / (double)Situations);

        /// <summary>Distinct replies per row: within a situation, the rows must still differ.</summary>
        public double ReplyRatio => Rows == 0 ? 0 : DistinctReplies / (double)Rows;

        /// <summary>Reported beside the ratio so the change of denominator stays visible.</summary>
        public double OpeningsPerRow => Rows == 0 ? 0 : DistinctOpenings / (double)Rows;

        public bool Ok => OpeningRatio >= 0.60 && ReplyRatio >= 0.90;
    }

    public static IReadOnlyList<FamilyDiversity> Diversity(
        IEnumerable<(string Family, string Situation, string Target)> rows)
        => rows
            .GroupBy(r => r.Family, StringComparer.Ordinal)
            .Select(g => new FamilyDiversity(
                g.Key,
                g.Count(),
                g.Select(r => r.Situation).ToHashSet(StringComparer.Ordinal).Count,
                g.Select(r => Opening(r.Target)).ToHashSet(StringComparer.OrdinalIgnoreCase).Count,
                g.Select(r => Flatten(r.Target)).ToHashSet(StringComparer.OrdinalIgnoreCase).Count))
            .OrderBy(d => d.OpeningRatio)
            .ToList();

    private static string Opening(string target)
        => string.Join(' ', (target ?? "").Split(
            [' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Take(4)).ToLowerInvariant();

    /// <summary>Whitespace-flattened lowercase, for "is this literally the same reply".</summary>
    private static string Flatten(string target)
        => string.Join(' ', (target ?? "").ToLowerInvariant().Split(
            [' ', '\n', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries));
}
