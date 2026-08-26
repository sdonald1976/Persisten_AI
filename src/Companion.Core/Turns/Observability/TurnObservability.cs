using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Companion.PlanV3;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Companion.Core.Turns.Observability;

/// <summary>
/// Everything the turn records about itself, and nothing it decides.
///
/// The invariant, stated once because every method below depends on it: observability may
/// record what happened, but it cannot select, replace, suppress, reorder, or otherwise affect
/// the displayed reply or any durable cognitive state. No method here returns a reply, a
/// candidate, or a value the caller branches on to change what the user sees. The methods that
/// return anything at all return <see cref="DecisionRecord"/>s — trace annotations appended to
/// the trace and read by /why, never consulted by generation.
///
/// It is deliberately NOT the owner of TurnTrace. The trace the caller returns carries the
/// displayed reply out to the API, which makes it the turn's result rather than a record of it;
/// building it inside a component that must not touch the reply would be the wrong shape,
/// however well it matches the word "trace".
///
/// Nothing here catches its own exceptions, for the same reason the post-turn effects do not:
/// the caller's existing try/catch owns the "the turn still stands" decision, and swallowing
/// failures in here would silently change which records exist after a partial failure.
/// </summary>
public sealed class TurnObservability(
    ITurnTraceLog turnLog,
    IOptions<CompanionOptions> options,
    ILogger<TurnObservability> logger,
    IShadowRecorder? shadow = null,
    ICognitiveCapture? capture = null,
    IDiagnosticsStore? diagnostics = null) : ITeachingObserver
{
    private readonly IShadowRecorder _shadow = shadow ?? new NoShadowRecorder();
    private readonly ICognitiveCapture _capture = capture ?? new NoCognitiveCapture();
    private readonly CompanionOptions _options = options.Value;

    /// <summary>The serialized-plan contract uses the same camelCase + kebab-enum shape as
    /// every other JSON boundary — this string IS the future renderer's input format.</summary>
    private static readonly System.Text.Json.JsonSerializerOptions PlanJson =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>
    /// Whether comparison rows are being written at all. The caller asks before paying for the
    /// extra raw-query retrieval, because that retrieval exists only to feed a comparison row —
    /// it never reaches the prompt. This is a question ABOUT observability, not a decision made
    /// BY it: the answer only ever suppresses measurement, never changes the reply.
    /// </summary>
    public bool IsRecording => _shadow.IsRecording;

    /// <summary>
    /// The reply gate's comparison row. Execution already decided and already enforced; this
    /// writes down what it decided.
    /// </summary>
    public Task RecordGateRefusalAsync(
        string userId, Guid sourceMessageId, Guid conversationId,
        Execution.GateRefusal refusal, CancellationToken ct = default)
        => _shadow.RecordAsync(new ShadowComparison
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            SourceMessageId = sourceMessageId,
            ConversationId = conversationId,
            Subject = "safety.gate",
            Legacy = "allow",
            Model = "block",
            Confidence = 1.0,
            Agreed = false,
            Applied = refusal.Enforced ? "model" : "legacy",
            Input = refusal.Reason,
        }, ct);

    /// <summary>
    /// Plan fidelity, SHADOW: the deterministic checks of what the model actually said against
    /// what the plan required — the measurable half of the renderer contract, running long
    /// before the plan has any authority. Violations are decisions AND capture rows; changing
    /// the reply is not on the table here, which is why it belongs in observability at all.
    ///
    /// The returned decisions are appended by the caller at the position the loop used to
    /// occupy, so their order within the trace is unchanged.
    /// </summary>
    public async Task<IReadOnlyList<DecisionRecord>> RecordPlanFidelityAsync(
        string userId, Guid sourceMessageId, Guid conversationId,
        ResponsePlan plan, string response, CancellationToken ct = default)
    {
        var decisions = new List<DecisionRecord>();
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
                    SourceMessageId = sourceMessageId,
                    ConversationId = conversationId,
                    Subject = "plan.fidelity",
                    Legacy = $"violated:{check}",
                    Model = null,
                    Applied = "legacy",
                    Input = violation,
                }, ct);
            }
        }
        return decisions;
    }

    /// <summary>
    /// The corpus-capture tail: the user message, the DISPLAYED reply, and the working-context
    /// rows. Every row here is capture-only and changes nothing it observes.
    ///
    /// The caller invokes this inside its extract gate, exactly where the inline block sat: a
    /// turn not allowed to produce durable memory is not allowed to produce durable training
    /// data either.
    /// </summary>
    public async Task CaptureExchangeAsync(TurnCaptureSnapshot t, CancellationToken ct = default)
    {
        // Corpus capture, last and deliberately inside the caller's gate. Off unless
        // CognitiveModels:Capture is set, and it changes nothing it observes — see
        // ICognitiveCapture.
        await _capture.CaptureUserMessageAsync(
            t.ExtractionSource.Content, ct, t.UserId, t.ExtractionSource.Id, t.ConversationId);
        await _capture.CaptureReplyAsync(
            t.DisplayedReply, ct, t.UserId, t.ExtractionSource.Id, t.ConversationId);

        // Same discipline for the working-context rules: record what they decided on the
        // populations they decide about — that is the base rate every precision claim depends
        // on, and it has never been measured (the ToolNudge lesson). Capture-only; changes
        // nothing it observes.
        if (AnswerBindingDetector.TrailingQuestion(t.Recent) is { } openQuestion)
        {
            await Shadow.CaptureAsync(
                _shadow, "context.binding",
                t.Working.BoundQuestion is not null,
                $"{openQuestion} ||| {t.ExtractionSource.Content}", ct,
                t.UserId, t.ExtractionSource.Id, t.ConversationId);
        }
        if (_shadow.IsRecording)
        {
            // Every turn's intent verdict, with the working-context move as input context —
            // the corpus that decides whether this vocabulary ever earns authority over
            // generation.
            // The input tag carries the evidence the vocabulary decisions need: the
            // working-context move, the top RAW topical relevance this turn (for the
            // admit-unknown signal characterization), and whether the message had imperative
            // shape (for the request/directive vocabulary question).
            var topTopical = t.Selected.Count == 0 ? 0.0 : t.Selected.Max(r => r.Topical);
            await _shadow.RecordAsync(new ShadowComparison
            {
                Id = Guid.NewGuid(),
                UserId = t.UserId,
                SourceMessageId = t.ExtractionSource.Id,
                ConversationId = t.ConversationId,
                Subject = "turn.intent",
                Legacy = $"{t.Intent.Intent.ToKebab()} ({t.Intent.Confidence:F2})"
                    + (t.Intent.Candidates.Count > 1
                        ? $" over {t.Intent.Candidates[1].Intent.ToKebab()} ({t.Intent.Candidates[1].Confidence:F2})" : ""),
                Model = null,
                Applied = "legacy",
                Input = SecretDetector.LooksLikeSecret(t.ExtractionSource.Content)
                    ? null
                    : $"[{t.Working.Move.ToKebab()}|topical={topTopical:F2}" +
                      $"{(TurnIntentClassifier.LooksDirective(t.ExtractionSource.Content) ? "|directive" : "")}" +
                      $"{(t.Focal is null ? "" : t.Focal.Covered ? "|focal=covered" : "|focal=uncovered")}] " +
                      t.ExtractionSource.Content,
            }, ct);
        }
        if (t.Working.ReferenceMarkers.Count > 0 && _shadow.IsRecording)
        {
            await _shadow.RecordAsync(new ShadowComparison
            {
                Id = Guid.NewGuid(),
                UserId = t.UserId,
                SourceMessageId = t.ExtractionSource.Id,
                ConversationId = t.ConversationId,
                Subject = "context.reference",
                Legacy = $"{t.Working.Move.ToKebab()}: {t.Working.ReferenceMarkers.First()}"
                    + (t.Working.ResolvedReference is null ? " (unresolved)"
                        : $" -> {t.Working.ResolvedReference}"),
                Model = null,
                Applied = "legacy",
                Input = SecretDetector.LooksLikeSecret(t.ExtractionSource.Content)
                    ? null : t.ExtractionSource.Content,
            }, ct);
        }
    }

    /// <summary>
    /// The turn's own record of itself: the operational log line, the in-memory ring entry that
    /// powers diagnostics.last_turn, and its durable twin.
    ///
    /// The snapshot is bound to locals of the original names below so the three recording bodies
    /// are the moved code rather than a re-typing of it.
    /// </summary>
    public async Task RecordTurnAsync(TurnRecordSnapshot t, CancellationToken ct = default)
    {
        var userId = t.UserId;
        var traceId = t.TraceId;
        var now = t.Now;
        var promptText = t.PromptText;
        var response = t.Response;
        var renderedPrompt = t.RenderedPrompt;
        var retrievalQuery = t.RetrievalQuery;
        var extractionSource = t.ExtractionSource;
        var extractFacts = t.ExtractFacts;
        var inCharacter = t.InCharacter;
        var remember = t.Remember;
        var selectedMemories = t.SelectedMemories;
        var rawQueryRetrieved = t.RawQueryRetrieved;
        var decisions = t.Decisions;
        var working = t.Working;
        var intent = t.Intent;
        var plan = t.Plan;
        var focal = t.Focal;
        var packet = t.Packet;
        var projectContext = t.ProjectContext;
        var outcome = t.Outcome;
        var generated = t.Generated;
        var toolOutcome = t.ToolOutcome;
        var extraction = t.Extraction;
        var updates = t.Updates;

        logger.LogInformation(
            "Turn complete for {UserId}: {Selected} memories, project={Project}, " +
            "reply finish={Finish}/rounds={Rounds}, " +
            "extraction {Accepted}A/{Merged}M/{Review}R/{Rejected}X, {Actions} project updates",
            userId, outcome.Selected.Count, projectContext.ResolvedProjectName ?? "(none)",
            generated.FinishReason ?? "(none)", generated.Rounds,
            extraction.Accepted, extraction.Merged, extraction.NeedsReview, extraction.Rejected,
            updates.Actions.Count);

        // The operational record for "why did you say that?" — powers diagnostics.last_turn.
        turnLog.Record(userId, new TurnDiagnostics
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
        // produce durable derived data — structure survives, words do not — and previews of
        // ordinary turns mirror text the Messages table already stores. The store owns the
        // never-throw guarantee.
        if (diagnostics is not null)
        {
            string? Bounded(string? text, int max) =>
                !extractFacts || text is null ? null
                : SecretDetector.LooksLikeSecret(text) ? null
                : text.Length <= max ? text : text[..max];

            await diagnostics.RecordTurnAsync(new TurnRecord
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
    }

    /// <summary>
    /// Renderer shadow (docs/RENDERER_SHADOW.md): the tuned renderer renders the same plan
    /// beside the reply that just went out. Fire-and-forget on an immutable snapshot — by
    /// construction it cannot touch conversation state, memory, goals, tools, or what the user
    /// saw.
    ///
    /// The caller still owns the <c>IsObserving || IsCanaryFor</c> question upstream, because
    /// that one decides whether a native plan/4 is BUILT and so is a planning concern. This
    /// method owns only the observation itself, and the decision it returns is a trace
    /// annotation.
    /// </summary>
    public DecisionRecord? ObserveRendererShadow(
        RendererShadowEligibility e, Func<RendererShadowObservation> build)
    {
        // A tool turn is still ineligible for a renderer COMPARISON — run-1c never trained on
        // tool results, so scoring it there measures the corpus's absence rather than the
        // renderer. Its structural V3 evidence is what Source 2 needs, so it takes the
        // plan-only path: the row is written, the renderer never runs.
        var eligible = !e.Sensitive && e.ToolCallCount == 0 && !e.InCharacter;
        var planOnly = !e.Sensitive && !eligible;
        var decision = new DecisionRecord
        {
            Stage = "renderer.shadow", Decider = "config",
            Verdict = eligible ? "observed" : planOnly ? "plan-only" : "skipped",
            Reason = eligible ? null
                : e.Sensitive ? "privacy-sensitive turn"
                : e.InCharacter ? "in-character turn: run-1c has no roleplay capability"
                : "turn used tools",
        };
        if (eligible || planOnly)
        {
            var observation = build();
            if (eligible)
                e.Shadow.Observe(observation);
            else
                e.Shadow.ObservePlanOnly(observation);
        }
        return decision;
    }

    /// <summary>
    /// The one capture that cannot be hoisted out of the post-turn effects: it happens between
    /// concept learning and the gap observations, so lifting it to the caller would move it
    /// after them. <see cref="ITeachingObserver"/> exists so the effects depend on the narrow
    /// ability to OBSERVE rather than on a recorder they could write anything with.
    /// </summary>
    public Task ObserveTeachingAsync(
        string userId, Guid sourceMessageId, Guid conversationId,
        string userMessage, bool taught, CancellationToken ct = default)
    {
        // Every loose-copular sentence the detector rejected is a labeled negative for the
        // future corpus — broadening happens on data, never on intuition.
        if (!TeachingDetector.LooseShape(userMessage))
            return Task.CompletedTask;
        return Shadow.CaptureAsync(
            _shadow, "knowledge.teaching", taught, userMessage, ct,
            userId, sourceMessageId, conversationId);
    }

    /// <summary>Which packet sections were actually present — diagnostics, not content.</summary>
    internal static IReadOnlyList<string> PresentSections(ContextPacket packet)
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

/// <summary>
/// The narrow ability to write down that a teaching sentence was seen — and nothing else.
///
/// This exists so <c>PostTurnEffects</c>, which owns durable cognitive state, does not hold a
/// general-purpose recorder. An observer can record; it cannot read comparisons back, prune
/// them, or influence what the effects decide.
/// </summary>
public interface ITeachingObserver
{
    Task ObserveTeachingAsync(
        string userId, Guid sourceMessageId, Guid conversationId,
        string userMessage, bool taught, CancellationToken ct = default);
}

/// <summary>
/// The eligibility facts the renderer-shadow decision reads. A record rather than four loose
/// parameters so the call site reads as a statement of what the turn WAS, not as a switchboard.
/// </summary>
public sealed record RendererShadowEligibility
{
    public required IRendererShadow Shadow { get; init; }
    public required bool Sensitive { get; init; }
    public required bool InCharacter { get; init; }
    public required int ToolCallCount { get; init; }
}

/// <summary>
/// What the capture tail is allowed to see. As with <c>PostTurnRequest</c>, the ONLY reply in
/// here is the one the user saw — there is no field for a production candidate a canary
/// replaced, a canary candidate the guard rejected, or pre-gate text, so none of them can reach
/// a capture row by mistake.
/// </summary>
public sealed record TurnCaptureSnapshot
{
    public required string UserId { get; init; }
    public required Guid ConversationId { get; init; }
    public required Message ExtractionSource { get; init; }

    /// <summary>The reply the user actually saw. Never a candidate.</summary>
    public required string DisplayedReply { get; init; }

    public required IReadOnlyList<Message> Recent { get; init; }
    public required WorkingContextState Working { get; init; }
    public required TurnIntentState Intent { get; init; }
    public required IReadOnlyList<RetrievalResult> Selected { get; init; }
    public FocalCoverage? Focal { get; init; }
}

/// <summary>
/// The turn as it is written down. Same rule: <see cref="Response"/> is the displayed reply and
/// there is nowhere for a losing candidate to live.
/// </summary>
public sealed record TurnRecordSnapshot
{
    public required Guid TraceId { get; init; }
    public required string UserId { get; init; }
    public required DateTimeOffset Now { get; init; }
    public required string PromptText { get; init; }

    /// <summary>The reply the user actually saw. Never a candidate.</summary>
    public required string Response { get; init; }

    public required string RenderedPrompt { get; init; }
    public required string RetrievalQuery { get; init; }
    public required Message ExtractionSource { get; init; }
    public required bool ExtractFacts { get; init; }
    public required bool InCharacter { get; init; }
    public required bool Remember { get; init; }
    public required IReadOnlyList<RetrievalResult> SelectedMemories { get; init; }
    public required IReadOnlyList<string> RawQueryRetrieved { get; init; }
    public required IReadOnlyList<DecisionRecord> Decisions { get; init; }
    public required WorkingContextState Working { get; init; }
    public required TurnIntentState Intent { get; init; }
    public required ResponsePlan Plan { get; init; }
    public FocalCoverage? Focal { get; init; }
    public required ContextPacket Packet { get; init; }
    public required ProjectContext ProjectContext { get; init; }
    public required RetrievalOutcome Outcome { get; init; }
    public required ChatCompletion Generated { get; init; }
    public required ToolLoop.Outcome ToolOutcome { get; init; }
    public required MemoryExtractionResult Extraction { get; init; }
    public required ProjectUpdateResult Updates { get; init; }
}
