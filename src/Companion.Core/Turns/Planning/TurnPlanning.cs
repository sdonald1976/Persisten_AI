using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Companion.PlanV3;

namespace Companion.Core.Turns.Planning;

/// <summary>
/// The production plan — Plan/2 — and the decision that records it.
///
/// This is the path that has authority today. It is deliberately a separate type from the
/// native material below, so no caller can pass one where the other is meant.
/// </summary>
public sealed record ProductionPlanResult
{
    public required ResponsePlan Plan { get; init; }
    public required DecisionRecord Decision { get; init; }
}

/// <summary>
/// The native plan/3-plan/4 material. Shadow evidence only: nothing here reaches a model,
/// and a failure costs a diagnostic rather than the turn.
///
/// Kept structurally distinct from <see cref="ProductionPlanResult"/> because the two are
/// parallel derivations from the same upstream state, never translations of each other. A
/// single "plan" type would make that confusion possible in one careless assignment.
/// </summary>
public sealed record NativePlanResult
{
    /// <summary>The native plan, or null when the build was refused or failed.</summary>
    public PlanV3.PlanV3? Plan { get; init; }

    /// <summary>The assembler's report when contributors ran. Null before contribution.</summary>
    public AssemblyReport? Assembly { get; init; }

    /// <summary>Content-safe diagnostic. Never carries user text.</summary>
    public string? BuildError { get; init; }

    public IReadOnlyList<string> LintRejections { get; init; } = [];

    /// <summary>Decisions produced here, appended by the caller at their existing positions.</summary>
    public IReadOnlyList<DecisionRecord> Decisions { get; init; } = [];
}

/// <summary>
/// The fourth stage of a turn: decide what the response should EXPRESS, before anything is
/// rendered or said.
///
/// Two parallel derivations live here and stay parallel. Plan/2 is what production uses.
/// The native plan/3-plan/4 material is shadow evidence built from the same upstream state
/// and never FROM Plan/2 — translating one into the other is exactly the confusion the
/// separate result types exist to prevent.
///
/// It owns nothing beyond deciding. Not serialization — the CompactV4 length probe stays
/// with the caller, because computing bytes is not planning. Not packet assembly, not
/// prompt rendering, not tools, not model calls, not renderer selection, not fidelity
/// scoring, not persistence, not observability storage. Frame LIFECYCLE stays with the turn
/// too; only the frame's contribution to the native plan is shaped here.
///
/// Several methods rather than one, because the turn's existing order interleaves planning
/// with tools, mood and the frame: the production plan is built before tools run, the native
/// plan immediately after it, and contribution only once the tool outcome and the frame exist.
/// Reordering to produce one attractive method would change the turn.
/// </summary>
public sealed class TurnPlanning(
    IOptions<CompanionOptions> options,
    ILogger<TurnPlanning> logger,
    // Optional and defaulted, exactly as they were on Companion: the preference store may be
    // absent in a reduced host, and IActivityInstanceProvider has no implementation at all
    // yet — it is a declared seam awaiting its producer. A required dependency here would
    // fail container validation for a capability that is deliberately dormant.
    IUserPreferenceStore? userPreferences = null,
    IActivityInstanceProvider? activities = null)
{
    private readonly CompanionOptions _options = options.Value;

    /// <summary>
    /// Plan/2: what Ava has DECIDED this turn — act, acknowledgments with error ownership,
    /// content authority levels, epistemic constraints, the question if any.
    /// </summary>
    public ProductionPlanResult BuildProductionPlan(
        Guid traceId,
        TurnIntentState intent,
        WorkingContextState working,
        string promptText,
        IReadOnlyList<RetrievalResult> selectedMemories,
        ConceptLookupResult? knowledge,
        string? curiosityQuestion,
        string? registerNote,
        string? moodNote,
        string? persona)
    {
        var plan = ResponsePlanner.Build(
            traceId, intent, working, promptText, selectedMemories, knowledge,
            curiosityQuestion, registerNote, moodNote, persona);

        return new ProductionPlanResult
        {
            Plan = plan,
            Decision = new DecisionRecord
            {
                Stage = "plan", Decider = "rule",
                Verdict = plan.Act.ToKebab()
                    + (plan.Acknowledgments.Count > 0 ? $"|ack={plan.Acknowledgments.Count}" : "")
                    + (plan.Content.Count(c => c.Requirement == ContentRequirement.MustState) is var must && must > 0 ? $"|must={must}" : "")
                    + (plan.Epistemic.Count > 0 ? $"|epistemic={plan.Epistemic.Count}" : "")
                    + (plan.Question is not null ? $"|q={plan.Question.Kind.ToKebab()}" : ""),
            },
        };
    }

    /// <summary>
    /// The NATIVE plan, built from the same upstream state as Plan/2 — never from it. A
    /// failed build records a content-safe diagnostic and the turn continues unchanged.
    /// </summary>
    public NativePlanResult BuildNativePlan(
        Guid traceId,
        TurnIntentState intent,
        WorkingContextState working,
        string promptText,
        IReadOnlyList<RetrievalResult> selectedMemories,
        ConceptLookupResult? knowledge,
        string? curiosityQuestion,
        bool sensitiveTurn,
        string userId,
        string? companionDisplay)
    {
        PlanV3.PlanV3? nativeV3 = null;
        string? buildError = null;
        IReadOnlyList<string> lintRejections = [];

        try
        {
            var built = PlanV3Builder.Build(
                traceId, intent, working, promptText, selectedMemories, knowledge,
                curiosityQuestion, sensitiveTurn: sensitiveTurn,
                userParticipantId: userId, userDisplay: userId,
                companionParticipantId: "companion-ava",
                companionDisplay: companionDisplay ?? "Ava");
            nativeV3 = built.Plan;
            lintRejections = built.LintRejections;
        }
        catch (Exception ex)
        {
            buildError = $"{ex.GetType().Name}: {Truncate(ex.Message, 120)}";
            logger.LogDebug(ex, "Native v3 build failed for {TraceId}; production unaffected.", traceId);
        }

        return new NativePlanResult
        {
            Plan = nativeV3,
            BuildError = buildError,
            LintRejections = lintRejections,
            Decisions =
            [
                new DecisionRecord
                {
                    Stage = "plan.native-v3", Decider = "rule",
                    Verdict = nativeV3 is not null ? "built" : "failed",
                    Reason = buildError
                        ?? (lintRejections.Count > 0 ? $"lint-rejected:{lintRejections.Count}" : null),
                },
            ],
        };
    }

    /// <summary>
    /// Folds this turn's typed contributions into the native plan through the contribution
    /// boundary, then attaches the frame if one is active.
    ///
    /// The assembler alone grants authority: a refused, secret-bearing or unexecuted call
    /// contributes nothing, a failure can only be acknowledged, and nothing reaches
    /// must_express without a planner disposition.
    /// </summary>
    public async Task<NativePlanResult> ContributeAsync(
        NativePlanResult built,
        Guid traceId,
        string userId,
        string promptText,
        bool sensitive,
        ResponsePlan productionPlan,
        ToolLoop.Outcome toolOutcome,
        WorkingContextState working,
        CompanionStateSnapshot innerState,
        FamiliaritySnapshot familiarity,
        Guid conversationId,
        Frame? nativeFrame,
        CancellationToken ct = default)
    {
        if (built.Plan is null)
            return built;

        var nativeV3 = built.Plan;
        var buildError = built.BuildError;
        var lintRejections = built.LintRejections;
        AssemblyReport? assembly = null;

        try
        {
            var contributors = new List<IPlanV3Contributor>();
            if (toolOutcome.TypedOutcomes.Count > 0)
            {
                contributors.Add(new ToolOutcomeContributor(toolOutcome.TypedOutcomes));
                contributors.Add(new ToolAuthorizationContributor(toolOutcome.TypedOutcomes));
            }

            // Source 3: the user's explicit standing preferences. Register preferences vote;
            // expression restrictions become must_not_express notes; every one cites its
            // record id. Hosting configuration votes as its OWN authority so a deployment
            // restriction can never masquerade as something the user asked for.
            if (userPreferences is not null)
            {
                var active = await userPreferences.GetActiveAsync(userId, ct);
                if (active.Count > 0)
                    contributors.Add(new UserPreferenceContributor(active));
            }
            if (_options.HostingRegisterRestrictions.Count > 0)
                contributors.Add(new HostingConfigContributor(_options.HostingRegisterRestrictions));

            // Sources 1a/1b: the active activity instance, if a producer exists. Today none
            // does, and it contributes nothing.
            if (activities is not null
                && await activities.GetActiveAsync(userId, conversationId, ct) is { } activity)
            {
                contributors.Add(new ActivityInstanceContributor(activity));
            }

            // Source 4a: the turn's own typed cognitive state votes on verbosity and nothing
            // else. Source 4b: Ava's mood modulates intensity, citing the transition it
            // descends from. Source 4c: how far along the relationship actually is.
            contributors.Add(WorkingContextContributor.From(traceId, working));
            contributors.Add(new MoodContributor(innerState));
            contributors.Add(new FamiliarityContributor(familiarity));

            if (contributors.Count > 0)
            {
                var context = new PlanContributionContext(
                    traceId, productionPlan.Act.ToKebab(), promptText, userId, "companion-ava", sensitive);
                var report = PlanV3Assembler.Assemble(
                    context, contributors, SourceRegistry.Default, nativeV3);
                nativeV3 = report.Plan;
                assembly = report;
                lintRejections = [.. lintRejections, .. report.LintRejections];
            }
        }
        catch (Exception ex)
        {
            // The assembly is diagnostic; losing it must never cost the turn or the row the
            // other sources already earned.
            buildError = $"{ex.GetType().Name}: {Truncate(ex.Message, 120)}";
            logger.LogDebug(ex, "Native v3 tool assembly failed for {TraceId}; production unaffected.", traceId);
        }

        // plan/4: the frame rides the native plan. RECORDED, never sent — no plan/4 text
        // reaches a model until Run-2 is trained and promoted. Serializing it to measure its
        // size is the caller's job, not planning's.
        if (nativeFrame is not null && nativeV3 is not null)
        {
            nativeV3 = nativeV3 with
            {
                Protocol = PlanV4Codec.Protocol,
                Frame = nativeFrame,
            };
        }

        var decisions = new List<DecisionRecord>();
        if (assembly is not null || buildError is not null)
            decisions.Add(new DecisionRecord
            {
                Stage = "plan.native-v3.tools", Decider = "rule",
                Verdict = assembly is null ? "failed"
                    : $"accepted={assembly.Accepted}|rejected={assembly.Rejected}",
                Reason = assembly?.AuthorityViolations.Count > 0
                    ? $"violations:{assembly.AuthorityViolations.Count}" : buildError,
            });

        return new NativePlanResult
        {
            Plan = nativeV3,
            Assembly = assembly,
            BuildError = buildError,
            LintRejections = lintRejections,
            Decisions = decisions,
        };
    }

    /// <summary>
    /// The frame's contribution to the native plan, read from the authoritative session.
    /// Reads only what the session records — no scene content, and nothing inferred from the
    /// message. Shaping this is planning; deciding and persisting the transition is not.
    /// </summary>
    public static Frame BuildFrame(FrameTransition transition, FrameSession session)
    {
        var exiting = transition == FrameTransition.exit;
        var characters = System.Text.Json.JsonSerializer
            .Deserialize<List<FrameCharacter>>(session.CharactersJson) ?? [];

        return new Frame
        {
            // Exiting restores real rules ON this turn, not the next one.
            Mode = exiting ? FrameMode.real : FrameMode.fiction,
            Transition = transition,
            SceneRef = exiting ? null : session.SceneRef,
            Narration = exiting || session.Narration != "licensed"
                ? FrameNarration.forbidden
                : FrameNarration.licensed,
            Continuity = session.Continuity == "maintain"
                ? FrameContinuity.maintain
                : FrameContinuity.none,
            ActiveCompanionCharacterId = exiting ? null : session.ActiveCompanionCharacterId,
            Characters = exiting ? [] : characters,
        };
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max];
}
