using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Companion.Core.Services;

/// <summary>
/// Orchestrates one conversation turn:
/// store message â†’ (resolve a pending clarification, or detect a new ambiguity) â†’
/// resolve project &amp; build project context â†’ retrieve memories â†’ assemble bounded context â†’
/// generate â†’ store â†’ extract &amp; validate memories â†’ update project/open-loop state â†’ trace.
///
/// Ambiguity is a control-flow state, not a prompt note: when a project reference is materially
/// ambiguous the turn stores a deterministic clarifying question and a pending-resolution record
/// and STOPS â€” no retrieval-for-answer, no chat generation, no memory extraction, no project
/// mutation. The next message tries to resolve that pending item before being treated as new.
/// </summary>
public sealed class Companion : ICompanion
{
    private readonly IConversationStore _conversations;
    private readonly IProjectContextService _projectContext;
    private readonly IPendingClarificationStore _pending;
    private readonly IProfileStore _profiles;
    private readonly IContextAssembler _assembler;

    // All three optional and defaulted, so every existing construction site â€” and every test â€”
    // keeps working with a gate that is simply not there.
    private readonly IShadowRecorder _shadow;
    private readonly IRendererShadow _rendererShadow;
    private readonly IFrameSessionStore? _frames;
    private readonly ICognitiveCapture _capture;
    private readonly IDiagnosticsStore? _diagnostics;
    private readonly IConceptKnowledge? _concepts;
    private readonly IGapStore? _gaps;

    /// <summary>The serialized-plan contract uses the same camelCase + kebab-enum shape as
    /// every other JSON boundary — this string IS the future renderer's input format.</summary>
    private static readonly System.Text.Json.JsonSerializerOptions PlanJson =
        new(System.Text.Json.JsonSerializerDefaults.Web);
    private readonly IPersonalityService _personality;
    private readonly IMemoryPipeline _pipeline;
    private readonly IProjectUpdater _projectUpdater;
    private readonly IProjectStore _projects;
    private readonly IEmotionStore _emotions;
    private readonly IReflectionStore _reflections;
    private readonly IAnticipationStore _anticipations;
    private readonly ICompanionStateTracker _innerState;
    private readonly IPrivacyClassifier _privacy;
    private readonly IAttentionService _attention;
    private readonly IProcedureStore _procedures;
    private readonly ITurnTraceLog _turnLog;
    private readonly CompanionOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<Companion> _logger;
    private readonly Turns.Admission.TurnAdmission _admission;
    private readonly Turns.Context.TurnContext _context;
    private readonly Turns.Planning.TurnPlanning _planning;
    private readonly Turns.Execution.TurnExecution _execution;

    public Companion(
        IConversationStore conversations,
        IProjectContextService projectContext,
        IPendingClarificationStore pending,
        IProfileStore profiles,
        IContextAssembler assembler,
        IPersonalityService personality,
        IMemoryPipeline pipeline,
        IProjectUpdater projectUpdater,
        IProjectStore projects,
        IEmotionStore emotions,
        IReflectionStore reflections,
        IAnticipationStore anticipations,
        ICompanionStateTracker innerState,
        IPrivacyClassifier privacy,
        IAttentionService attention,
        IProcedureStore procedures,
        ITurnTraceLog turnLog,
        IOptions<CompanionOptions> options,
        TimeProvider clock,
        ILogger<Companion> logger,
        Turns.Admission.TurnAdmission admission,
        Turns.Context.TurnContext context,
        Turns.Planning.TurnPlanning planning,
        Turns.Execution.TurnExecution execution,
        IShadowRecorder? shadow = null,
        ICognitiveCapture? capture = null,
        IDiagnosticsStore? diagnostics = null,
        IConceptKnowledge? concepts = null,
        IGapStore? gaps = null,
        IRendererShadow? rendererShadow = null,
        IFrameSessionStore? frames = null)
    {
        _shadow = shadow ?? new NoShadowRecorder();
        _rendererShadow = rendererShadow ?? new NullRendererShadow();
        _frames = frames;
        _capture = capture ?? new NoCognitiveCapture();
        _diagnostics = diagnostics;
        _concepts = concepts;
        _gaps = gaps;
        _conversations = conversations;
        _projectContext = projectContext;
        _pending = pending;
        _profiles = profiles;
        _assembler = assembler;
        _personality = personality;
        _pipeline = pipeline;
        _projectUpdater = projectUpdater;
        _projects = projects;
        _emotions = emotions;
        _reflections = reflections;
        _anticipations = anticipations;
        _innerState = innerState;
        _privacy = privacy;
        _attention = attention;
        _procedures = procedures;
        _turnLog = turnLog;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
        _admission = admission;
        _context = context;
        _planning = planning;
        _execution = execution;
    }

    public async Task<TurnTrace> RespondAsync(
        string userId, Guid conversationId, string userMessage,
        IProgress<string>? tokenSink = null, CancellationToken ct = default)
    {
        // Admission (Turns/Admission/TurnAdmission): validation, conversation resolution and
        // ownership, the turn's instant, the pre-storage temporal anchor, the stored user
        // message that becomes the turn's evidence identity, and the pending-clarification
        // lookup. Moved verbatim, in the same order, with the same exceptions.
        var admitted = await _admission.AdmitAsync(userId, conversationId, userMessage, ct);

        var userMsg = admitted.UserMessage;
        var now = admitted.Now;
        var lastSeenBefore = admitted.LastSeenBefore;

        // If a clarification is pending in this conversation, try to resolve it before treating
        // this message as a brand-new request.
        if (admitted.Pending is { } pending)
            return await ResolvePendingAsync(userId, conversationId, pending, userMsg, tokenSink, now, lastSeenBefore, ct);

        // 3. Resolve the project reference and build project-aware context.
        var projectContext = await _projectContext.BuildAsync(userId, userMessage, ct);

        // Ambiguity is control flow: ask, record a pending item, and run nothing else.
        if (projectContext.Resolution.RequiresClarification)
            return await RequestClarificationAsync(userId, conversationId, userMsg, projectContext, now, ct);

        // Normal answered turn.
        return await CompleteTurnAsync(
            userId, conversationId, userMessage, projectContext,
            extractionSource: userMsg, replyToId: userMsg.Id, TurnStatus.Answered, pendingId: null, tokenSink, now, lastSeenBefore, ct);
    }

    // ---- ambiguity: request ----

    private async Task<TurnTrace> RequestClarificationAsync(
        string userId, Guid conversationId, Message userMsg, ProjectContext projectContext,
        DateTimeOffset now, CancellationToken ct)
    {
        var question = projectContext.Resolution.ClarificationQuestion ?? "Which one do you mean?";

        var candidates = projectContext.Resolution.Candidates
            .Take(3)
            .Select(c => new ClarificationCandidate(c.Project.Id, c.Project.Name, c.Score))
            .ToList();

        var pending = new PendingClarification
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ConversationId = conversationId,
            OriginalMessageId = userMsg.Id,
            OriginalText = userMsg.Content,
            AmbiguityType = AmbiguityType.Project,
            CandidatesJson = ClarificationCandidate.Serialize(candidates),
            Question = question,
            CreatedAt = now,
            Status = ClarificationStatus.Pending,
        };
        await _pending.AddAsync(pending, ct);

        // The clarification is deterministic application text, not a model generation.
        await StoreMessageAsync(userId, conversationId, MessageRole.Assistant, question, userMsg.Id, _clock.GetUtcNow(), ct);

        _logger.LogInformation(
            "Turn paused for clarification for {UserId}: {Candidates} candidates, pending {Pending}",
            userId, candidates.Count, pending.Id);

        return ClarificationTrace(userMsg.Content, question, TurnStatus.ClarificationRequested, pending.Id, projectContext);
    }

    // ---- ambiguity: resolve a pending item on the next message ----

    private async Task<TurnTrace> ResolvePendingAsync(
        string userId, Guid conversationId, PendingClarification pending, Message replyMsg,
        IProgress<string>? tokenSink, DateTimeOffset now, DateTimeOffset? lastSeenBefore, CancellationToken ct)
    {
        var decision = ClarificationResolver.Resolve(replyMsg.Content, pending.Candidates());

        if (decision.Kind == ClarificationDecisionKind.Cancelled)
        {
            pending.Status = ClarificationStatus.Cancelled;
            pending.ResolutionNote = "user cancelled";
            pending.ResolvedAt = now;
            await _pending.UpdateAsync(pending, ct);

            const string ack = "No problem â€” I've dropped that. What would you like to do instead?";
            await StoreMessageAsync(userId, conversationId, MessageRole.Assistant, ack, replyMsg.Id, _clock.GetUtcNow(), ct);
            return ClarificationTrace(replyMsg.Content, ack, TurnStatus.ClarificationCancelled, pending.Id);
        }

        if (decision.Kind == ClarificationDecisionKind.StillAmbiguous)
        {
            // Ask again rather than guess. The pending item stays open.
            await StoreMessageAsync(userId, conversationId, MessageRole.Assistant, pending.Question, replyMsg.Id, _clock.GetUtcNow(), ct);
            return ClarificationTrace(replyMsg.Content, pending.Question, TurnStatus.ClarificationRequested, pending.Id);
        }

        // Resolved â†’ record the audit trail and resume the ORIGINAL request with the chosen project.
        pending.Status = ClarificationStatus.Resolved;
        pending.ResolvedProjectId = decision.ProjectId;
        pending.ResolutionNote = decision.Note;
        pending.ResolvedAt = now;
        await _pending.UpdateAsync(pending, ct);

        var forced = await _projectContext.BuildForProjectAsync(userId, pending.OriginalText, decision.ProjectId!.Value, ct);

        // Extract from the ORIGINAL message (now disambiguated), never from the terse reply
        // ("the buoy one") â€” so a clarification answer never becomes a durable memory on its own.
        var originalMsg = await _conversations.GetMessageAsync(pending.OriginalMessageId, userId, ct) ?? replyMsg;

        _logger.LogInformation(
            "Resolved clarification {Pending} for {UserId} â†’ project {Project} ({Note})",
            pending.Id, userId, decision.ProjectId, decision.Note);

        return await CompleteTurnAsync(
            userId, conversationId, pending.OriginalText, forced,
            extractionSource: originalMsg, replyToId: replyMsg.Id, TurnStatus.ClarificationResolved, pending.Id, tokenSink, now, lastSeenBefore, ct);
    }

    // ---- the normal turn tail (retrieve â†’ generate â†’ store â†’ extract â†’ update) ----

    private async Task<TurnTrace> CompleteTurnAsync(
        string userId, Guid conversationId, string promptText, ProjectContext projectContext,
        Message extractionSource, Guid replyToId, TurnStatus status, Guid? pendingId,
        IProgress<string>? tokenSink, DateTimeOffset now, DateTimeOffset? lastSeenBefore, CancellationToken ct)
    {
        // Privacy gate, computed up front: a "don't remember this conversation" turn produces a
        // reply but writes NO durable derived memory â€” no extraction, no project/open-loop updates,
        // and no emotional signal. Raw messages are still stored for in-session context.
        var conversation = await _conversations.GetConversationAsync(conversationId, userId, ct);
        var sensitive = await _privacy.ShouldSkipDerivedMemoryAsync(promptText, ct);
        var remember = _options.EnableExtraction && !(conversation?.DoNotRemember ?? false) && !sensitive;
        if (sensitive)
            _logger.LogInformation("Derived memory skipped for {UserId}: privacy classifier marked the turn sensitive.", userId);

        // Roleplay gate: an in-character turn (RP markup, or a relationship word the persona has
        // claimed) gets a full reply but leaves
        // no durable derived memory, exactly like a private turn. Enjoying the play without
        // believing it is what keeps personas from leaking into the fact store.
        var profile = await _profiles.GetOrCreateAsync(userId, ct);
        var persona = _personality.Compose(profile);
        var companionIdentity = _personality.Identity(profile);
        var identityProjection = PromptIdentityProjector.From(profile, companionIdentity);
        var lexicon = PersonaLexicon.From(companionIdentity.Name, persona);
        var inCharacter = InCharacterDetector.IsInCharacter(promptText, lexicon);
        if (remember && inCharacter)
            _logger.LogDebug("In-character turn for {UserId}: derived memory skipped.", userId);
        var extractFacts = remember && !inCharacter;

        // Phase 0 of the language-organ plan (docs/LANGUAGE_ORGAN.md): the system-level
        // decisions this turn makes are recorded in pipeline order, so the diagnostics ring
        // answers "what did OUR architecture decide?" separately from "what did the model say?".
        // Recording adds no authority â€” every entry below was already being decided.
        var traceId = Guid.NewGuid();
        var decisions = new List<DecisionRecord>
        {
            new()
            {
                Stage = "privacy", Decider = "model",
                Verdict = sensitive ? "sensitive" : "not-sensitive",
            },
            new()
            {
                Stage = "roleplay", Decider = "rule",
                Verdict = inCharacter ? "in-character" : "plain",
            },
            new()
            {
                Stage = "memory.derived", Decider = "rule",
                Verdict = extractFacts ? "enabled" : "disabled",
                Reason = extractFacts ? null
                    : sensitive ? "privacy classifier marked the turn sensitive"
                    : (conversation?.DoNotRemember ?? false) ? "do-not-remember conversation"
                    : !_options.EnableExtraction ? "extraction disabled"
                    : "in-character turn",
            },
            new()
            {
                Stage = "project", Decider = "rule",
                Verdict = projectContext.ResolvedProjectName ?? "none",
            },
        };

        // Recent prior turns (exclude the extraction source we just handled). Fetched BEFORE
        // retrieval since the working-context read below shapes the retrieval query.
        var recent = await _context.LoadHistoryAsync(
            conversationId, userId, extractionSource.Id, ct);

        // 3c. Understanding (Turns/Understanding/TurnUnderstanding): the system's explicit
        // read of the conversation — open questions, topic, salient entities, what the user's
        // references point at, and what kind of turn this is — plus the reference resolution
        // extraction depends on. Deterministic, ephemeral, and traced in full.
        //
        // Its decisions are RETURNED and appended here, at the point the turn always added
        // them, so the recorded decision sequence is byte-identical.
        var understanding = Turns.Understanding.TurnUnderstanding.Read(
            recent, promptText, projectContext.ResolvedProjectName,
            identityProjection.UserName, identityProjection.CompanionName);
        decisions.AddRange(understanding.Decisions);

        var working = understanding.Working;
        var retrievalQuery = understanding.RetrievalQuery;
        var interpretationNote = understanding.InterpretationNote;
        var extractionResolution = understanding.ExtractionResolution;

        // 4. Retrieve relevant memories, boosted by the resolved project â€” searching what the
        // message MEANS (question + answer, reference + referent), not just what it says.
        var retrieved = await _context.RetrieveAsync(
            userId, retrievalQuery, projectContext.ResolvedProjectName, ct);
        var outcome = retrieved.Outcome;
        var selectedMemories = retrieved.Selected;

        // 4a. Turn intent, in SHADOW. Part of understanding, and it runs here rather than
        // above because the classification counts what retrieval selected — an existing data
        // dependency the extraction follows instead of hiding.
        var (intent, intentDecision) = Turns.Understanding.TurnUnderstanding.ClassifyIntent(
            working, promptText, outcome.Selected.Count);
        decisions.Add(intentDecision);
        understanding = understanding with { Intent = intent };

        var focal = RelevanceSignals.Focal(promptText, outcome.Selected);

        // The one controlled promotion (language-organ Phase 2): when the flag is on and the
        // system selected clarify â€” which the classifier only does for a QUESTION hanging on
        // guess-level ambiguity â€” the packet's authoritative interpretation section carries
        // one instruction preferring a short clarifying question over guessing. Narrowest
        // possible authority: one intent, one condition, one line, its own flag, off by
        // default, measured by the canonical soak stage. Nothing else is promoted.
        // 4b. The epistemic question (Phase 3): "do you know what X is?" is answered by the
        // SYSTEM from her concept store, never silently by the model's pretraining. The
        // lookup and its verdict are always recorded; the authoritative packet line rides
        // behind its own promotion flag, same discipline as clarify.
        ConceptLookupResult? knowledge = null;
        if (await _context.LookupKnowledgeAsync(userId, promptText, ct) is var (looked, askedTerm))
        {
            knowledge = looked;
            decisions.Add(new DecisionRecord
            {
                Stage = "knowledge.lookup", Decider = "rule",
                Verdict = $"{ConceptKnowledge.Canonical(askedTerm)}:{knowledge.Familiarity.ToKebab()}",
                Reason = knowledge.Definition,
            });
            if (_options.PromoteKnowledgeBoundary && interpretationNote is null)
            {
                interpretationNote = knowledge.Familiarity == ConceptFamiliarity.Known
                    ? Prompts.Format("knowledge.known",
                        ("term", knowledge.Term), ("definition", knowledge.Definition ?? ""),
                        ("source", identityProjection.UserName ?? "the user"),
                        ("date", $"{knowledge.LearnedAt:MMM d}"))
                    : Prompts.Format("knowledge.unknown", ("term", askedTerm));
                decisions.Add(new DecisionRecord
                {
                    Stage = "knowledge.promotion", Decider = "config",
                    Verdict = knowledge.Familiarity == ConceptFamiliarity.Known
                        ? "known-injected" : "unknown-injected",
                });
            }
        }

        if (_options.PromoteClarifyIntent && intent.Intent == TurnIntent.Clarify
            && interpretationNote is null)
        {
            interpretationNote = Prompts.Format("intent.clarify",
                ("marker", working.ReferenceMarkers.FirstOrDefault() ?? "their reference"));
            decisions.Add(new DecisionRecord
            {
                Stage = "intent.promotion", Decider = "config",
                Verdict = "clarify-injected",
                Reason = "PromoteClarifyIntent is on; question turn with unresolvable ambiguity",
            });
        }

        // When the query was rewritten and capture is on, also retrieve with the RAW message
        // and trace both result sets â€” the before/after evidence for whether resolution
        // actually changes what reaches the prompt. Costs one extra embedding on rewritten
        // turns only, and only while measuring.
        IReadOnlyList<string> rawQueryRetrieved = Array.Empty<string>();
        if (retrievalQuery != working.RawQuery && _shadow.IsRecording)
        {
            rawQueryRetrieved = await _context.RetrieveWithRawQueryAsync(
                userId, working.RawQuery, projectContext.ResolvedProjectName, ct);
        }

        // 4b. Relational/emotional layer: read this message's tone and append it to the signal log
        // (gated by privacy), then derive how things have been feeling so the reply can attune its
        // tone. The snapshot includes this turn, so it reflects the user's mood right now.
        if (extractFacts)
        {
            await CaptureMoodAsync(userId, extractionSource, projectContext, now, ct);

            // A dated plan in the user's words ("interview on Thursday") becomes an anticipation:
            // encouragement on the day, a follow-up after â€” the caring-at-the-right-moment layer.
            await CaptureAnticipationAsync(userId, extractionSource, ct);
        }
        // 4c-4e. The remaining contextual ingredients (Turns/Context/TurnContext): the
        // relationship snapshot, a musing that colors this turn, at most one held curiosity,
        // her own state and familiarity, and the relevant tastes, attention items, procedures,
        // capabilities and shared perspectives. Runs HERE, after the mood capture above, so
        // her state reflects the signal this message just left.
        var prepared = await _context.PrepareAsync(
            userId, promptText, now, outcome.QueryEmbedding, selectedMemories,
            identityProjection, ct);
        decisions.AddRange(prepared.Decisions);

        // These five are read by several later stages (the plan contributors, the trace, the
        // curiosity mark-voiced); the rest are consumed once, by assembly, and are read
        // straight off the result.
        var relationship = prepared.Relationship;
        var musing = prepared.Musing;
        var curiosity = prepared.Curiosity;
        var innerState = prepared.InnerState;
        var familiarity = prepared.Familiarity;

        // Temporal grounding stays here: it is a pure clock read rather than anything
        // gathered, and moving it would have meant moving a public helper the tests name.
        var temporal = TemporalNote(_clock.GetLocalNow(), now, lastSeenBefore);

        // 5. Assemble a bounded, labeled context packet (with the user's persona/style + tone read).
        var packet = _assembler.Assemble(
            promptText, recent, selectedMemories, projectContext, persona, relationship,
            musing, curiosity?.Question, innerState.Describe(), familiarity.Describe(),
            temporal, prepared.PreferenceNotes, identityProjection, prepared.AttentionNotes,
            prepared.ProcedureNotes, prepared.CapabilityNote, prepared.PerspectiveNotes,
            interpretationNote);

        // 5b. The bounded tool loop, driven by the executive planner. It gets a COMPACT planning
        // context â€” recent exchange, what retrieval already found, the detected project â€” never
        // the full persona packet: planning is an information-gap question, not a conversation,
        // and the planner model is deliberately not her. Everything executed is read-only,
        // validated, deduped, and capped; results are injected into the packet for THIS
        // generation only â€” they never become messages or memory.
        if (_options.PacketTokenWarningThreshold > 0
            && packet.EstimatedTokens > _options.PacketTokenWarningThreshold)
        {
            _logger.LogWarning(
                "Context packet for {UserId} is ~{Tokens} tokens (threshold {Threshold}); " +
                "sections: {Sections}. A bloated prompt degrades small local models quietly.",
                userId, packet.EstimatedTokens, _options.PacketTokenWarningThreshold,
                string.Join(", ", PresentSections(packet)));
        }

        // Something had to be left out to stay inside the model's window. Said plainly, because
        // the alternative to saying it is the failure this whole mechanism exists to prevent:
        // context disappearing with nothing anywhere to show that it did.
        if (packet.TrimmedSections.Count > 0)
        {
            _logger.LogWarning(
                "Prompt for {UserId} exceeded its {Budget}-token budget; left out (lowest value " +
                "first): {Dropped}. Identity and the standing rules are never among these â€” if " +
                "this is routine, the chat model needs a larger context window.",
                userId, _options.PromptTokenBudget, string.Join(", ", packet.TrimmedSections));
        }

        decisions.Add(new DecisionRecord
        {
            Stage = "register", Decider = "rule",
            Verdict = packet.RegisterNote is null ? "unconstrained" : "advised",
            Reason = packet.RegisterNote,
        });
        decisions.Add(new DecisionRecord
        {
            Stage = "packet.budget", Decider = "rule",
            Verdict = packet.TrimmedSections.Count == 0 ? "fit" : "trimmed",
            Reason = packet.TrimmedSections.Count == 0
                ? null : string.Join(", ", packet.TrimmedSections),
        });

        // 5a. The response plan (Phase 5), SHADOW: what Ava has DECIDED this turn — act,
        // acknowledgments with error ownership, content authority levels, epistemic
        // constraints, the question if any. Computed entirely from state already decided
        // above, recorded beside the turn, and NOT rendered: the generation packet is
        // byte-identical with or without it. Fidelity of real replies to the plan is
        // measured before the plan is ever given authority (docs/RESPONSE_PLAN.md).
        var planned = _planning.BuildProductionPlan(
            traceId, intent, working, promptText, selectedMemories, knowledge,
            curiosity?.Question, packet.RegisterNote, packet.MoodNote, persona);
        var plan = planned.Plan;
        decisions.Add(planned.Decision);

        // 5b. The narrowest plan promotion: correction acknowledgments ONLY, and only
        // when the conflict check proved the error is hers (an agreement-shaped turn
        // never reaches this — it planned AgreementConfirmed instead). One authoritative
        // line; style stays free.
        if (_options.PromoteResponsePlan
            && plan.Acknowledgments.Any(a => a is { Kind: AckKind.CorrectionAccepted, ErrorOwner: ErrorOwner.Companion }))
        {
            var owned = Prompts.Get("plan.correction-owned");
            packet = packet with
            {
                InterpretationNote = packet.InterpretationNote is { } existing
                    ? $"{existing}\n{owned}" : owned,
            };
            decisions.Add(new DecisionRecord
            {
                Stage = "plan.promotion", Decider = "config",
                Verdict = "correction-owned-injected",
                Reason = "PromoteResponsePlan is on; conflict-verified companion-owned correction",
            });
        }

        // P4 (docs/RESPONSE_PLAN_V3_SPEC.md §15): the NATIVE v3 plan, built from the same
        // upstream state as the v2 plan — never FROM it. Shadow evidence only: a failed
        // build records a content-safe diagnostic and the turn continues unchanged.
        PlanV3.PlanV3? nativeV3 = null;
        string? nativeBuildError = null;
        IReadOnlyList<string> nativeLintRejections = [];
        PlanV3.AssemblyReport? nativeAssembly = null;
        int? nativeCompactV4Chars = null;
        if (_rendererShadow.IsObserving || _rendererShadow.IsCanaryFor(userId))
        {
            var native = _planning.BuildNativePlan(
                traceId, intent, working, promptText, selectedMemories, knowledge,
                curiosity?.Question, sensitive, userId, identityProjection?.CompanionName);
            nativeV3 = native.Plan;
            nativeBuildError = native.BuildError;
            nativeLintRejections = native.LintRejections;
            decisions.AddRange(native.Decisions);
        }

        var (toolOutcome, toolDecision) = await _execution.RunToolsAsync(
            userId, recent, selectedMemories, projectContext.ResolvedProjectName,
            promptText, traceId, ct);
        if (toolOutcome.ResultsSection is not null)
            packet = packet with { ToolResults = toolOutcome.ResultsSection };
        decisions.Add(toolDecision);

        // plan/4 (docs/PLAN_V4_FICTION_FRAME.md): the fiction frame.
        //
        // R-02: this is COGNITION, not observation, and it runs unconditionally. It used to
        // sit inside the renderer-shadow gate below, which meant switching observation off
        // silently stopped the frame lifecycle from advancing and from persisting — an
        // observability flag deciding whether durable conversation state moved. Ava would
        // forget she was in a scene because nobody was watching her be in it.
        //
        // The lifecycle owns frame truth; FrameRequestReader supplies the typed request and
        // InCharacterDetector contributes only a hint that, alone, does nothing. Content
        // never activates, restricts or exits a frame. What the shadow path may do with the
        // result is read it — the Frame object below rides the native plan when one is being
        // built, and is simply unused when one is not.
        global::Companion.PlanV3.Frame? nativeFrame = null;
        if (_frames is not null)
        {
            try
            {
                var request = FrameRequestReader.Read(promptText);
                if (request == FrameLifecycle.Request.None && inCharacter)
                    request = FrameLifecycle.Request.DetectedInCharacter;

                var active = await _frames.GetActiveAsync(userId, conversationId, ct);
                var decision = FrameLifecycle.Decide(request, active is not null);

                if (decision.Transition is { } transition)
                {
                    var write = await _frames.ApplyAsync(new FrameTransitionRequest
                    {
                        UserId = userId,
                        ConversationId = conversationId,
                        Transition = global::Companion.PlanV3.PlanV4Codec.Kebab(transition),
                        Cause = decision.Cause,
                        At = now,
                        SceneRef = active?.SceneRef,
                        // R-01: the EVENT, never the words — and not even the event on a
                        // privacy-sensitive turn. The frame still advances (the lifecycle
                        // is not privacy-conditional); what it declines to record is any
                        // handle back to what was said. Ava can still tell you she is in
                        // a scene and when it started; she cannot reproduce the sentence
                        // that started it, and neither can a training export.
                        EvidenceMessageId = sensitive ? null : extractionSource.Id,
                    }, traceId.ToString(), ct);

                    var session = write.Session ?? active;
                    if (session is not null)
                        nativeFrame = Turns.Planning.TurnPlanning.BuildFrame(transition, session);
                }

                decisions.Add(new DecisionRecord
                {
                    Stage = "plan.frame", Decider = "rule",
                    Verdict = decision.Transition is { } t
                        ? global::Companion.PlanV3.PlanV4Codec.Kebab(t) : "none",
                    Reason = decision.Cause,
                });
            }
            catch (Exception ex)
            {
                // Frame truth failing must not cost the turn. It is recorded as a decision so
                // a lost transition is answerable rather than invisible.
                decisions.Add(new DecisionRecord
                {
                    Stage = "plan.frame", Decider = "rule", Verdict = "failed",
                    Reason = $"{ex.GetType().Name}",
                });
                _logger.LogWarning(ex,
                    "Frame lifecycle failed for {TraceId}; the turn continues unframed.", traceId);
            }
        }

        // Source 2 (docs/SOURCE2_TOOL_PLAN.md): fold the turn's TYPED tool outcomes into the
        // native V3 plan through the contribution boundary. The inputs are the typed results
        // captured at execution time — never `ResultsSection`, never the prose the production
        // prompt received. The assembler alone grants authority: a refused, secret-bearing, or
        // unexecuted call contributes nothing, a failure can only be acknowledged, and nothing
        // reaches must_express without a planner disposition. Shadow evidence only: the
        // production packet, the reply, and run-1c are untouched either way.
        // Source 2/3/4 contribution and the frame's ride on the native plan
        // (Turns/Planning/TurnPlanning). The assembler alone grants authority. Shadow
        // evidence only: the production packet, the reply, and run-1c are untouched.
        if (nativeV3 is not null)
        {
            var contributed = await _planning.ContributeAsync(
                new Turns.Planning.NativePlanResult
                {
                    Plan = nativeV3,
                    BuildError = nativeBuildError,
                    LintRejections = nativeLintRejections,
                },
                traceId, userId, promptText, sensitive, plan, toolOutcome, working,
                innerState, familiarity, conversationId, nativeFrame, ct);

            nativeV3 = contributed.Plan;
            nativeAssembly = contributed.Assembly;
            nativeBuildError = contributed.BuildError;
            nativeLintRejections = contributed.LintRejections;

            // Serializing the plan to measure its size is not planning, so the probe stays
            // here. It is RECORDED, never sent.
            if (nativeFrame is not null && nativeV3 is not null)
            {
                try
                {
                    nativeCompactV4Chars = PlanV3.PlanV4Codec.CompactV4(nativeV3).Length;
                }
                catch (Exception ex)
                {
                    nativeBuildError ??= $"compactv4: {ex.GetType().Name}";
                }
            }

            decisions.AddRange(contributed.Decisions);
        }

        // The user-scoped renderer canary (docs/RENDERER_SHADOW.md §8): on this user's
        // eligible non-tool turns, the tuned renderer's reply is DISPLAYED and production is
        // the immediate fallback. Decided before generation so streaming can be handled: the
        // production tokens are not forwarded on a canary turn (the displayed reply may
        // differ), and the chosen reply is reported to the sink once, whole, at the end.
        // CAPABILITY ROUTING, not content blocking (2026-08-25). Run-1c's corpus contains no
        // roleplay: rendering an in-character turn through it produced fabricated dialogue and
        // control-block echo when measured. So a declared or detected in-character turn stays
        // on production, which is the model proven to handle the request. This is the same
        // reasoning as skipping tool turns — route the request to the renderer that can serve
        // it — and it restricts no subject matter: the production model answers it in full.
        // 6. Execution (Turns/Execution/TurnExecution): render the prompt, call production,
        // run the canary if this turn is eligible, and apply the reply gate. It selects the
        // displayed reply and persists nothing — every write below is still the turn's.
        var executed = await _execution.ExecuteAsync(new Turns.Execution.TurnExecutionRequest
        {
            TraceId = traceId,
            UserId = userId,
            ConversationId = conversationId,
            SourceMessageId = extractionSource.Id,
            PromptText = promptText,
            Packet = packet,
            Recent = recent,
            Plan = plan,
            ToolOutcome = toolOutcome,
            InCharacter = inCharacter,
            Sensitive = sensitive,
            CompanionName = identityProjection?.CompanionName,
            NativeV3 = nativeV3,
            NativeBuildError = nativeBuildError,
            NativeLintRejections = nativeLintRejections,
            NativeAssembly = nativeAssembly,
            NativeCompactV4Chars = nativeCompactV4Chars,
            NativeFrameTransition = nativeFrame is null ? null
                : PlanV3.PlanV4Codec.Kebab(nativeFrame.Transition),
            TokenSink = tokenSink,
        }, ct);
        decisions.AddRange(executed.Decisions);

        var canaryTurn = executed.CanaryTurn;
        var renderedPrompt = executed.RenderedPrompt;
        var generated = executed.Generation;
        var response = executed.Displayed;

        // The gate's comparison row is written HERE: execution decided, observability records.
        if (executed.Refusal is { } refusal)
        {
            await _shadow.RecordAsync(new ShadowComparison
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                SourceMessageId = extractionSource.Id,
                ConversationId = conversationId,
                Subject = "safety.gate",
                Legacy = "allow",
                Model = "block",
                Confidence = 1.0,
                Agreed = false,
                Applied = refusal.Enforced ? "model" : "legacy",
                Input = refusal.Reason,
            }, ct);
        }

        // 6c. Plan fidelity, SHADOW: the deterministic checks of what the model actually
        // said against what the plan required — the measurable half of the renderer
        // contract, running long before the plan has any authority. Violations are
        // decisions AND capture rows; changing the reply is not on the table here.
        foreach (var (check, violation) in new (string, string?)[]
        {
            ("correction-ownership", PlanFidelity.CheckCorrectionOwnership(plan, response)),
            ("invented-contrition", PlanFidelity.CheckInventedContrition(plan, response)),
            ("shared-history", PlanFidelity.CheckSharedHistoryClaim(plan, response)),
            ("epistemic", PlanFidelity.CheckEpistemic(plan, response)),
        })
        {
            if (violation is null)
                continue;
            decisions.Add(new DecisionRecord
            {
                Stage = "plan.fidelity", Decider = "rule",
                Verdict = $"violated:{check}",
                Reason = violation,
            });
            if (_shadow.IsRecording)
            {
                await _shadow.RecordAsync(new ShadowComparison
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    SourceMessageId = extractionSource.Id,
                    ConversationId = conversationId,
                    Subject = "plan.fidelity",
                    Legacy = $"violated:{check}",
                    Model = null,
                    Applied = "legacy",
                    Input = violation,
                }, ct);
            }
        }

        // 6d. Renderer shadow (docs/RENDERER_SHADOW.md): the tuned renderer renders the same
        // plan beside the reply that just went out. Fire-and-forget on an immutable snapshot —
        // by construction it cannot touch conversation state, memory, goals, tools, or what the
        // user saw. Eligibility mirrors what the renderer corpus covers: ordinary answered chat
        // turns, no tool results (never trained on), and never a privacy-sensitive turn (the
        // shadow row stores real text, so the strictest existing boundary applies). A canary
        // turn already rendered and recorded synchronously — observing it again would double
        // both the GPU work and the row.
        if (!canaryTurn && _rendererShadow.IsObserving)
        {
            // A tool turn is still ineligible for a renderer COMPARISON — run-1c never
            // trained on tool results, so scoring it there measures the corpus's absence
            // rather than the renderer. Its structural V3 evidence is what Source 2 needs,
            // so it takes the plan-only path: the row is written, the renderer never runs.
            var eligible = !sensitive && toolOutcome.Calls.Count == 0 && !inCharacter;
            var planOnly = !sensitive && !eligible;
            decisions.Add(new DecisionRecord
            {
                Stage = "renderer.shadow", Decider = "config",
                Verdict = eligible ? "observed" : planOnly ? "plan-only" : "skipped",
                Reason = eligible ? null
                    : sensitive ? "privacy-sensitive turn"
                    : inCharacter ? "in-character turn: run-1c has no roleplay capability"
                    : "turn used tools",
            });
            if (eligible || planOnly)
            {
                var observation = new RendererShadowObservation
                {
                    TraceId = traceId,
                    UserId = userId,
                    SourceMessageId = extractionSource.Id,
                    ConversationId = conversationId,
                    Plan = plan,
                    Transcript = recent
                        .TakeLast(4)
                        .Select(m => (m.Role == MessageRole.User ? "user" : "assistant", m.Content))
                        .ToList(),
                    UserMessage = promptText,
                    ProductionResponse = response,
                    NativeV3 = nativeV3,
                    NativeBuildError = nativeBuildError,
                    NativeLintRejections = nativeLintRejections,
                    NativeAssembly = nativeAssembly,
                    NativeCompactV4Chars = nativeCompactV4Chars,
                    NativeFrameTransition = nativeFrame is null ? null
                        : global::Companion.PlanV3.PlanV4Codec.Kebab(nativeFrame.Transition),
                };
                if (eligible)
                    _rendererShadow.Observe(observation);
                else
                    _rendererShadow.ObservePlanOnly(observation);
            }
        }

        // 7. Store the response, with the generation metadata (why it stopped, rounds, tokens) so a
        // reply is answerable after the fact instead of a mystery.
        var assistantMsg = await StoreMessageAsync(
            userId, conversationId, MessageRole.Assistant, response, replyToId, _clock.GetUtcNow(), ct, generated);

        // 8â€“10. Derived-state work: extraction, project/open-loop updates, attention, procedures,
        // commitments. The reply is already generated and STORED, so a failure here (extraction
        // model down, embedding server gone, a malformed candidate) must not turn a delivered
        // answer into an error â€” the user would see a 500 for a message the companion actually
        // answered, and the stored exchange would be orphaned mid-turn. Losing a turn's derived
        // memory is recoverable; losing the turn is not. Cancellation still propagates: that is
        // the caller leaving, not a failure.
        var exchange = new[] { extractionSource, assistantMsg };
        var extraction = MemoryExtractionResult.Empty;
        var updates = ProjectUpdateResult.Empty;
        try
        {
            // (Skipped for private AND in-character turns â€” fiction never reaches the fact store.)
            if (extractFacts)
            {
                extraction = await _pipeline.ProcessAsync(userId, exchange, extractionResolution, ct);
                updates = await _projectUpdater.ApplyAsync(userId, exchange, extraction, projectContext, ct);

                // A commitment the companion just made ("I'll check in tomorrow") becomes a
                // companion-owned open loop, so it can follow up next session instead of
                // forgetting it said so. Deduped against existing open commitments.
                await _attention.CaptureTurnAsync(userId, extractionSource, remember: true, ct);
                await _procedures.ApplyRevisionAsync(userId, extractionSource, now, ct);
                await _procedures.AddOrUpdateFromTeachingAsync(userId, conversationId, extractionSource, now, ct);
                await CaptureCommitmentAsync(userId, response, assistantMsg.Id, now, ct);

                // Explicit teaching becomes Ava-owned world knowledge — user message only,
                // high-precision detector, evidence-bound (docs/CONCEPT_KNOWLEDGE.md). Inside
                // this gate deliberately: a turn not allowed durable derived memory is not
                // allowed durable knowledge either.
                string? taught = null;
                if (_concepts is not null)
                {
                    taught = await _concepts.LearnFromAsync(userId, extractionSource, lexicon, ct);
                    if (taught is not null)
                    {
                        decisions.Add(new DecisionRecord
                        {
                            Stage = "knowledge.taught", Decider = "rule",
                            Verdict = ConceptKnowledge.Canonical(taught),
                        });
                    }
                    // Every loose-copular sentence the detector rejected is a labeled
                    // negative for the future corpus — broadening happens on data, never
                    // on intuition.
                    if (TeachingDetector.LooseShape(extractionSource.Content))
                    {
                        await Shadow.CaptureAsync(
                            _shadow, "knowledge.teaching", taught is not null,
                            extractionSource.Content, ct,
                            userId, extractionSource.Id, conversationId);
                    }
                }

                // Knowledge gaps (Phase 4): observable epistemic events become typed,
                // deduped, provenance-bearing gap rows. Recording is NOT a promise to ask —
                // promotion is a separate, capped decision in the reflection cadence.
                // Inside this gate deliberately: gaps are durable derived state.
                if (_gaps is not null)
                {
                    async Task ObserveGapAsync(GapKind kind, string subject, GapSource source)
                    {
                        // sourceRef stays the trace id (diagnostic provenance); the message
                        // id is what /forget matches on, and a gap accumulates many of them.
                        var (gap, _) = await _gaps.ObserveAsync(
                            userId, kind, subject, source, traceId, now, extractionSource.Id, ct);
                        decisions.Add(new DecisionRecord
                        {
                            Stage = "gap.observed", Decider = "rule",
                            Verdict = $"{kind.ToKebab()}:{subject}",
                            Reason = $"seen {gap.Occurrences}x ({gap.Status.ToKebab()})",
                        });
                    }

                    if (knowledge is not null)
                    {
                        var subject = ConceptKnowledge.Canonical(knowledge.Term);
                        if (knowledge.Familiarity == ConceptFamiliarity.Unknown)
                            await ObserveGapAsync(GapKind.UnknownConcept, subject, GapSource.KnowledgeLookup);
                        else if (knowledge.Familiarity is ConceptFamiliarity.Learning or ConceptFamiliarity.Disputed)
                            await ObserveGapAsync(GapKind.UncertainKnowledge, subject, GapSource.KnowledgeLookup);
                    }

                    // An unpinned reference: recorded, never promoted in v1 (it ages badly).
                    if (working is { ReferenceMarkers.Count: > 0 }
                        && (working.ResolvedReference is null
                            || working.ResolutionConfidence == ResolutionConfidence.Guess))
                    {
                        await ObserveGapAsync(GapKind.UnresolvedReference,
                            working.ReferenceMarkers[0].ToLowerInvariant(), GapSource.WorkingContext);
                    }

                    // Conflicting evidence the pipeline parked for review.
                    foreach (var parked in extraction.Decisions
                                 .Where(d => d.Outcome == MemoryDecisionKind.NeedsReview).Take(2))
                    {
                        await ObserveGapAsync(GapKind.ConflictingEvidence,
                            $"{parked.Candidate.Subject}/{parked.Candidate.Predicate}".ToLowerInvariant(),
                            GapSource.MemoryReview);
                    }

                    // Teaching satisfies: the loop closes with provenance, and the linked
                    // curiosity closes with it.
                    if (taught is not null)
                    {
                        var satisfied = await _gaps.SatisfyBySubjectAsync(
                            userId, ConceptKnowledge.Canonical(taught),
                            $"learned from teaching on {now:MMM d}", ct);
                        if (satisfied > 0)
                        {
                            decisions.Add(new DecisionRecord
                            {
                                Stage = "gap.satisfied", Decider = "rule",
                                Verdict = ConceptKnowledge.Canonical(taught),
                                Reason = $"{satisfied} gap(s) closed by teaching",
                            });
                        }
                    }
                }

                // Corpus capture, last and deliberately inside this gate: a turn that is not
                // allowed to produce durable memory is not allowed to produce durable training
                // data either. Off unless CognitiveModels:Capture is set, and it changes nothing
                // it observes â€” see ICognitiveCapture.
                await _capture.CaptureUserMessageAsync(
                    extractionSource.Content, ct, userId, extractionSource.Id, conversationId);
                await _capture.CaptureReplyAsync(
                    response, ct, userId, extractionSource.Id, conversationId);

                // Same discipline for the working-context rules: record what they decided on
                // the populations they decide about â€” that is the base rate every precision
                // claim depends on, and it has never been measured (the ToolNudge lesson).
                // Capture-only; changes nothing it observes.
                if (AnswerBindingDetector.TrailingQuestion(recent) is { } openQuestion)
                {
                    await Shadow.CaptureAsync(
                        _shadow, "context.binding",
                        working.BoundQuestion is not null,
                        $"{openQuestion} ||| {extractionSource.Content}", ct,
                        userId, extractionSource.Id, conversationId);
                }
                if (_shadow.IsRecording)
                {
                    // Every turn's intent verdict, with the working-context move as input
                    // context â€” the corpus that decides whether this vocabulary ever earns
                    // authority over generation.
                    // The input tag carries the evidence the vocabulary decisions need: the
                    // working-context move, the top RAW topical relevance this turn (for the
                    // admit-unknown signal characterization), and whether the message had
                    // imperative shape (for the request/directive vocabulary question).
                    var topTopical = outcome.Selected.Count == 0 ? 0.0 : outcome.Selected.Max(r => r.Topical);
                    await _shadow.RecordAsync(new ShadowComparison
                    {
                        Id = Guid.NewGuid(),
                        UserId = userId,
                        SourceMessageId = extractionSource.Id,
                        ConversationId = conversationId,
                        Subject = "turn.intent",
                        Legacy = $"{intent.Intent.ToKebab()} ({intent.Confidence:F2})"
                            + (intent.Candidates.Count > 1
                                ? $" over {intent.Candidates[1].Intent.ToKebab()} ({intent.Candidates[1].Confidence:F2})" : ""),
                        Model = null,
                        Applied = "legacy",
                        Input = SecretDetector.LooksLikeSecret(extractionSource.Content)
                            ? null
                            : $"[{working.Move.ToKebab()}|topical={topTopical:F2}" +
                              $"{(TurnIntentClassifier.LooksDirective(extractionSource.Content) ? "|directive" : "")}" +
                              $"{(focal is null ? "" : focal.Covered ? "|focal=covered" : "|focal=uncovered")}] " +
                              extractionSource.Content,
                    }, ct);
                }
                if (working.ReferenceMarkers.Count > 0 && _shadow.IsRecording)
                {
                    await _shadow.RecordAsync(new ShadowComparison
                    {
                        Id = Guid.NewGuid(),
                        UserId = userId,
                        SourceMessageId = extractionSource.Id,
                        ConversationId = conversationId,
                        Subject = "context.reference",
                        Legacy = $"{working.Move.ToKebab()}: {working.ReferenceMarkers.First()}"
                            + (working.ResolvedReference is null ? " (unresolved)"
                                : $" -> {working.ResolvedReference}"),
                        Model = null,
                        Applied = "legacy",
                        Input = SecretDetector.LooksLikeSecret(extractionSource.Content)
                            ? null : extractionSource.Content,
                    }, ct);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "Derived-state work failed for {UserId} after the reply was stored; " +
                "the turn stands, this turn's derived memory is lost.", userId);
        }

        // 10c. The offered curiosity is spent whether or not the model chose to raise it â€” asked
        // once (or passed over once) is the whole budget, so proactive wondering never nags.
        if (curiosity is not null)
            await _reflections.MarkVoicedAsync(userId, curiosity.Id, now, ct);

        if (extractFacts)
        {
            decisions.Add(new DecisionRecord
            {
                Stage = "extraction", Decider = "model",
                Verdict = $"accepted={extraction.Accepted} merged={extraction.Merged} " +
                          $"review={extraction.NeedsReview} rejected={extraction.Rejected}",
            });
        }

        // 11. Record the trace for debugging (`/why`).
        _logger.LogInformation(
            "Turn complete for {UserId}: {Selected} memories, project={Project}, " +
            "reply finish={Finish}/rounds={Rounds}, " +
            "extraction {Accepted}A/{Merged}M/{Review}R/{Rejected}X, {Actions} project updates",
            userId, outcome.Selected.Count, projectContext.ResolvedProjectName ?? "(none)",
            generated.FinishReason ?? "(none)", generated.Rounds,
            extraction.Accepted, extraction.Merged, extraction.NeedsReview, extraction.Rejected,
            updates.Actions.Count);

        // The operational record for "why did you say that?" â€” powers diagnostics.last_turn.
        _turnLog.Record(userId, new TurnDiagnostics
        {
            TraceId = traceId,
            At = now,
            UserMessagePreview = promptText.Length <= 80 ? promptText : promptText[..80],
            PromptChars = renderedPrompt.Length,
            // Full text only when explicitly switched on; the preview above is always safe.
            PromptSystem = _options.CapturePromptText ? renderedPrompt : null,
            PromptUser = _options.CapturePromptText ? promptText : null,
            MemoriesRetrieved = selectedMemories.Count,
            RetrievedSummaries = selectedMemories.Take(5)
                .Select(r => (r.Memory.Content.Length <= 120 ? r.Memory.Content : r.Memory.Content[..120])
                    + $" (score {r.Score:F2})").ToList(),
            Retrieved = selectedMemories.Take(5)
                .Select(r => new RetrievedMemoryTrace
                {
                    Content = r.Memory.Content.Length <= 120 ? r.Memory.Content : r.Memory.Content[..120],
                    Score = r.Score,
                    Topical = r.Topical,
                    Source = r.Source == RetrievalSource.Associative ? "associative" : "retrieval",
                }).ToList(),
            Decisions = decisions,
            WorkingContext = working,
            Intent = intent,
            Plan = plan,
            Focal = focal,
            RetrievedWithRawQuery = rawQueryRetrieved,
            ContextSections = PresentSections(packet),
            DetectedProject = projectContext.ResolvedProjectName,
            InCharacterTurn = inCharacter,
            PrivateConversation = !remember,
            FinishReason = generated.FinishReason,
            GenerationRounds = generated.Rounds,
            ModelUsed = generated.Model,
            AdvertisedTools = toolOutcome.AdvertisedTools,
            ToolCalls = toolOutcome.Calls,
            ToolDecisions = toolOutcome.Decisions,
            PlanningRounds = toolOutcome.PlanningRounds,
            PacketTokens = packet.EstimatedTokens,
        });

        // The DURABLE twin of the ring entry (the ring forgets on restart, which is how the
        // Epcot specimen's trace was lost). Content fields are nulled on turns not allowed to
        // produce durable derived data â€” structure survives, words do not â€” and previews of
        // ordinary turns mirror text the Messages table already stores. The store owns the
        // never-throw guarantee.
        if (_diagnostics is not null)
        {
            string? Bounded(string? text, int max) =>
                !extractFacts || text is null ? null
                : SecretDetector.LooksLikeSecret(text) ? null
                : text.Length <= max ? text : text[..max];

            await _diagnostics.RecordTurnAsync(new TurnRecord
            {
                Id = traceId,
                UserId = userId,
                Timestamp = now,
                // A1 lineage: every preview below is derived from this message, so forgetting
                // it must reach them.
                SourceMessageId = extractionSource.Id,
                UserPreview = Bounded(promptText, 300),
                AssistantPreview = Bounded(response, 300),
                Move = working.Move.ToKebab(),
                ResolvedReference = Bounded(working.ResolvedReference, 200),
                ResolutionConfidence = working.ResolutionConfidence?.ToKebab(),
                BoundQuestion = Bounded(working.BoundQuestion, 300),
                RetrievalQuery = Bounded(retrievalQuery, 500),
                Intent = intent.Intent.ToKebab(),
                IntentConfidence = intent.Confidence,
                IntentRunnerUp = intent.Candidates.Count > 1
                    ? $"{intent.Candidates[1].Intent.ToKebab()} ({intent.Candidates[1].Confidence:F2})" : null,
                Retrieved = !extractFacts ? null : System.Text.Json.JsonSerializer.Serialize(
                    selectedMemories.Take(5).Select(r => new
                    {
                        c = r.Memory.Content.Length <= 90 ? r.Memory.Content : r.Memory.Content[..90],
                        s = Math.Round(r.Score, 2),
                        t = Math.Round(r.Topical, 2),
                    })),
                FocalTerms = !extractFacts || focal is null ? null : string.Join(",", focal.FocalTerms),
                FocalCovered = focal?.Covered,
                Decisions = string.Join("; ", decisions.Select(d => $"{d.Stage}={d.Verdict}")),
                Plan = !extractFacts ? null
                    : System.Text.Json.JsonSerializer.Serialize(plan, PlanJson) is var planJson
                        && planJson.Length <= 2500 ? planJson : null,
                PacketTokens = packet.EstimatedTokens,
                ModelUsed = generated.Model,
            }, ct);
        }

        return new TurnTrace
        {
            TraceId = traceId,
            UserMessage = promptText,
            Status = status,
            PendingClarificationId = pendingId,
            DetectedProject = projectContext.ResolvedProjectName,
            Retrieved = outcome.Selected,
            Excluded = outcome.Excluded,
            Packet = packet,
            Response = response,
            Extraction = extraction,
            ProjectContext = projectContext,
            ProjectUpdates = updates,
            AdvertisedTools = toolOutcome.AdvertisedTools,
            ToolCalls = toolOutcome.Calls,
        };
    }

    /// <summary>
    /// The planner's bounded view of the turn: enough to judge information gaps ("does the
    /// context already cover this?"), nothing more. Deliberately NOT the full packet â€” no
    /// persona, no mood, no relationship framing; the planner is an executive, not her.
    /// </summary>

    /// <summary>Which packet sections were actually present â€” diagnostics, not content.</summary>
    private static IReadOnlyList<string> PresentSections(ContextPacket packet)
    {
        var sections = new List<string>();
        void If(bool present, string name) { if (present) sections.Add(name); }
        If(!string.IsNullOrWhiteSpace(packet.Persona), "persona");
        If(!string.IsNullOrWhiteSpace(packet.MoodNote), "mood");
        If(!string.IsNullOrWhiteSpace(packet.RegisterNote), "register");
        If(!string.IsNullOrWhiteSpace(packet.InterpretationNote), "interpretation");
        If(!string.IsNullOrWhiteSpace(packet.FamiliarityNote), "familiarity");
        If(!string.IsNullOrWhiteSpace(packet.RelationshipNote), "relationship");
        If(!string.IsNullOrWhiteSpace(packet.TemporalNote), "temporal");
        If(!string.IsNullOrWhiteSpace(packet.Musing), "musing");
        If(!string.IsNullOrWhiteSpace(packet.CuriosityQuestion), "curiosity");
        If(packet.Project is not null, "project");
        If(packet.OpenLoops.Count > 0, "openLoops");
        If(packet.Memories.Count > 0, "memories");
        If(packet.LearnedKnowledge.Count > 0, "knowledge");
        If(packet.PreferenceNotes.Count > 0, "preferences");
        If(!string.IsNullOrWhiteSpace(packet.ToolResults), "toolResults");
        return sections;
    }

    // ---- helpers ----

    /// <summary>A musing is only current for so long â€” after this it stops shaping turns by default.</summary>

    /// <summary>How far back the diary is searched for a relevant past thought.</summary>

    /// <summary>Minimum cosine for an old musing to resurface on relevance alone.</summary>

    /// <summary>Below this age a musing reads as current; older ones get an age prefix.</summary>
    private static readonly TimeSpan MusingIsRecent = TimeSpan.FromHours(36);

    /// <summary>
    /// The musing that should color THIS turn: the most relevant past thought (by similarity to
    /// the turn's query â€” an old thought resurfaces on its own when the conversation comes back
    /// to it), falling back to the freshest one while it's still current. Reading the diary is
    /// side-effect free â€” a musing can accompany many turns; it is a mood the companion carries,
    /// unlike a curiosity, which is consumed the one time it is offered.
    /// </summary>


    /// <summary>An older thought carries its age, so "I'd been thinkingâ€¦" can be timed honestly.</summary>
    /// <summary>Gaps shorter than this are just an ongoing conversation, not an absence.</summary>
    private static readonly TimeSpan MinGapToMention = TimeSpan.FromMinutes(5);

    /// <summary>
    /// One compact line of temporal grounding: the day and time, and how long the user was
    /// actually gone (measured before this turn's message landed). The model turns this into
    /// "back already?" or "look who finally showed up" on its own â€” nothing is scripted.
    /// </summary>
    public static string TemporalNote(DateTimeOffset localNow, DateTimeOffset utcNow, DateTimeOffset? lastSeenBefore)
    {
        var line = $"It's {localNow:dddd}, {localNow:h:mm tt}.";
        if (lastSeenBefore is null)
            return line + " This is your first conversation.";

        var gap = utcNow - lastSeenBefore.Value;
        return gap < MinGapToMention
            ? line + " You're mid-conversation."
            : line + $" You last spoke {RelativeTime.Describe(gap)} ago.";
    }

    /// <summary>How many of her own tastes may accompany one turn.</summary>

    /// <summary>Minimum similarity for a taste to be relevant to this turn at all.</summary>

    /// <summary>Her tastes that are actually relevant to what's being discussed, in natural words.</summary>


    /// <summary>
    /// Builds the plan/4 frame from the authoritative session. Reads only what the session
    /// records — no scene content, and nothing inferred from the message.
    /// </summary>






    private async Task<Message> StoreMessageAsync(
        string userId, Guid conversationId, MessageRole role, string content, Guid? replyToId,
        DateTimeOffset timestamp, CancellationToken ct, ChatCompletion? generation = null)
    {
        var message = new Message
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            UserId = userId,
            Role = role,
            Content = content,
            ReplyToId = replyToId,
            TokenCount = ContextAssembler.EstimateTokens(content),
            Timestamp = timestamp,
            // Generation metadata: only present for a model-produced reply.
            FinishReason = generation?.FinishReason,
            GenerationRounds = generation?.Rounds,
            Truncated = generation is null ? null : generation.Truncated,
            ModelUsed = generation?.Model,
            PromptTokens = generation?.PromptTokens,
            CompletionTokens = generation?.CompletionTokens,
        };
        await _conversations.AddMessageAsync(message, ct);
        return message;
    }

    /// <summary>
    /// Reads the emotional tone of the user's message and, when a real cue is present, appends it to
    /// the emotional-signal log â€” the substrate the relationship snapshot is derived from. No-op on
    /// flat/neutral messages, so the log stays signal, not noise.
    /// </summary>
    private async Task CaptureMoodAsync(
        string userId, Message userMessage, ProjectContext projectContext, DateTimeOffset now, CancellationToken ct)
    {
        var mood = MoodDetector.Detect(userMessage.Content);
        if (mood.IsNeutral)
            return;

        // Tie the feeling to what it's about: the subject phrase from the message ("the interview"),
        // or â€” failing that â€” the project this turn resolved to, so a mood voiced while discussing a
        // project still knows its subject.
        var resolvedProject = projectContext.Summary?.Project;
        var topic = MoodTopic.Extract(userMessage.Content) ?? resolvedProject?.Name;

        // Latest feeling about a topic wins: a fresh reading about the same thing closes out the
        // prior one, so an old worry the user has now spoken to again isn't surfaced separately.
        if (topic is not null)
            await _emotions.MarkTopicFollowedUpAsync(userId, topic, ct);

        // The durable forgetting handle, assigned once at capture (Phase 0) and shared with
        // the mood transition this moment produces, so /forget can reach both.
        var evidenceEventId = Guid.NewGuid();
        await _emotions.AddSignalAsync(new EmotionalSignal
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            MessageId = userMessage.Id,
            EvidenceEventId = evidenceEventId,
            EvidenceKind = "user-message",
            Timestamp = now,
            Sentiment = mood.Sentiment,
            Valence = mood.Valence,
            Label = mood.Label,
            Evidence = mood.Evidence,
            Topic = topic,
            ProjectId = resolvedProject?.Id,
        }, ct);

        // The moment rubs off on her too â€” honest emotional contagion, one small step.
        await _innerState.NudgeAsync(userId, mood.Valence, evidenceEventId, ct);

        _logger.LogDebug(
            "Captured mood for {UserId}: {Sentiment} ({Valence:+0.00;-0.00}) about \"{Topic}\" from \"{Evidence}\"",
            userId, mood.Sentiment, mood.Valence, topic ?? "(untied)", mood.Evidence);
    }

    /// <summary>
    /// Holds a dated event the user just mentioned as an anticipation, so the companion can wish
    /// them luck on the day and ask how it went after. Deduped against open anticipations by
    /// description; no-op when the message names no dated plan.
    /// </summary>
    private async Task CaptureAnticipationAsync(string userId, Message userMessage, CancellationToken ct)
    {
        var detected = AnticipationDetector.Detect(userMessage.Content, _clock.GetLocalNow());
        if (detected is null)
            return;

        var open = await _anticipations.GetOpenAsync(userId, ct);
        if (open.Any(a => string.Equals(a.Description, detected.Description, StringComparison.OrdinalIgnoreCase)))
            return;

        await _anticipations.AddAsync(new Anticipation
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Description = detected.Description,
            EventAt = detected.EventAt,
            Evidence = detected.Evidence,
            SourceMessageId = userMessage.Id,
            Status = AnticipationStatus.Pending,
            CreatedAt = _clock.GetUtcNow(),
        }, ct);

        _logger.LogInformation(
            "Captured an anticipation for {UserId}: \"{Description}\" on {Day:yyyy-MM-dd}",
            userId, detected.Description, detected.EventAt);
    }

    /// <summary>
    /// Records a commitment the companion made in its reply as a companion-owned open loop, so it
    /// can proactively follow up later. Deduped against existing open commitments; no-op when the
    /// reply contains no clear promise.
    /// </summary>
    private async Task CaptureCommitmentAsync(
        string userId, string reply, Guid sourceMessageId, DateTimeOffset now, CancellationToken ct)
    {
        var commitment = CommitmentDetector.Detect(reply);
        if (commitment is null)
            return;

        var open = await _projects.GetOpenLoopsAsync(userId, onlyOpen: true, ct);
        if (open.Any(l => string.Equals(l.Owner, "companion", StringComparison.OrdinalIgnoreCase)
                && string.Equals(l.Description, commitment, StringComparison.OrdinalIgnoreCase)))
            return;

        await _projects.AddOpenLoopAsync(new OpenLoop
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ProjectId = null,
            Owner = "companion",
            Description = commitment,
            Status = OpenLoopStatus.Open,
            CreatedAt = now,
            SourceMessageId = sourceMessageId,
        }, ct);

        _logger.LogInformation("Captured a companion commitment for {UserId}: \"{Commitment}\"", userId, commitment);
    }

    /// <summary>A trace for a turn that paused (or cancelled) instead of answering â€” no retrieval/generation ran.</summary>
    private static TurnTrace ClarificationTrace(
        string userMessage, string response, TurnStatus status, Guid? pendingId,
        ProjectContext? projectContext = null)
        => new()
        {
            UserMessage = userMessage,
            Status = status,
            PendingClarificationId = pendingId,
            DetectedProject = null,
            Retrieved = Array.Empty<RetrievalResult>(),
            Excluded = Array.Empty<RetrievalResult>(),
            Packet = new ContextPacket { UserMessage = userMessage, ClarificationQuestion = response },
            Response = response,
            Extraction = MemoryExtractionResult.Empty,
            ProjectContext = projectContext ?? ProjectContext.Empty,
            ProjectUpdates = ProjectUpdateResult.Empty,
        };


    /// <summary>Recording is optional here for the same reason the gate is: neither may be required.</summary>
    private sealed class NoShadowRecorder : IShadowRecorder
    {
        public bool IsRecording => false;

        public bool IsShadowing => false;

        public Task RecordAsync(ShadowComparison comparison, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<ShadowAgreement>> GetAgreementAsync(
            DateTimeOffset since, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ShadowAgreement>>(Array.Empty<ShadowAgreement>());

        public Task<IReadOnlyList<ShadowComparison>> GetDisagreementsAsync(
            string? subject, int count, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ShadowComparison>>(Array.Empty<ShadowComparison>());

        public Task<IReadOnlyList<ShadowComparison>> GetCapturesAsync(
            string? subject, int count, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ShadowComparison>>(Array.Empty<ShadowComparison>());

        public Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<int> ForgetByEvidenceAsync(
            string userId, IReadOnlyCollection<Guid> messageIds, DateTimeOffset now,
            Guid? memoryId = null, CancellationToken ct = default)
            => Task.FromResult(0);
    }

}

