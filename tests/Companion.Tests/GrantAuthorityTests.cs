using Companion.PlanV3;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// P5b authority hardening: grants are exact (source + category + policy + reason prefix
/// + provenance + promotion) tuples, never a Cartesian product. The procedure audit is
/// explicit — a procedure may ask its activity's question and frame the activity, and
/// nothing else. Every registered contributor gets the same tuple audit.
/// </summary>
public class GrantAuthorityTests
{
    private static readonly PlanContributionContext Ctx = new(
        Guid.Parse("bbbbbbbb-1111-2222-3333-444444444444"),
        "answer-question", "synthetic", "usr-synth", "companion-ava", SensitiveTurn: false);

    private static PlanV3.PlanV3 Seed() => new()
    {
        TraceId = Ctx.TraceId,
        Participants =
        [
            new Participant("usr-synth", ParticipantRole.user, "SynthUser"),
            new Participant("companion-ava", ParticipantRole.companion, "Ava"),
        ],
        Act = "answer-question",
        Question = new QuestionPolicyBlock(QuestionPolicy.question_forbidden),
        Items = [],
        Register = PlanV3Codec.Canonicalize(new RegisterVector()),
    };

    /// <summary>A contributor that proposes exactly one arbitrary tuple, for auditing.</summary>
    private sealed class Prober(string source, RenderCategory category, ExpressionPolicy policy,
        string? reason = null, string? origin = null, string? evidence = null,
        bool promotion = false, RegisterProposal? vote = null) : IPlanV3Contributor
    {
        public string SourceId => source;
        public PlanContributionResult Contribute(PlanContributionContext c) => new(
            [new ProposedItem
            {
                LocalId = "p1", Type = "probe", Category = category, ProposedPolicy = policy,
                Text = "A synthetic probe.", ReasonCode = reason, PlanningPromotion = promotion,
                Provenance = origin is null && evidence is null
                    ? null : new Provenance(Origin: origin, EvidenceRef: evidence),
            }],
            vote is null ? null : [vote]);
    }

    private static AssemblyReport Run(IPlanV3Contributor c)
        => PlanV3Assembler.Assemble(Ctx, [c], SourceRegistry.Default, Seed());

    private static void AssertRefused(AssemblyReport r, string reasonFragment)
    {
        Assert.DoesNotContain(r.Plan.Items, i => i.Policy is ExpressionPolicy.must_express
            or ExpressionPolicy.must_not_express or ExpressionPolicy.ask_required);
        Assert.Contains(r.Outcomes, o => o.Decision is "rejected" or "downgraded"
            && (o.Reason ?? "").Contains(reasonFragment));
    }

    // ---- the procedure audit -------------------------------------------------------------

    [Fact]
    public void Procedure_MayAskItsActivityQuestion_AndFrameTheActivity()
    {
        var ask = Run(new Prober("procedure", RenderCategory.clarify, ExpressionPolicy.ask_required));
        Assert.Single(ask.Plan.Items, i => i.Policy == ExpressionPolicy.ask_required);
        Assert.Empty(ask.AuthorityViolations);

        var frame = Run(new Prober("procedure", RenderCategory.state, ExpressionPolicy.background_only));
        Assert.Single(frame.Plan.Items, i => i.Policy == ExpressionPolicy.background_only);
        Assert.Empty(frame.AuthorityViolations);
    }

    [Fact]
    public void Procedure_CannotForceUnrelatedClaims()
    {
        // `claim` is not among procedure's granted categories at all: refused at the
        // category gate, before any policy question arises.
        var claim = Run(new Prober("procedure", RenderCategory.claim, ExpressionPolicy.must_express));
        AssertRefused(claim, "category-not-permitted");

        // And it cannot smuggle a mandatory claim through its GRANTED `state` category —
        // this is the tuple check proper: state is allowed, must_express is not with it.
        var stateClaim = Run(new Prober("procedure", RenderCategory.state, ExpressionPolicy.must_express));
        AssertRefused(stateClaim, "combination-not-granted");
        Assert.Contains(stateClaim.AuthorityViolations, v => v.Contains("combination not granted"));
    }

    [Fact]
    public void Procedure_CannotSuppressOrdinarySpeech()
    {
        // must_not_express exists ONLY under the activity-state reason prefix with evidence.
        var suppress = Run(new Prober("procedure", RenderCategory.memory, ExpressionPolicy.must_not_express,
            reason: "epistemic-integrity.superseded-or-disputed", evidence: "activity:1"));
        AssertRefused(suppress, "category-not-permitted");

        // Right category, but a reason OUTSIDE the granted scope: refused, and the
        // violation names the scope it tried to exceed.
        var outOfScope = Run(new Prober("procedure", RenderCategory.state, ExpressionPolicy.must_not_express,
            reason: "epistemic-integrity.superseded-or-disputed", evidence: "activity:1"));
        AssertRefused(outOfScope, "reason-outside-granted-scope");
        Assert.Contains(outOfScope.AuthorityViolations,
            v => v.Contains("outside granted scope 'epistemic-integrity.activity-state.*'"));
    }

    [Fact]
    public void Procedure_HasNoGeneralEpistemicIntegrityOwnership_AndNeedsEvidence()
    {
        // In-scope but unevidenced: refused.
        var unevidenced = Run(new Prober("procedure", RenderCategory.state, ExpressionPolicy.must_not_express,
            reason: "epistemic-integrity.activity-state.question-12-retired"));
        AssertRefused(unevidenced, "grant-requires-evidence");

        // In-scope, evidenced: permitted — and scoped to the procedure's OWN state.
        var scoped = Run(new Prober("procedure", RenderCategory.state, ExpressionPolicy.must_not_express,
            reason: "epistemic-integrity.activity-state.question-12-retired",
            evidence: "activity:tq-2026-08-24-01"));
        Assert.Single(scoped.Plan.Items, i => i.Policy == ExpressionPolicy.must_not_express);
        Assert.Empty(scoped.AuthorityViolations);
    }

    [Fact]
    public void Procedure_CannotAlterRegisterOrProfanity()
    {
        var report = Run(new Prober("procedure", RenderCategory.state, ExpressionPolicy.background_only,
            vote: new RegisterProposal("profanity", "forbidden", "user-preference.no-swearing",
                new Provenance(EvidenceRef: "preference:1"), Restrictive: true)));

        Assert.Equal("neutral", report.Plan.Register.Profanity);
        Assert.Null(report.Plan.RegisterRestrictions);
        Assert.Contains(report.AuthorityViolations, v => v.Contains("register vote without register authority"));
    }

    // ---- the same audit, applied to every other registered contributor -------------------

    [Fact]
    public void Tool_CannotPromoteItsOwnObservationIntoAClaim()
    {
        // (observation, must_express) is not a granted tuple even with a promotion flag.
        var forged = Run(new Prober("tool", RenderCategory.observation, ExpressionPolicy.must_express,
            origin: "tool", promotion: true));
        AssertRefused(forged, "combination-not-granted");

        // (claim, must_express) IS granted — but only as a planner-authorized promotion.
        var authorized = Run(new Prober("tool", RenderCategory.claim, ExpressionPolicy.must_express,
            origin: "tool", promotion: true));
        Assert.Single(authorized.Plan.Items, i => i.Policy == ExpressionPolicy.must_express);
        Assert.Contains(authorized.Outcomes, o => o.Decision == "promoted");
    }

    [Fact]
    public void Tool_CannotClaimAnOriginItDoesNotHave()
    {
        var forged = Run(new Prober("tool", RenderCategory.claim, ExpressionPolicy.background_only,
            origin: "told-by-user"));
        AssertRefused(forged, "origin-not-permitted-for-grant");
        Assert.Contains(forged.AuthorityViolations, v => v.Contains("not permitted for this grant"));
    }

    [Fact]
    public void Perception_CanBePromotedToOptional_NeverToMandatory()
    {
        var optional = Run(new Prober("vision", RenderCategory.observation, ExpressionPolicy.may_express,
            origin: "observed", promotion: true));
        Assert.Single(optional.Plan.Items, i => i.Policy == ExpressionPolicy.may_express);

        var mandatory = Run(new Prober("vision", RenderCategory.observation, ExpressionPolicy.must_express,
            origin: "observed", promotion: true));
        AssertRefused(mandatory, "combination-not-granted");
    }

    [Fact]
    public void RegisterSources_HaveZeroItemGrants_SoTheyCannotSpeakAtAll()
    {
        foreach (var source in new[] { "persona", "mood", "relationship", "user-preference", "mirror" })
        {
            var report = Run(new Prober(source, RenderCategory.claim, ExpressionPolicy.may_express));
            Assert.Empty(report.Plan.Items);
            Assert.Contains(report.Outcomes, o => o.Decision == "rejected"
                && o.Reason == "category-not-permitted");
        }
    }

    [Fact]
    public void ToolAuthorization_MayWithhold_ButOnlyUnderItsOwnFamily()
    {
        var proper = Run(new Prober("tool-authorization", RenderCategory.note,
            ExpressionPolicy.must_not_express, reason: "tool-authorization.result-unauthorized"));
        Assert.Single(proper.Plan.Items, i => i.Policy == ExpressionPolicy.must_not_express);

        var poaching = Run(new Prober("tool-authorization", RenderCategory.note,
            ExpressionPolicy.must_not_express, reason: "user-preference.dislikes-tools",
            evidence: "preference:3"));
        AssertRefused(poaching, "reason-outside-granted-scope");
    }

    /// <summary>Every grant in the registry must carry a documented use case — the rule
    /// that keeps the table from growing silent, unexplained authority.</summary>
    [Fact]
    public void EveryGrant_DocumentsAConcreteUseCase()
    {
        foreach (var cap in SourceRegistry.Default.Values)
            foreach (var grant in cap.Grants)
                Assert.False(string.IsNullOrWhiteSpace(grant.UseCase),
                    $"{cap.SourceId}: {grant.Category}+{grant.Policy} has no documented use case");
    }

    /// <summary>The Cartesian trap, stated as a test: no source may hold every pairing of
    /// its own categories and policies unless each pairing is separately justified.</summary>
    [Fact]
    public void AuthorityIsCombinatorial_NotCartesian()
    {
        foreach (var cap in SourceRegistry.Default.Values)
        {
            var categories = cap.Grants.Select(g => g.Category).Distinct().Count();
            var policies = cap.Grants.Select(g => g.Policy).Distinct().Count();
            if (categories > 1 && policies > 1)
                Assert.True(cap.Grants.Count < categories * policies,
                    $"{cap.SourceId} holds every category×policy pairing — grants must be explicit");
        }
    }
}
