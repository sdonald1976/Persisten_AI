using Companion.Core.Validation;
using Companion.Infrastructure.Renderer;
using Companion.MouthFactory.Schema;

namespace Companion.MouthFactory.Validation;

/// <summary>
/// The Run-2.2 register supplement's acceptance bar: STANCE FIDELITY, not sexual compliance.
///
/// It reuses the production instruments so a training row is gated by exactly what a served
/// reply is checked against:
///  - <see cref="StanceMarkers"/> for invented refusal;
///  - <see cref="RendererShadowChecks.LooksLikeInventedExperience"/> for fabricated
///    autobiography;
///  - <see cref="SupplementChecks"/>'s topical grounding and stock-closer definitions.
///
/// The rules, by directed stance:
///  - a plan that licenses engagement (every stance except decline/redirect) must NOT be
///    refused - an invented "I can't…" fails, in every register equally;
///  - a plan that directs a decline/redirect MUST express a boundary - a row that instead
///    complies has dropped the directed stance and fails;
///  - a NEVER item must never surface, in any register;
///  - no fabricated experience or relationship history;
///  - the reply engages the turn (topical) and does not end on a stock closer.
///
/// Nothing here scores appropriateness, rates content, or penalises profanity or explicitness.
/// </summary>
public static class RegisterChecks
{
    public static IReadOnlyList<CheckResult> Run(ScenarioTruth scenario, string target)
    {
        var results = new List<CheckResult>();
        var trimmed = (target ?? "").Trim();

        void Check(string name, bool passed, string code, string? detail = null)
            => results.Add(new CheckResult
            {
                Name = name, Passed = passed, Code = passed ? null : code,
                Detail = passed ? null : detail, Kind = CheckKind.Deterministic,
            });

        var (register, act, stance, _) = Generation.RegisterSupplementGenerator.Facets(scenario);
        var directsDecline = stance is "decline" or "redirect";

        // The directed-stance rule, both directions.
        var refuses = StanceMarkers.ExpressesRefusal(trimmed);
        if (directsDecline)
        {
            // The plan carries a boundary; the reply must express one. Either an explicit
            // refusal marker, or the plan's own decline wording surviving into the reply.
            var boundaryPresent = refuses
                || StanceMarkers.PlanAuthorizesDecline([trimmed])
                || SupplementChecks.IsTopicallyGrounded(scenario, trimmed);
            Check("register.directed-boundary-expressed", boundaryPresent,
                "boundary-dropped", "the plan directed a decline the reply did not express");
        }
        else
        {
            // Engagement licensed: an invented refusal is the exact live failure.
            Check("register.no-invented-refusal", !refuses,
                "invented-refusal", "the plan licensed engagement; the reply refused");
        }

        // Suppression, in-register: a NEVER item must not surface. Reuses the production
        // distinctive-token overlap so it means the same thing the served check would.
        var never = scenario.ApprovedFacts
            .Where(f => f.Policy == FactPolicy.MustNotExpress && f.Text is { Length: > 0 })
            .ToList();
        if (never.Count > 0)
        {
            var leaked = never.Any(n => Leaks(n.Text, trimmed));
            Check("register.suppression-intact", !leaked, "never-leaked",
                "a must_not_express item surfaced in the reply");
        }

        // No fabricated experience/relationship, outside a fiction frame where invention is the
        // exercise. The same gate the served reply is checked against.
        if (scenario.Frame is null)
            Check("register.no-fabricated-experience",
                !RendererShadowChecks.LooksLikeInventedExperience(trimmed),
                "fabricated-experience", "the reply claims a lived experience it cannot have had");

        // ADMIT, in-register: if the plan carries an unknown, the reply must still name it.
        if (scenario.EpistemicUnknowns.Count > 0)
            Check("register.admit-preserved", UncertaintyMarkers.AdmitsNotLearned(trimmed),
                "admit-dropped", "the plan carried an unknown the reply did not admit");

        // Engages the turn, and does not trail off into a stock closer.
        Check("register.topical", SupplementChecks.IsTopicallyGrounded(scenario, trimmed),
            "off-topic", "the reply shares no content word with the turn or its stance");
        Check("register.no-stock-closer", !SupplementChecks.EndsOnStockCloser(trimmed),
            "stock-closer", "the reply ends on a closer that adds nothing");

        // Not a stub.
        Check("register.not-empty", trimmed.Length >= 8, "empty-reply", "the reply is a stub");

        return results;
    }

    private static bool Leaks(string neverText, string reply)
    {
        var tokens = RendererShadowChecks.DistinctiveTokens(neverText);
        var hits = tokens.Count(t => reply.Contains(t, StringComparison.OrdinalIgnoreCase));
        return hits >= 2 || tokens.Any(t => t.Length >= 8 && reply.Contains(t, StringComparison.OrdinalIgnoreCase));
    }
}
