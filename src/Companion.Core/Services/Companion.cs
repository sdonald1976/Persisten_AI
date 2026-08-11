using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Companion.Core.Services;

/// <summary>
/// Orchestrates one conversation turn:
/// store message → (resolve a pending clarification, or detect a new ambiguity) →
/// resolve project &amp; build project context → retrieve memories → assemble bounded context →
/// generate → store → extract &amp; validate memories → update project/open-loop state → trace.
///
/// Ambiguity is a control-flow state, not a prompt note: when a project reference is materially
/// ambiguous the turn stores a deterministic clarifying question and a pending-resolution record
/// and STOPS — no retrieval-for-answer, no chat generation, no memory extraction, no project
/// mutation. The next message tries to resolve that pending item before being treated as new.
/// </summary>
public sealed class Companion : ICompanion
{
    private readonly IConversationStore _conversations;
    private readonly IProjectContextService _projectContext;
    private readonly IPendingClarificationStore _pending;
    private readonly IProfileStore _profiles;
    private readonly IRetriever _retriever;
    private readonly IContextAssembler _assembler;
    private readonly IReplyGenerator _replyGenerator;
    private readonly IPersonalityService _personality;
    private readonly IMemoryPipeline _pipeline;
    private readonly IProjectUpdater _projectUpdater;
    private readonly IProjectStore _projects;
    private readonly IEmotionStore _emotions;
    private readonly IRelationshipTracker _relationship;
    private readonly IReflectionStore _reflections;
    private readonly IAnticipationStore _anticipations;
    private readonly ICompanionStateTracker _innerState;
    private readonly IFamiliarityTracker _familiarity;
    private readonly IPreferenceStore _preferences;
    private readonly IPrivacyClassifier _privacy;
    private readonly CompanionOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<Companion> _logger;

    public Companion(
        IConversationStore conversations,
        IProjectContextService projectContext,
        IPendingClarificationStore pending,
        IProfileStore profiles,
        IRetriever retriever,
        IContextAssembler assembler,
        IReplyGenerator replyGenerator,
        IPersonalityService personality,
        IMemoryPipeline pipeline,
        IProjectUpdater projectUpdater,
        IProjectStore projects,
        IEmotionStore emotions,
        IRelationshipTracker relationship,
        IReflectionStore reflections,
        IAnticipationStore anticipations,
        ICompanionStateTracker innerState,
        IFamiliarityTracker familiarity,
        IPreferenceStore preferences,
        IPrivacyClassifier privacy,
        IOptions<CompanionOptions> options,
        TimeProvider clock,
        ILogger<Companion> logger)
    {
        _conversations = conversations;
        _projectContext = projectContext;
        _pending = pending;
        _profiles = profiles;
        _retriever = retriever;
        _assembler = assembler;
        _replyGenerator = replyGenerator;
        _personality = personality;
        _pipeline = pipeline;
        _projectUpdater = projectUpdater;
        _projects = projects;
        _emotions = emotions;
        _relationship = relationship;
        _reflections = reflections;
        _anticipations = anticipations;
        _innerState = innerState;
        _familiarity = familiarity;
        _preferences = preferences;
        _privacy = privacy;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    public async Task<TurnTrace> RespondAsync(
        string userId, Guid conversationId, string userMessage,
        IProgress<string>? tokenSink = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            throw new ArgumentException("User message must not be empty.", nameof(userMessage));

        // The conversation must exist and belong to this user before ANY work happens — no message
        // storage, retrieval, generation, extraction, or project/open-loop mutation on an unknown
        // or foreign conversation. A missing conversation is an invalid request, not a new one.
        var conversation = await _conversations.GetConversationAsync(conversationId, userId, ct);
        if (conversation is null)
            throw new ConversationNotFoundException(conversationId);

        var now = _clock.GetUtcNow();

        // Temporal anchor: how long since they last spoke, read BEFORE this message is stored so
        // the gap describes the actual absence, not this very turn.
        var lastSeenBefore = await _conversations.GetLastMessageAtAsync(userId, ct);

        // 1–2. Store the raw user message. (Raw storage is unconditional — private turns skip
        // durable *derived* memory, see the extraction gate below, not conversation storage.)
        var userMsg = await StoreMessageAsync(userId, conversationId, MessageRole.User, userMessage, replyToId: null, now, ct);

        // If a clarification is pending in this conversation, try to resolve it before treating
        // this message as a brand-new request.
        var pending = await _pending.GetActiveAsync(userId, conversationId, ct);
        if (pending is not null)
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

            const string ack = "No problem — I've dropped that. What would you like to do instead?";
            await StoreMessageAsync(userId, conversationId, MessageRole.Assistant, ack, replyMsg.Id, _clock.GetUtcNow(), ct);
            return ClarificationTrace(replyMsg.Content, ack, TurnStatus.ClarificationCancelled, pending.Id);
        }

        if (decision.Kind == ClarificationDecisionKind.StillAmbiguous)
        {
            // Ask again rather than guess. The pending item stays open.
            await StoreMessageAsync(userId, conversationId, MessageRole.Assistant, pending.Question, replyMsg.Id, _clock.GetUtcNow(), ct);
            return ClarificationTrace(replyMsg.Content, pending.Question, TurnStatus.ClarificationRequested, pending.Id);
        }

        // Resolved → record the audit trail and resume the ORIGINAL request with the chosen project.
        pending.Status = ClarificationStatus.Resolved;
        pending.ResolvedProjectId = decision.ProjectId;
        pending.ResolutionNote = decision.Note;
        pending.ResolvedAt = now;
        await _pending.UpdateAsync(pending, ct);

        var forced = await _projectContext.BuildForProjectAsync(userId, pending.OriginalText, decision.ProjectId!.Value, ct);

        // Extract from the ORIGINAL message (now disambiguated), never from the terse reply
        // ("the buoy one") — so a clarification answer never becomes a durable memory on its own.
        var originalMsg = await _conversations.GetMessageAsync(pending.OriginalMessageId, userId, ct) ?? replyMsg;

        _logger.LogInformation(
            "Resolved clarification {Pending} for {UserId} → project {Project} ({Note})",
            pending.Id, userId, decision.ProjectId, decision.Note);

        return await CompleteTurnAsync(
            userId, conversationId, pending.OriginalText, forced,
            extractionSource: originalMsg, replyToId: replyMsg.Id, TurnStatus.ClarificationResolved, pending.Id, tokenSink, now, lastSeenBefore, ct);
    }

    // ---- the normal turn tail (retrieve → generate → store → extract → update) ----

    private async Task<TurnTrace> CompleteTurnAsync(
        string userId, Guid conversationId, string promptText, ProjectContext projectContext,
        Message extractionSource, Guid replyToId, TurnStatus status, Guid? pendingId,
        IProgress<string>? tokenSink, DateTimeOffset now, DateTimeOffset? lastSeenBefore, CancellationToken ct)
    {
        // Privacy gate, computed up front: a "don't remember this conversation" turn produces a
        // reply but writes NO durable derived memory — no extraction, no project/open-loop updates,
        // and no emotional signal. Raw messages are still stored for in-session context.
        var conversation = await _conversations.GetConversationAsync(conversationId, userId, ct);
        var sensitive = await _privacy.ShouldSkipDerivedMemoryAsync(promptText, ct);
        var remember = _options.EnableExtraction && !(conversation?.DoNotRemember ?? false) && !sensitive;
        if (sensitive)
            _logger.LogInformation("Derived memory skipped for {UserId}: privacy classifier marked the turn sensitive.", userId);

        // Roleplay gate: an in-character turn (RP markup, or a relationship word the persona has
        // claimed — if she IS "your sister", "my sister" means HER) gets a full reply but leaves
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

        // 4. Retrieve relevant memories, boosted by the resolved project.
        var outcome = await _retriever.RetrieveAsync(userId, promptText, projectContext.ResolvedProjectName, ct);

        // Recent prior turns (exclude the extraction source we just handled).
        var recent = (await _conversations.GetRecentMessagesAsync(
                conversationId, userId, _options.RecentMessageCount + 1, ct))
            .Where(m => m.Id != extractionSource.Id)
            .ToList();

        // 4b. Relational/emotional layer: read this message's tone and append it to the signal log
        // (gated by privacy), then derive how things have been feeling so the reply can attune its
        // tone. The snapshot includes this turn, so it reflects the user's mood right now.
        if (extractFacts)
        {
            await CaptureMoodAsync(userId, extractionSource, projectContext, now, ct);

            // A dated plan in the user's words ("interview on Thursday") becomes an anticipation:
            // encouragement on the day, a follow-up after — the caring-at-the-right-moment layer.
            await CaptureAnticipationAsync(userId, extractionSource, ct);
        }
        var relationship = await _relationship.BuildAsync(userId, ct);

        // 4c. The inner monologue reaches the turn here: a fresh private musing colors the reply's
        // attention, and at most one held curiosity is offered for the model to raise IF it fits.
        // Offering consumes it (marked voiced below), so a question is asked once, never nagged.
        var musing = await RelevantMusingAsync(userId, outcome.QueryEmbedding, now, ct);
        var curiosity = await _reflections.GetNextToVoiceAsync(
            userId, now, TimeSpan.FromHours(_options.CuriosityCooldownHours), ct);

        // 4d. Her own inner state — spirits + energy — colors the reply's tone (and answers
        // "how are you?" honestly). Read AFTER the mood capture above, so this turn's emotional
        // signal has already rubbed off on her. Familiarity calibrates how casual she may be.
        var innerState = await _innerState.BuildAsync(userId, ct);
        var familiarity = await _familiarity.BuildAsync(userId, ct);

        // 4e. Temporal grounding + her own relevant tastes for this turn (top few by similarity —
        // never the whole preference table, and never raw numbers).
        var temporal = TemporalNote(_clock.GetLocalNow(), now, lastSeenBefore);
        var preferenceNotes = await RelevantPreferencesAsync(
            userId, outcome.QueryEmbedding, identityProjection.CompanionRef, ct);

        // 5. Assemble a bounded, labeled context packet (with the user's persona/style + tone read).
        var packet = _assembler.Assemble(
            promptText, recent, outcome.Selected, projectContext, persona, relationship,
            musing, curiosity?.Question, innerState.Describe(), familiarity.Describe(),
            temporal, preferenceNotes, identityProjection);

        // 6. Generate the response. The reply generator owns "when to keep going" — it continues a
        // cut-off or self-truncated answer (feeding the text so far back so it resumes the SAME
        // task), and streams to the sink across rounds when one is provided.
        var generated = await _replyGenerator.GenerateAsync(packet.Render(), promptText, tokenSink, ct);
        var response = generated.Text;

        // 7. Store the response, with the generation metadata (why it stopped, rounds, tokens) so a
        // reply is answerable after the fact instead of a mystery.
        var assistantMsg = await StoreMessageAsync(
            userId, conversationId, MessageRole.Assistant, response, replyToId, _clock.GetUtcNow(), ct, generated);

        // 8–9. Extract candidate memories from the exchange and validate/persist accepted ones.
        // (Skipped for private AND in-character turns — fiction never reaches the fact store.)
        var exchange = new[] { extractionSource, assistantMsg };
        var extraction = extractFacts
            ? await _pipeline.ProcessAsync(userId, exchange, ct)
            : MemoryExtractionResult.Empty;

        // 10. Reflect accepted memories into project/open-loop state.
        var updates = extractFacts
            ? await _projectUpdater.ApplyAsync(userId, exchange, extraction, projectContext, ct)
            : ProjectUpdateResult.Empty;

        // 10b. Capture a commitment the companion just made ("I'll check in tomorrow") as a
        // companion-owned open loop, so it can follow up next session instead of forgetting it said
        // so. Skipped on private and in-character turns; deduped against existing open commitments.
        if (extractFacts)
            await CaptureCommitmentAsync(userId, response, assistantMsg.Id, now, ct);

        // 10c. The offered curiosity is spent whether or not the model chose to raise it — asked
        // once (or passed over once) is the whole budget, so proactive wondering never nags.
        if (curiosity is not null)
            await _reflections.MarkVoicedAsync(userId, curiosity.Id, now, ct);

        // 11. Record the trace for debugging (`/why`).
        _logger.LogInformation(
            "Turn complete for {UserId}: {Selected} memories, project={Project}, " +
            "reply finish={Finish}/rounds={Rounds}, " +
            "extraction {Accepted}A/{Merged}M/{Review}R/{Rejected}X, {Actions} project updates",
            userId, outcome.Selected.Count, projectContext.ResolvedProjectName ?? "(none)",
            generated.FinishReason ?? "(none)", generated.Rounds,
            extraction.Accepted, extraction.Merged, extraction.NeedsReview, extraction.Rejected,
            updates.Actions.Count);

        return new TurnTrace
        {
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
        };
    }

    // ---- helpers ----

    /// <summary>A musing is only current for so long — after this it stops shaping turns by default.</summary>
    private static readonly TimeSpan MusingSurfaceWindow = TimeSpan.FromDays(7);

    /// <summary>How far back the diary is searched for a relevant past thought.</summary>
    private const int MusingSearchLookback = 50;

    /// <summary>Minimum cosine for an old musing to resurface on relevance alone.</summary>
    private const double MusingRelevanceFloor = 0.3;

    /// <summary>Below this age a musing reads as current; older ones get an age prefix.</summary>
    private static readonly TimeSpan MusingIsRecent = TimeSpan.FromHours(36);

    /// <summary>
    /// The musing that should color THIS turn: the most relevant past thought (by similarity to
    /// the turn's query — an old thought resurfaces on its own when the conversation comes back
    /// to it), falling back to the freshest one while it's still current. Reading the diary is
    /// side-effect free — a musing can accompany many turns; it is a mood the companion carries,
    /// unlike a curiosity, which is consumed the one time it is offered.
    /// </summary>
    private async Task<string?> RelevantMusingAsync(
        string userId, float[]? queryEmbedding, DateTimeOffset now, CancellationToken ct)
    {
        var musings = (await _reflections.GetRecentAsync(userId, MusingSearchLookback, ct))
            .Where(r => r.HasMusing)
            .ToList();
        if (musings.Count == 0)
            return null;

        // Relevance first: "I remember thinking about this a while back" — and it's literally true.
        if (queryEmbedding is not null)
        {
            var best = musings
                .Where(r => r.Embedding is not null)
                .Select(r => (Reflection: r, Score: ScoreMath.Cosine(queryEmbedding, r.Embedding!)))
                .OrderByDescending(x => x.Score)
                .FirstOrDefault();
            if (best.Reflection is not null && best.Score >= MusingRelevanceFloor)
                return WithAge(best.Reflection, now);
        }

        // Otherwise the freshest thought still colors the turn — but only while it's current.
        var newest = musings[0];
        return now - newest.CreatedAt <= MusingSurfaceWindow ? WithAge(newest, now) : null;
    }

    /// <summary>An older thought carries its age, so "I'd been thinking…" can be timed honestly.</summary>
    private static string WithAge(Reflection reflection, DateTimeOffset now)
        => now - reflection.CreatedAt <= MusingIsRecent
            ? reflection.Musing!
            : $"(a thought from {RelativeTime.Describe(now - reflection.CreatedAt)} ago) {reflection.Musing}";

    /// <summary>Gaps shorter than this are just an ongoing conversation, not an absence.</summary>
    private static readonly TimeSpan MinGapToMention = TimeSpan.FromMinutes(5);

    /// <summary>
    /// One compact line of temporal grounding: the day and time, and how long the user was
    /// actually gone (measured before this turn's message landed). The model turns this into
    /// "back already?" or "look who finally showed up" on its own — nothing is scripted.
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
    private const int MaxPreferenceNotes = 2;

    /// <summary>Minimum similarity for a taste to be relevant to this turn at all.</summary>
    private const double PreferenceRelevanceFloor = 0.25;

    /// <summary>Her tastes that are actually relevant to what's being discussed, in natural words.</summary>
    private async Task<IReadOnlyList<string>> RelevantPreferencesAsync(
        string userId, float[]? queryEmbedding, string companionName, CancellationToken ct)
    {
        if (queryEmbedding is null)
            return Array.Empty<string>();

        var all = await _preferences.GetAllAsync(userId, ct);
        return all
            .Where(p => p.Embedding is not null)
            .Select(p => (Preference: p, Score: ScoreMath.Cosine(queryEmbedding, p.Embedding!)))
            .Where(x => x.Score >= PreferenceRelevanceFloor)
            .OrderByDescending(x => x.Score)
            .Take(MaxPreferenceNotes)
            .Select(x => x.Preference.Describe(companionName))
            .ToList();
    }

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
    /// the emotional-signal log — the substrate the relationship snapshot is derived from. No-op on
    /// flat/neutral messages, so the log stays signal, not noise.
    /// </summary>
    private async Task CaptureMoodAsync(
        string userId, Message userMessage, ProjectContext projectContext, DateTimeOffset now, CancellationToken ct)
    {
        var mood = MoodDetector.Detect(userMessage.Content);
        if (mood.IsNeutral)
            return;

        // Tie the feeling to what it's about: the subject phrase from the message ("the interview"),
        // or — failing that — the project this turn resolved to, so a mood voiced while discussing a
        // project still knows its subject.
        var resolvedProject = projectContext.Summary?.Project;
        var topic = MoodTopic.Extract(userMessage.Content) ?? resolvedProject?.Name;

        // Latest feeling about a topic wins: a fresh reading about the same thing closes out the
        // prior one, so an old worry the user has now spoken to again isn't surfaced separately.
        if (topic is not null)
            await _emotions.MarkTopicFollowedUpAsync(userId, topic, ct);

        await _emotions.AddSignalAsync(new EmotionalSignal
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            MessageId = userMessage.Id,
            Timestamp = now,
            Sentiment = mood.Sentiment,
            Valence = mood.Valence,
            Label = mood.Label,
            Evidence = mood.Evidence,
            Topic = topic,
            ProjectId = resolvedProject?.Id,
        }, ct);

        // The moment rubs off on her too — honest emotional contagion, one small step.
        await _innerState.NudgeAsync(userId, mood.Valence, ct);

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

    /// <summary>A trace for a turn that paused (or cancelled) instead of answering — no retrieval/generation ran.</summary>
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
}
