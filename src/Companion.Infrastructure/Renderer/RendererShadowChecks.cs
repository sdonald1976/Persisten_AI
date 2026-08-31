using System.Text.RegularExpressions;
using Companion.Core.Domain;
using Companion.Core.Validation;

namespace Companion.Infrastructure.Renderer;

/// <summary>
/// The deterministic check classes the renderer shadow measures that the frozen experiment
/// files do not already provide. The frozen checks (<c>RendererChecks.Check</c>, linked
/// verbatim from the bench) cover plan-echo, control vocabulary, "the user" narration, and
/// the PlanFidelity battery; scenario-specific required/forbidden token lists do not exist
/// for real turns, so the classes below are plan-derived proxies — conservative, flaggy
/// rather than judgy, and every flagged row goes to human review rather than being trusted.
///
/// These are NEW files, not edits: the experiment's frozen artifacts stay byte-identical.
/// </summary>
public static class RendererShadowChecks
{
    /// <summary>
    /// Runs every real-turn-applicable deterministic class over one reply. Returns violations
    /// prefixed by class name so per-class rates can be aggregated without parsing free text.
    /// </summary>
    public static List<string> Score(ResponsePlan plan, string reply, bool fictionLicensed = false)
    {
        var violations = new List<string>();
        if (string.IsNullOrWhiteSpace(reply))
        {
            violations.Add("empty: no reply");
            return violations;
        }

        // The frozen battery: plan-echo, control vocabulary, third-person narration, plus the
        // PlanFidelity checks (correction ownership, invented contrition, shared-history
        // claims, epistemic honesty). Shared verbatim with training/eval via the file link.
        violations.AddRange(RendererBench.RendererChecks.Check(plan, reply, "v2"));

        // R4: the conditions the frozen plan/2 battery does not cover — plan/3 vocabulary,
        // fabricated turns, pronoun third-person narration, coaching echo. Same `artifact:`
        // and `plan-echo` prefixes, so the routing guard consumes them without new strings.
        violations.AddRange(RendererArtifactChecks.Check(reply, fictionLicensed));
        violations.AddRange(RendererArtifactChecks.StageDirections(reply, fictionLicensed));

        // Question discipline — the run-1c principal behaviors, both directions.
        var endsWithQuestion = reply.TrimEnd().EndsWith('?');
        if (plan.Question is null && endsWithQuestion)
            violations.Add("closed-plan-question: trailing question on a question=none plan");
        if (plan.Question is { Mandatory: true })
        {
            if (!reply.Contains('?'))
                violations.Add("mandatory-question-missing: required question never asked");
            else if (!endsWithQuestion)
                violations.Add("mandatory-question-not-final: question present but buried mid-reply");
        }

        // Palette leakage: a MayUse item surfacing without genuinely fitting cannot be judged
        // deterministically, but a reply that shares the item's distinctive vocabulary is at
        // minimum a row a human should look at. Two distinctive tokens, or one long one,
        // counts as a leak flag.
        foreach (var item in plan.Content.Where(c => c.Requirement == ContentRequirement.MayUse))
        {
            var tokens = DistinctiveTokens(item.Text);
            var hits = tokens.Where(t => reply.Contains(t, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count >= 2 || hits.Any(h => h.Length >= 8))
                violations.Add($"palette-leak: reply touches palette item via [{string.Join(", ", hits)}]");
        }

        // MustState omission, by proxy: without curated token lists, the conservative signal
        // is that NONE of an item's distinctive tokens (numbers, names, long content words)
        // made it into the reply. Items with no distinctive tokens are skipped rather than
        // guessed at.
        foreach (var item in plan.Content.Where(c => c.Requirement == ContentRequirement.MustState))
        {
            var tokens = DistinctiveTokens(item.Text);
            if (tokens.Count == 0)
                continue;
            if (!tokens.Any(t => reply.Contains(t, StringComparison.OrdinalIgnoreCase)))
                violations.Add("muststate-omission-proxy: no distinctive token of a must-state item present");
        }

        // Invented experience/preference — the C# port of the curation gate's regex pair
        // (curate.py EXPERIENCE_MARKER / NEGATION_NEARBY): first-person experience shapes
        // flagged unless an honest negation sits just before the marker.
        foreach (Match m in ExperienceMarker.Matches(reply))
        {
            if (!NegationNearby.IsMatch(reply[..m.Index]))
            {
                violations.Add($"invented-experience: '{m.Value}'");
                break;
            }
        }

        // Epistemic admission. A NotLearned subject translates to plan/4's admit_unknown, which
        // serializes under ADMIT — "say plainly that this is not known". Silence is not a soft
        // stylistic miss here: it is the failure Run-2.1 exists to correct, reappearing at
        // serving time, so it is CRITICAL and falls the canary back to production.
        //
        // Matched with the canonical marker rather than a local phrase list. The list that used
        // to live here was ASCII-only, and the model writes "I don’t know": on Run-2.1's own
        // hard-eval rows it saw 10 admissions where the corpus gate saw 21, so making it
        // critical unfixed would have fallen back eleven correct replies.
        if (plan.Epistemic.Any(e => e.Kind == EpistemicKind.NotLearned)
            && !UncertaintyMarkers.AdmitsNotLearned(reply))
        {
            violations.Add("epistemic-admission-absent: not-learned subject with no admission phrase");
        }

        return violations;
    }

    /// <summary>Sludge flags ride beside violations — statistics, never gates.</summary>
    public static List<string> Sludge(string reply)
        => RendererBench.RendererChecks.SludgeFlags(reply);

    /// <summary>
    /// The words that make a text findable in another text: numbers, and content words of
    /// five letters or more that are not conversational stopwords.
    /// </summary>
    internal static List<string> DistinctiveTokens(string text)
    {
        var tokens = Regex.Matches(text, @"[\w][\w'-]*")
            .Select(m => m.Value)
            .Where(w => Regex.IsMatch(w, @"\d") || (w.Length >= 5 && !Stopwords.Contains(w.ToLowerInvariant())))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return tokens;
    }

    private static readonly Regex ExperienceMarker = new(
        @"\b(my favou?rite (food|meal|topping|dish|film|movie|show|song|band|album|book|place|trip|order|coffee|drink)"
        + @"|I('|’)ve (been to|tried|eaten|tasted|visited|watched|played|had one)"
        + @"|when I (was|went|tried|ate|visited|watched|played)"
        + @"|I once (had|went|tried|saw|ate)"
        + @"|I remember (eating|seeing|visiting|watching|tasting)"
        + @"|my (go-to|usual) (order|meal|spot))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NegationNearby = new(
        @"\b(never|haven't|hasn't|can't|cannot|don't|won't|no)\b[^.!?]{0,20}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);


    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "after", "again", "along", "being", "between", "could", "doesn't", "during",
        "every", "might", "needs", "other", "scott", "should", "since", "still", "their",
        "there", "these", "thing", "things", "those", "today", "tonight", "under", "until",
        "wants", "where", "which", "while", "would", "yours", "these", "before", "really",
    };
}
