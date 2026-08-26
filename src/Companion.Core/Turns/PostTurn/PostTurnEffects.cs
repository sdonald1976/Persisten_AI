using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Microsoft.Extensions.Logging;

namespace Companion.Core.Turns.PostTurn;

/// <summary>
/// What the completed turn is allowed to learn from.
///
/// <see cref="DisplayedReply"/> is the ONLY reply in here, and it is the one the user saw.
/// There is deliberately no field for a production candidate that lost to the canary, a
/// canary candidate that was rejected, or a pre-gate response — a value that does not exist
/// on the request cannot reach durable state by mistake.
/// </summary>
public sealed record PostTurnRequest
{
    public required Guid TraceId { get; init; }
    public required string UserId { get; init; }
    public required Guid ConversationId { get; init; }
    public required DateTimeOffset Now { get; init; }

    /// <summary>The user message this turn answered.</summary>
    public required Message ExtractionSource { get; init; }

    /// <summary>The stored assistant message. Its content IS the displayed reply.</summary>
    public required Message AssistantMessage { get; init; }

    /// <summary>The reply the user actually saw. Never a candidate.</summary>
    public required string DisplayedReply { get; init; }

    public required ProjectContext ProjectContext { get; init; }
    public required WorkingContextState Working { get; init; }
    public ReferenceResolution? ExtractionResolution { get; init; }
    public ConceptLookupResult? Knowledge { get; init; }
    public required PersonaLexicon Lexicon { get; init; }
}

/// <summary>Content-safe facts the caller still needs after the effects have run.</summary>
public sealed record PostTurnEffectsResult
{
    public required MemoryExtractionResult Extraction { get; init; }
    public required ProjectUpdateResult Updates { get; init; }

    /// <summary>The term learned this turn, if any. Needed by nothing else; reported for the trace.</summary>
    public string? TaughtTerm { get; init; }

    /// <summary>Decisions produced here, appended by the caller at their existing positions.</summary>
    public required IReadOnlyList<DecisionRecord> Decisions { get; init; }
}

/// <summary>
/// The sixth stage of a turn: everything the completed exchange durably changes.
///
/// The invariant this exists to hold: every durable effect observes the user's message and
/// the reply that was ACTUALLY DISPLAYED. Not the production candidate a canary replaced, not
/// a canary reply the critical guard rejected, not the pre-gate text, not tool intermediate
/// output, and not native plan/4. The request type enforces it structurally — those values
/// have nowhere to live on it.
///
/// It does NOT own the mood and anticipation capture. Those run before generation because
/// this turn's inner state colors this turn's own prompt, and relabelling them "post-turn" to
/// move more code would change when Ava's mood is read.
///
/// It also does not wrap anything in a transaction. These effects were independent before and
/// stay independent: a failure part-way leaves what already succeeded, and the caller's
/// existing catch decides that the turn still stands. Nothing here catches its own exceptions,
/// precisely so that behaviour is unchanged.
/// </summary>
public sealed class PostTurnEffects(
    IConversationStore conversations,
    IMemoryPipeline pipeline,
    IProjectUpdater projectUpdater,
    IProjectStore projects,
    IAttentionService attention,
    IProcedureStore procedures,
    IReflectionStore reflections,
    ILogger<PostTurnEffects> logger,
    IConceptKnowledge? concepts = null,
    IGapStore? gaps = null,
    // Used for exactly ONE interleaved capture, inside concept learning. It is co-located
    // rather than returned because the alternative reorders writes: the capture happens
    // between learning and the gap observations, and hoisting it to the caller would move it
    // after them. Preserving effect order matters more than the tidier boundary.
    IShadowRecorder? shadow = null)
{
    /// <summary>
    /// Stores the assistant reply with its generation metadata, so a reply is answerable after
    /// the fact rather than a mystery. This is the message every later effect reads.
    /// </summary>
    public async Task<Message> StoreReplyAsync(
        string userId, Guid conversationId, string displayedReply, Guid replyToId,
        DateTimeOffset at, ChatCompletion generation, CancellationToken ct = default)
    {
        var message = new Message
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            UserId = userId,
            Role = MessageRole.Assistant,
            Content = displayedReply,
            ReplyToId = replyToId,
            TokenCount = ContextAssembler.EstimateTokens(displayedReply),
            Timestamp = at,
            FinishReason = generation.FinishReason,
            GenerationRounds = generation.Rounds,
            Truncated = generation.Truncated,
            ModelUsed = generation.Model,
            PromptTokens = generation.PromptTokens,
            CompletionTokens = generation.CompletionTokens,
        };
        await conversations.AddMessageAsync(message, ct);
        return message;
    }

    /// <summary>
    /// The derived-state work, in the order it has always run. Throws on failure rather than
    /// swallowing: the caller's catch owns the "the turn still stands" decision, and moving
    /// that judgement in here would change which effects are skipped after a failure.
    /// </summary>
    public async Task<PostTurnEffectsResult> ApplyAsync(
        PostTurnRequest request, CancellationToken ct = default)
    {
        var decisions = new List<DecisionRecord>();
        var exchange = new[] { request.ExtractionSource, request.AssistantMessage };

        var extraction = await pipeline.ProcessAsync(
            request.UserId, exchange, request.ExtractionResolution, ct);
        var updates = await projectUpdater.ApplyAsync(
            request.UserId, exchange, extraction, request.ProjectContext, ct);

        await attention.CaptureTurnAsync(request.UserId, request.ExtractionSource, remember: true, ct);
        await procedures.ApplyRevisionAsync(request.UserId, request.ExtractionSource, request.Now, ct);
        await procedures.AddOrUpdateFromTeachingAsync(
            request.UserId, request.ConversationId, request.ExtractionSource, request.Now, ct);
        await CaptureCommitmentAsync(
            request.UserId, request.DisplayedReply, request.AssistantMessage.Id, request.Now, ct);

        // Explicit teaching becomes Ava-owned world knowledge — user message only,
        // high-precision detector, evidence-bound. Inside the caller's extract gate
        // deliberately: a turn not allowed durable derived memory is not allowed durable
        // knowledge either.
        string? taught = null;
        if (concepts is not null)
        {
            taught = await concepts.LearnFromAsync(
                request.UserId, request.ExtractionSource, request.Lexicon, ct);
            if (taught is not null)
            {
                decisions.Add(new DecisionRecord
                {
                    Stage = "knowledge.taught", Decider = "rule",
                    Verdict = ConceptKnowledge.Canonical(taught),
                });
            }
            // Every loose-copular sentence the detector rejected is a labeled negative for
            // the future corpus — broadening happens on data, never on intuition.
            if (shadow is not null && TeachingDetector.LooseShape(request.ExtractionSource.Content))
            {
                await Shadow.CaptureAsync(
                    shadow, "knowledge.teaching", taught is not null,
                    request.ExtractionSource.Content, ct,
                    request.UserId, request.ExtractionSource.Id, request.ConversationId);
            }
        }

        // Knowledge gaps: observable epistemic events become typed, deduped,
        // provenance-bearing rows. Recording is NOT a promise to ask.
        if (gaps is not null)
        {
            async Task ObserveGapAsync(GapKind kind, string subject, GapSource source)
            {
                // sourceRef stays the trace id (diagnostic provenance); the message id is
                // what /forget matches on, and a gap accumulates many of them.
                var (gap, _) = await gaps.ObserveAsync(
                    request.UserId, kind, subject, source, request.TraceId, request.Now,
                    request.ExtractionSource.Id, ct);
                decisions.Add(new DecisionRecord
                {
                    Stage = "gap.observed", Decider = "rule",
                    Verdict = $"{kind.ToKebab()}:{subject}",
                    Reason = $"seen {gap.Occurrences}x ({gap.Status.ToKebab()})",
                });
            }

            if (request.Knowledge is { } knowledge)
            {
                var subject = ConceptKnowledge.Canonical(knowledge.Term);
                if (knowledge.Familiarity == ConceptFamiliarity.Unknown)
                    await ObserveGapAsync(GapKind.UnknownConcept, subject, GapSource.KnowledgeLookup);
                else if (knowledge.Familiarity is ConceptFamiliarity.Learning or ConceptFamiliarity.Disputed)
                    await ObserveGapAsync(GapKind.UncertainKnowledge, subject, GapSource.KnowledgeLookup);
            }

            // An unpinned reference: recorded, never promoted in v1 (it ages badly).
            if (request.Working is { ReferenceMarkers.Count: > 0 }
                && (request.Working.ResolvedReference is null
                    || request.Working.ResolutionConfidence == ResolutionConfidence.Guess))
            {
                await ObserveGapAsync(GapKind.UnresolvedReference,
                    request.Working.ReferenceMarkers[0].ToLowerInvariant(), GapSource.WorkingContext);
            }

            // Conflicting evidence the pipeline parked for review.
            foreach (var parked in extraction.Decisions
                         .Where(d => d.Outcome == MemoryDecisionKind.NeedsReview).Take(2))
            {
                await ObserveGapAsync(GapKind.ConflictingEvidence,
                    $"{parked.Candidate.Subject}/{parked.Candidate.Predicate}".ToLowerInvariant(),
                    GapSource.MemoryReview);
            }

            // Teaching satisfies: the loop closes with provenance, and the linked curiosity
            // closes with it.
            if (taught is not null)
            {
                var satisfied = await gaps.SatisfyBySubjectAsync(
                    request.UserId, ConceptKnowledge.Canonical(taught),
                    $"learned from teaching on {request.Now:MMM d}", ct);
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

        return new PostTurnEffectsResult
        {
            Extraction = extraction,
            Updates = updates,
            TaughtTerm = taught,
            Decisions = decisions,
        };
    }

    /// <summary>
    /// The offered curiosity is spent whether or not the model chose to raise it — asked once,
    /// or passed over once, is the whole budget, so proactive wondering never nags.
    ///
    /// Outside <see cref="ApplyAsync"/> because it runs outside the caller's try: a curiosity
    /// must be marked spent even when the derived-state work failed.
    /// </summary>
    public Task MarkCuriosityVoicedAsync(
        string userId, Guid curiosityId, DateTimeOffset now, CancellationToken ct = default)
        => reflections.MarkVoicedAsync(userId, curiosityId, now, ct);

    /// <summary>
    /// A commitment the companion just made ("I'll check in tomorrow") becomes a
    /// companion-owned open loop, so it can follow up next session instead of forgetting it
    /// said so. Deduped against existing open commitments. Moved verbatim.
    /// </summary>
    private async Task CaptureCommitmentAsync(
        string userId, string reply, Guid sourceMessageId, DateTimeOffset now, CancellationToken ct)
    {
        var commitment = CommitmentDetector.Detect(reply);
        if (commitment is null)
            return;

        var open = await projects.GetOpenLoopsAsync(userId, onlyOpen: true, ct);
        if (open.Any(l => string.Equals(l.Owner, "companion", StringComparison.OrdinalIgnoreCase)
                && string.Equals(l.Description, commitment, StringComparison.OrdinalIgnoreCase)))
            return;

        await projects.AddOpenLoopAsync(new OpenLoop
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

        logger.LogInformation("Captured a companion commitment for {UserId}: \"{Commitment}\"", userId, commitment);
    }
}
