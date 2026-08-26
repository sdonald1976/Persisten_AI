using System.Text.Json.Nodes;
using Companion.Core.Domain;
using Companion.PlanV3;

namespace Companion.MouthFactory.Schema;

/// <summary>Why a scenario could not become a plan. Terminal — the scenario is dropped, not fudged.</summary>
public sealed record PlanConstructionFailure(string Code, string Detail);

/// <summary>
/// Scenario truth → native Plan/4, through the real types and the real validators.
///
/// There is no factory-only plan schema anywhere in this file. Every plan is a
/// <see cref="Companion.PlanV3.PlanV3"/> built from <see cref="PlanItem"/>s, and before it is
/// allowed to become a row it must pass all three production gates:
///
///   1. <c>PlanV3Codec.Validate</c>            — structural
///   2. <c>PlanV3Codec.ValidateForAudience</c> — recipient authorization
///   3. <c>PlanV3Codec.CheckRenderEligibility</c> — may it be serialized at all
///
/// A scenario that cannot clear all three produces no row. This is the mechanism that makes
/// "a training row whose input differs from the shipping renderer format is invalid" enforceable
/// rather than aspirational: an unserializable plan has no bytes to train on.
/// </summary>
public static class PlanConstruction
{
    private static readonly RendererTrustContext LocalTrust = new(RendererTransport.local_loopback);

    /// <summary>Profanity values that restrict, and so need an owner and evidence.</summary>
    private static readonly string[] RestrictiveProfanity = ["avoid", "forbidden"];

    public static (global::Companion.PlanV3.PlanV3? Plan, PlanConstructionFailure? Failure) Build(
        ScenarioTruth scenario)
    {
        var participants = scenario.Participants
            .Select(p => new global::Companion.PlanV3.Participant(
                p.Id,
                p.Kind switch
                {
                    ParticipantKind.User => ParticipantRole.user,
                    ParticipantKind.Companion => ParticipantRole.companion,
                    _ => ParticipantRole.other,
                },
                p.Name))
            .ToList();

        var userId = participants.FirstOrDefault(p => p.Role == ParticipantRole.user)?.Id;
        if (userId is null || participants.All(p => p.Role != ParticipantRole.companion))
            return (null, new PlanConstructionFailure(
                "participants", "a plan requires both a user and a companion participant"));

        var items = new List<PlanItem>();

        foreach (var fact in scenario.ApprovedFacts)
        {
            var policy = fact.Policy switch
            {
                FactPolicy.MustExpress => ExpressionPolicy.must_express,
                FactPolicy.MayExpress => ExpressionPolicy.may_express,
                FactPolicy.BackgroundOnly => ExpressionPolicy.background_only,
                FactPolicy.MustNotExpress => ExpressionPolicy.must_not_express,
                FactPolicy.AdmitUnknown => ExpressionPolicy.admit_unknown,
                FactPolicy.AskRequired => ExpressionPolicy.ask_required,
                _ => ExpressionPolicy.may_express,
            };

            items.Add(new PlanItem
            {
                Id = fact.Id,
                Type = "fact",
                Policy = policy,
                Text = fact.Text,
                // "scenario" is not one of the AUTHORED sources the coaching lint polices, so
                // supplied scenario content is never mistaken for a producer coaching the mouth.
                Source = "scenario",
                Owner = fact.SubjectParticipantId,
                // must_not_express requires a reason code from a permitted family; supplying the
                // wrong one is a structural error the validator catches, which is the point.
                // must_not_express needs a reasonCode from a PERMITTED family
                // (user-preference. / privacy-audience. / tool-authorization. /
                // epistemic-integrity. / hosting-config.). A superseded or withheld fact is an
                // epistemic-integrity restriction: stating it would assert something untrue.
                ReasonCode = policy == ExpressionPolicy.must_not_express
                    ? "epistemic-integrity.superseded"
                    : null,
            });
        }

        // Superseded facts enter as must_not_express, which is exactly how production models a
        // correction: the stale claim is present so the mouth is told not to resurrect it.
        var supersededIndex = 0;
        foreach (var stale in scenario.Superseded)
        {
            items.Add(new PlanItem
            {
                Id = $"sup{++supersededIndex}",
                Type = "superseded",
                Policy = ExpressionPolicy.must_not_express,
                Text = stale.StaleText,
                Source = "supersession",
                ReasonCode = "epistemic-integrity.superseded",
            });
            items.Add(new PlanItem
            {
                Id = $"cur{supersededIndex}",
                Type = "fact",
                Policy = ExpressionPolicy.must_express,
                Text = stale.CurrentText,
                Source = "scenario",
            });
        }

        var unknownIndex = 0;
        foreach (var unknown in scenario.EpistemicUnknowns)
        {
            items.Add(new PlanItem
            {
                Id = $"unk{++unknownIndex}",
                Type = "epistemic",
                Policy = ExpressionPolicy.admit_unknown,
                Text = unknown,
                Source = "scenario",
            });
        }

        QuestionPolicyBlock? question = null;
        if (!string.Equals(scenario.Question.Policy, "none", StringComparison.OrdinalIgnoreCase)
            && scenario.Question.Text is { Length: > 0 } questionText)
        {
            var id = $"q{items.Count + 1}";
            items.Add(new PlanItem
            {
                Id = id,
                Type = "question",
                Policy = scenario.Question.Policy.Equals("must_ask", StringComparison.OrdinalIgnoreCase)
                    ? ExpressionPolicy.ask_required
                    : ExpressionPolicy.may_express,
                Text = questionText,
                Source = "scenario",
            });
            question = new QuestionPolicyBlock(
                scenario.Question.Policy.Equals("must_ask", StringComparison.OrdinalIgnoreCase)
                    ? QuestionPolicy.ask_required : QuestionPolicy.may_ask,
                id);
        }

        var plan = new global::Companion.PlanV3.PlanV3
        {
            Protocol = PlanV4Codec.Protocol,
            Act = ActFor(scenario),
            Participants = participants,
            Items = items,
            Question = question ?? new QuestionPolicyBlock(QuestionPolicy.question_forbidden, null),
            Register = new RegisterVector
            {
                Warmth = scenario.Register.Warmth,
                Bluntness = scenario.Register.Bluntness,
                Playfulness = scenario.Register.Playfulness,
                Teasing = scenario.Register.Teasing,
                Skepticism = scenario.Register.Skepticism,
                Intensity = scenario.Register.Intensity,
                Verbosity = scenario.Register.Verbosity,
                Profanity = scenario.Register.Profanity,
                Mirror = scenario.Register.Mirror,
            },
            Frame = FrameFor(scenario, participants.FirstOrDefault(p => p.Role == ParticipantRole.companion)?.Id),
            // A restrictive profanity setting is an exercise of AUTHORITY, and the protocol
            // refuses authority that is merely claimed: "avoid"/"forbidden" must name whose
            // preference it is and cite evidence for it. Permissive values need nothing, which
            // is the asymmetry the spec intends - restriction is what has to justify itself.
            RegisterRestrictions = RestrictiveProfanity.Contains(scenario.Register.Profanity)
                ?
                [
                    new RegisterRestriction(
                        "profanity", scenario.Register.Profanity, userId,
                        "user-preference.profanity",
                        new Provenance(Origin: "told-by-user", EvidenceRef: $"scenario:{scenario.Id}")),
                ]
                : null,
        };

        // ---- the three production gates, in order --------------------------------------------
        var structural = PlanV3Codec.Validate(plan);
        structural.AddRange(PlanV4Codec.ValidateFrame(plan));
        if (structural.Count > 0)
            return (null, new PlanConstructionFailure("invalid-plan", string.Join("; ", structural)));

        var audience = PlanV3Codec.ValidateForAudience(plan, [userId], LocalTrust);
        if (!audience.Ok)
            return (null, new PlanConstructionFailure("audience", string.Join("; ", audience.Errors)));

        var eligibility = PlanV3Codec.CheckRenderEligibility(plan);
        if (!eligibility.Eligible)
            return (null, new PlanConstructionFailure(
                "render-ineligible", string.Join("; ", eligibility.Reasons)));

        return (plan, null);
    }

    private static Frame? FrameFor(ScenarioTruth scenario, string? companionId)
    {
        if (scenario.Frame is not { } f)
            return null;
        var transition = f.Transition switch
        {
            "enter" => FrameTransition.enter,
            "continue" => FrameTransition.@continue,
            "switch" => FrameTransition.switchScene,
            "exit" => FrameTransition.exit,
            _ => FrameTransition.@continue,
        };
        return new Frame
        {
            // S9: real mode is legal ONLY on the exit turn, and exit is precisely the turn that
            // returns to the real world. Anything else stays in fiction.
            Mode = transition == FrameTransition.exit ? FrameMode.real : FrameMode.fiction,
            Transition = transition,
            // S2: continue and switch require a scene to be within. A scenario that omits one
            // fails validation rather than being silently given a generated id.
            SceneRef = f.SceneRef,
            Narration = f.NarratorVoice ? FrameNarration.licensed : FrameNarration.forbidden,
            Continuity = transition == FrameTransition.enter
                ? FrameContinuity.none : FrameContinuity.maintain,
            Characters = f.Characters
                .Select(c => new FrameCharacter(c, c, companionId))
                .ToList(),
        };
    }

    /// <summary>
    /// The conversational act. Taken from the scenario's own shape rather than guessed: a
    /// question-bearing plan asks, a correction accepts, everything else answers.
    /// </summary>
    private static string ActFor(ScenarioTruth scenario)
        => scenario.Superseded.Count > 0 ? "accept-correction"
            : scenario.Question.Policy.Equals("must_ask", StringComparison.OrdinalIgnoreCase) ? "clarify"
            : scenario.EpistemicUnknowns.Count > 0 ? "admit-unknown"
            : scenario.ApprovedFacts.Count == 0 ? "acknowledge"
            : "answer-question";
}
