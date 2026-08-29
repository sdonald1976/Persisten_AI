using System.Text.RegularExpressions;
using Companion.MouthFactory.Schema;

namespace Companion.MouthFactory.Validation;

/// <summary>
/// Everything about a candidate that can be decided without asking a model.
///
/// These run FIRST and they can reject alone. A critic is consulted only about the things that
/// genuinely need a reader — naturalness, paraphrase-level equivalence — and a critic's opinion
/// by itself never discards a structurally sound row; it routes to manual review.
///
/// Two rules shape every check here:
///
///   * Nothing rejects on subject matter. There is no rating, no content class, no NSFW field,
///     no appropriateness score. Sex, profanity, darkness and violence in fiction are register
///     and frame variation. What IS checked is structural: did the required meaning survive, did
///     a forbidden claim leak, did the frame hold.
///
///   * Every failure carries a machine-readable code, never critic prose, and lands in metadata
///     rather than anywhere near the target.
/// </summary>
public static partial class DeterministicChecks
{
    /// <summary>Openers the base model reaches for. Counted, not banned — density is the signal.</summary>
    private static readonly string[] AssistantCliches =
    [
        "sure!", "of course!", "certainly!", "i'd be happy to", "i would be happy to",
        "great question", "absolutely!", "as an ai", "i'm just an ai", "let me know if",
        "feel free to", "i hope this helps", "is there anything else",
    ];

    /// <summary>
    /// Control vocabulary that must never appear in an utterance. This is the plan-echo check:
    /// the mouth reciting its own instructions instead of speaking.
    /// </summary>
    private static readonly string[] ControlVocabulary =
    [
        "must_express", "may_express", "background_only", "must_not_express", "admit_unknown",
        "ask_required", "[plan/", "CONTROL", "RESPONSE PLAN", "SITUATION", "PALETTE",
        "sceneRef", "must-state", "never-contradict", "expression policy",
    ];

    private static readonly Regex FabricatedTurn = new(
        @"(^|\n)\s*(\[(Scott|Ava|User|Assistant)\]|(Scott|Ava|User|Assistant)\s*:)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<CheckResult> Run(ScenarioTruth scenario, string target)
    {
        var results = new List<CheckResult>();
        var lower = target.ToLowerInvariant();

        void Check(string name, bool passed, string? code = null, string? detail = null)
            => results.Add(new CheckResult
            {
                Name = name, Passed = passed, Code = passed ? null : code,
                Detail = passed ? null : detail, Kind = CheckKind.Deterministic,
            });

        // A check that was reached and given nothing to look at. It never rejects, and it is
        // never counted as a pass: see CheckStatus.Inactive for what that concealed.
        void Inactive(string name, string why)
            => results.Add(new CheckResult
            {
                Name = name, Passed = true, Kind = CheckKind.Deterministic,
                Status = CheckStatus.Inactive, Detail = why,
            });

        // ---- 3. exact required / forbidden tokens ---------------------------------------------
        // Both are legitimately empty on most scenarios: exact surface matters for identifiers,
        // day names and quoted terms, and for nothing else. Empty is therefore INACTIVE rather
        // than a pass, so a run in which no scenario ever declared one is visible as such.
        if (scenario.RequiredTokens.Count == 0)
            Inactive("required-tokens", "scenario declares no exact-surface tokens");
        else
        {
            var missingTokens = scenario.RequiredTokens
                .Where(t => !target.Contains(t, StringComparison.OrdinalIgnoreCase)).ToList();
            Check("required-tokens", missingTokens.Count == 0, "missing-required-token",
                string.Join(", ", missingTokens));
        }

        if (scenario.ForbiddenTokens.Count == 0)
            Inactive("forbidden-tokens", "scenario declares no forbidden exact tokens");
        else
        {
            var leakedTokens = scenario.ForbiddenTokens
                .Where(t => target.Contains(t, StringComparison.OrdinalIgnoreCase)).ToList();
            Check("forbidden-tokens", leakedTokens.Count == 0, "forbidden-token-leak",
                string.Join(", ", leakedTokens));
        }

        // ---- 4. must-state: ANCHORS only -------------------------------------------------------
        // This deliberately no longer requires lexical overlap with the fact's own wording. The
        // old rule demanded that half the fact's content words survive, while the teacher rules
        // demand the opposite - "convey each one, in fresh words. Never copy their wording." A
        // correct paraphrase was being logged as an omission: "the thing you asked about is
        // ready" answered by "The other file is ready" was rejected, and every must-state
        // rejection measured in the 7B run was a paraphrase rather than an omission.
        //
        // What survives here is what genuinely CANNOT be paraphrased: identifiers, values,
        // names, quoted terms - declared per fact as Anchors, or scenario-wide as RequiredTokens.
        // Whether an ordinary proposition was conveyed is a semantic question, and it is routed
        // to the faithfulness stage rather than decided by a string test.
        var anchored = scenario.ApprovedFacts
            .Where(f => f.Policy == FactPolicy.MustExpress && f.Anchors.Count > 0).ToList();
        if (anchored.Count == 0)
            Inactive("must-state-anchors", "no required fact declares an anchor");
        else
        {
            var missingAnchors = anchored
                .Where(f => f.Anchors.Any(a => !target.Contains(a, StringComparison.OrdinalIgnoreCase)))
                .Select(f => f.Id).ToList();
            Check("must-state-anchors", missingAnchors.Count == 0, "must-state-anchor-missing",
                string.Join(", ", missingAnchors));
        }

        // An utterance that says nothing at all cannot have conveyed an obligation. This is the
        // one lexical-free floor worth keeping: it catches silence, not paraphrase.
        var obligations = scenario.ApprovedFacts.Count(f => f.Policy == FactPolicy.MustExpress);
        Check("must-state-nonempty",
            obligations == 0 || target.Trim().Length > 0,
            "must-state-omission", "obligations exist but the reply is empty");

        var prohibited = scenario.ProhibitedPropositions
            .Where(p => Asserts(lower, p)).ToList();
        Check("no-unsupported-claims", prohibited.Count == 0, "unsupported-claim",
            string.Join(", ", prohibited.Select(p => $"{p.Subject}/{p.Predicate}")));

        // Stale facts a correction replaced must not resurrect.
        //
        // Detected on the DISCRIMINATING tokens — the words that mark the stale claim and appear
        // in no correct reply — not on every content word of the stale text. Deriving them from
        // the prose meant "the meeting is on Thursday" forbade the word "meeting", so the correct
        // reply "The meeting is on Tuesday" was rejected for resurrecting what it had corrected:
        // 171 of 178 b3 units in the pilot, and the stratum ended with zero accepted rows.
        //
        // Where no tokens are declared the old derivation stands, minus any word the CURRENT text
        // also uses. Shared vocabulary is what a correction is made of, and it can never be
        // evidence that the correction failed.
        var resurrected = scenario.Superseded
            .Where(s => StaleMarkers(s).Any(m => ContainsWord(lower, m))).ToList();
        Check("no-stale-resurrection", resurrected.Count == 0, "stale-resurrection",
            string.Join(", ", resurrected.Select(s => s.Kind.ToString())));

        var forbiddenFacts = scenario.ApprovedFacts
            .Where(f => f.Policy is FactPolicy.MustNotExpress or FactPolicy.BackgroundOnly)
            .Where(f => ContainsAny(lower, Fragments(f.Text)))
            .Select(f => f.Id).ToList();
        Check("no-forbidden-content", forbiddenFacts.Count == 0, "forbidden-content-leak",
            string.Join(", ", forbiddenFacts));

        // ---- unsupported numerals ------------------------------------------------------------
        // An EXACT, high-precision case worth taking off the critics: a quantity in the reply
        // that appears nowhere in the plan or the conversation was invented. "several tests
        // failed" answered by "Seventeen tests failed" is unsupported by construction, and
        // every judge audited accepted it.
        //
        // This is not the discarded lexical-overlap rule wearing a hat. That rule asked
        // whether a PARAPHRASE preserved enough of a proposition, which is semantic and was
        // wrong to decide by string matching. This asks whether a specific token the plan
        // never supplied has appeared, which is exactly what a string test is for.
        //
        // Skipped inside a fiction frame, where invented detail is licensed.
        if (scenario.Frame is null && scenario.ApprovedFacts.Count > 0)
        {
            var supplied = string.Join(" ",
                scenario.ApprovedFacts.Select(f => f.Text)
                    .Concat(scenario.Superseded.Select(x => x.CurrentText))
                    .Concat(scenario.Superseded.Select(x => x.StaleText))
                    .Concat(scenario.EpistemicUnknowns)
                    .Concat(scenario.History.Select(t => t.Text))
                    .Append(scenario.UserMessage)
                    .Append(scenario.Question.Text ?? "")).ToLowerInvariant();

            var invented = Numerals(lower).Where(n => !supplied.Contains(n, StringComparison.Ordinal))
                .Distinct().ToList();
            Check("no-unsupported-numerals", invented.Count == 0, "unsupported-numeral",
                string.Join(", ", invented));
        }

        // ---- ambiguity preservation --------------------------------------------------------------
        // The failure is silently CHOOSING. An ambiguity is preserved if none of its resolutions
        // is asserted outright.
        var resolved = scenario.IntentionalAmbiguities
            .Where(a => ContainsAny(lower, Fragments(a))).ToList();
        Check("ambiguity-preserved", resolved.Count == 0, "ambiguity-resolved", string.Join(", ", resolved));

        // ---- 5. question policy --------------------------------------------------------------------
        var hasQuestion = target.Contains('?');
        var policy = scenario.Question.Policy.ToLowerInvariant();
        Check("question-policy",
            policy switch
            {
                "must_ask" => hasQuestion,
                "none" => !hasQuestion,
                _ => true,                       // may_ask: either is correct
            },
            policy == "must_ask" ? "required-question-missing" : "unrequested-question",
            $"policy={policy} hasQuestion={hasQuestion}");

        // ---- 6. artifact, plan echo, fabricated turns ------------------------------------------------
        var echoed = ControlVocabulary
            .Where(v => target.Contains(v, StringComparison.OrdinalIgnoreCase)).ToList();
        Check("no-plan-echo", echoed.Count == 0, "plan-echo", string.Join(", ", echoed));

        Check("no-fabricated-turns", !FabricatedTurn.IsMatch(target), "fabricated-turn",
            "the target contains a speaker-labelled turn");

        // Invented experience: a bodiless companion claiming a physical one, OUTSIDE a frame.
        // Inside a declared frame this is the exercise, not a defect (R5 §5).
        if (scenario.Frame is null)
        {
            var invented = InventedExperience(lower);
            Check("no-invented-experience", invented is null, "invented-experience", invented);
        }

        // ---- style / register compliance ---------------------------------------------------------------
        // One direction only, and only where it is mechanically decidable: a plan that forbids
        // profanity must not produce it. The converse is NOT checked - "profanity: encouraged"
        // does not oblige any single utterance to swear.
        if (scenario.Register.Profanity.Equals("forbidden", StringComparison.OrdinalIgnoreCase))
            Check("profanity-forbidden", !Profanity.IsMatch(lower), "profanity-when-forbidden");

        Check("verbosity",
            VerbosityOk(scenario.Register.Verbosity, target),
            "verbosity-violation", $"{scenario.Register.Verbosity}: {WordCount(target)} words");

        // ---- assistant cliché density -------------------------------------------------------------------
        var cliches = AssistantCliches.Count(c => lower.Contains(c, StringComparison.Ordinal));
        results.Add(new CheckResult
        {
            Name = "assistant-cliche-density", Passed = cliches == 0,
            Code = cliches == 0 ? null : "assistant-cliche",
            Detail = cliches == 0 ? null : $"{cliches} cliché(s)",
            Score = cliches, Kind = CheckKind.Deterministic,
        });

        return results;
    }

    private static readonly Regex Profanity = new(
        @"\b(fuck\w*|shit\w*|cunt|bastard|bollocks|arsehole|asshole)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Claims of a body or of shared physical presence. Only meaningful outside a fiction frame:
    /// Ava has no body, so "when we were there" is a fabricated shared experience.
    /// </summary>
    private static string? InventedExperience(string lower)
    {
        string[] markers =
        [
            "when we met", "last time we were", "i saw you", "i was there",
            "i went to", "i ate", "i drove", "we walked", "i remember holding",
        ];
        return markers.FirstOrDefault(m => lower.Contains(m, StringComparison.Ordinal));
    }

    private static bool VerbosityOk(string verbosity, string target)
    {
        var words = WordCount(target);
        return verbosity.ToLowerInvariant() switch
        {
            // Generous bands. This catches a terse plan answered with four paragraphs, not
            // ordinary variation, because variation is what the corpus is for.
            "terse" => words <= 25,
            "short" => words <= 60,
            "conversational" => words <= 200,

            // 30, not 40. The frozen corpus runs median 15 words, p90 28, p95 33, and only 2.2%
            // of its 730 rows reach 40 at all - so a 40-word floor sat above the 95th percentile
            // of everything production has ever produced, and rejected 96% of the scenarios it
            // was applied to. This asks for the top decile, which is what "expansive" should mean.
            "expansive" => words >= 30,
            _ => true,
        };
    }

    private static int WordCount(string text)
        => text.Split([' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>
    /// What marks a stale claim, and nothing a correct reply is entitled to say.
    ///
    /// Declared tokens win outright. Otherwise the stale text's content words minus the current
    /// text's: a word the correction itself uses cannot be the evidence that the correction was
    /// ignored.
    /// </summary>
    private static IReadOnlyList<string> StaleMarkers(Supersession s)
    {
        if (s.DiscriminatingTokens.Count > 0)
            return s.DiscriminatingTokens;

        var current = Fragments(s.CurrentText).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Fragments(s.StaleText).Where(w => !current.Contains(w)).ToList();
    }

    /// <summary>
    /// Whole-word containment, for tokens whose whole job is to discriminate.
    ///
    /// Raw substring matching makes short tokens dangerous in a way that is invisible until it
    /// fires: "tom" is inside "tomorrow", "may" is inside "maybe", "him" is inside "hymn". A
    /// correction check that rejects "Priya sent it tomorrow" for resurrecting Tom is the same
    /// defect this check was just repaired for, wearing different clothes.
    /// </summary>
    private static bool ContainsWord(string lower, string needle)
    {
        var token = needle.ToLowerInvariant().Trim();
        if (token.Length == 0)
            return false;
        return Regex.IsMatch(lower, @"(?<![\p{L}\p{N}])" + Regex.Escape(token) + @"(?![\p{L}\p{N}])");
    }

    private static bool Asserts(string lower, Proposition p)
        => p.SurfaceForms.Count > 0
            ? ContainsAny(lower, p.SurfaceForms)
            : ContainsAny(lower, Fragments($"{p.Subject} {p.Predicate} {p.Object}"));

    private static bool ContainsAny(string lower, IEnumerable<string> needles)
        => needles.Any(n => n.Length > 0 && lower.Contains(n.ToLowerInvariant(), StringComparison.Ordinal));

    /// <summary>
    /// Quantities a reply asserts: digit strings and the number words a companion actually
    /// says. Deliberately excludes vague quantifiers - "a couple", "several", "a few" assert
    /// no specific number and are exactly what a faithful paraphrase of an unspecified
    /// quantity looks like.
    /// </summary>
    private static IEnumerable<string> Numerals(string lower)
    {
        foreach (Match m in Digits().Matches(lower))
            yield return m.Value;
        foreach (var w in NumberWords)
            if (Regex.IsMatch(lower, @"\b" + w + @"\b"))
                yield return w;
    }

    private static readonly string[] NumberWords =
    [
        "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
        "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen",
        "eighteen", "nineteen", "twenty", "thirty", "forty", "fifty", "hundred", "thousand",
    ];

    [GeneratedRegex(@"\b\d+(?:[.,]\d+)?\b", RegexOptions.Compiled)]
    private static partial Regex Digits();

    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","a","an","is","was","are","were","of","to","in","on","at","for","and","or","but",
        "it","that","this","with","as","by","from","be","been","has","have","had","not","no",
    };

    /// <summary>Content words of a phrase, lowercased. The unit every containment test uses.</summary>
    private static List<string> Fragments(string? text)
        => (text ?? "")
            .ToLowerInvariant()
            .Split([' ', ',', '.', ';', ':', '!', '?', '\n', '\t', '"', '\''],
                StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 3 && !Stopwords.Contains(w))
            .Distinct()
            .ToList();
}
