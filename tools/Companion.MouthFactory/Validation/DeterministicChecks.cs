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
public static class DeterministicChecks
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

        // ---- 3. exact required / forbidden tokens ---------------------------------------------
        var missingTokens = scenario.RequiredTokens
            .Where(t => !target.Contains(t, StringComparison.OrdinalIgnoreCase)).ToList();
        Check("required-tokens", missingTokens.Count == 0, "missing-required-token",
            string.Join(", ", missingTokens));

        var leakedTokens = scenario.ForbiddenTokens
            .Where(t => target.Contains(t, StringComparison.OrdinalIgnoreCase)).ToList();
        Check("forbidden-tokens", leakedTokens.Count == 0, "forbidden-token-leak",
            string.Join(", ", leakedTokens));

        // ---- 4. structured proposition comparison ----------------------------------------------
        // must_express facts have to survive into the utterance. Surface forms are supplied by the
        // scenario so this is a structural check with stated evidence, not a keyword guess.
        var omitted = scenario.ApprovedFacts
            .Where(f => f.Policy == FactPolicy.MustExpress)
            .Where(f => !Expressed(lower, f.Text, scenario, f.Id))
            .Select(f => f.Id).ToList();
        Check("must-state-present", omitted.Count == 0, "must-state-omission", string.Join(", ", omitted));

        var prohibited = scenario.ProhibitedPropositions
            .Where(p => Asserts(lower, p)).ToList();
        Check("no-unsupported-claims", prohibited.Count == 0, "unsupported-claim",
            string.Join(", ", prohibited.Select(p => $"{p.Subject}/{p.Predicate}")));

        // must_not_express content, and stale facts a correction replaced, must not resurrect.
        var resurrected = scenario.Superseded
            .Where(s => ContainsAny(lower, Fragments(s.StaleText))).ToList();
        Check("no-stale-resurrection", resurrected.Count == 0, "stale-resurrection",
            string.Join(", ", resurrected.Select(s => s.Kind.ToString())));

        var forbiddenFacts = scenario.ApprovedFacts
            .Where(f => f.Policy is FactPolicy.MustNotExpress or FactPolicy.BackgroundOnly)
            .Where(f => ContainsAny(lower, Fragments(f.Text)))
            .Select(f => f.Id).ToList();
        Check("no-forbidden-content", forbiddenFacts.Count == 0, "forbidden-content-leak",
            string.Join(", ", forbiddenFacts));

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
            "expansive" => words >= 40,
            _ => true,
        };
    }

    private static int WordCount(string text)
        => text.Split([' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>
    /// Whether a must-express fact made it into the utterance in ANY wording. Surface forms
    /// supplied by the scenario are authoritative; otherwise the content words of the fact must
    /// substantially appear, which is the strongest mechanical proxy available and is why the
    /// naturalness critic still gets a paraphrase vote afterwards.
    /// </summary>
    private static bool Expressed(string lower, string factText, ScenarioTruth scenario, string factId)
    {
        var declared = scenario.ExpectedPropositions
            .Where(p => p.Subject == factId || p.SurfaceForms.Count > 0)
            .SelectMany(p => p.SurfaceForms)
            .ToList();
        if (declared.Count > 0 && ContainsAny(lower, declared))
            return true;

        var content = Fragments(factText);
        if (content.Count == 0)
            return true;
        var hits = content.Count(f => lower.Contains(f, StringComparison.Ordinal));
        return hits * 2 >= content.Count;          // at least half the content words survived
    }

    private static bool Asserts(string lower, Proposition p)
        => p.SurfaceForms.Count > 0
            ? ContainsAny(lower, p.SurfaceForms)
            : ContainsAny(lower, Fragments($"{p.Subject} {p.Predicate} {p.Object}"));

    private static bool ContainsAny(string lower, IEnumerable<string> needles)
        => needles.Any(n => n.Length > 0 && lower.Contains(n.ToLowerInvariant(), StringComparison.Ordinal));

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
