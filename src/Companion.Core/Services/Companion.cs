using System.Text;
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
    private readonly IChatModel _chat;
    private readonly IMemoryPipeline _pipeline;
    private readonly IProjectUpdater _projectUpdater;
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
        IChatModel chat,
        IMemoryPipeline pipeline,
        IProjectUpdater projectUpdater,
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
        _chat = chat;
        _pipeline = pipeline;
        _projectUpdater = projectUpdater;
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

        var now = _clock.GetUtcNow();

        // 1–2. Store the raw user message. (Raw storage is unconditional — private turns skip
        // durable *derived* memory, see the extraction gate below, not conversation storage.)
        var userMsg = await StoreMessageAsync(userId, conversationId, MessageRole.User, userMessage, replyToId: null, now, ct);

        // If a clarification is pending in this conversation, try to resolve it before treating
        // this message as a brand-new request.
        var pending = await _pending.GetActiveAsync(userId, conversationId, ct);
        if (pending is not null)
            return await ResolvePendingAsync(userId, conversationId, pending, userMsg, tokenSink, now, ct);

        // 3. Resolve the project reference and build project-aware context.
        var projectContext = await _projectContext.BuildAsync(userId, userMessage, ct);

        // Ambiguity is control flow: ask, record a pending item, and run nothing else.
        if (projectContext.Resolution.RequiresClarification)
            return await RequestClarificationAsync(userId, conversationId, userMsg, projectContext, now, ct);

        // Normal answered turn.
        return await CompleteTurnAsync(
            userId, conversationId, userMessage, projectContext,
            extractionSource: userMsg, replyToId: userMsg.Id, TurnStatus.Answered, pendingId: null, tokenSink, now, ct);
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
        IProgress<string>? tokenSink, DateTimeOffset now, CancellationToken ct)
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
            extractionSource: originalMsg, replyToId: replyMsg.Id, TurnStatus.ClarificationResolved, pending.Id, tokenSink, now, ct);
    }

    // ---- the normal turn tail (retrieve → generate → store → extract → update) ----

    private async Task<TurnTrace> CompleteTurnAsync(
        string userId, Guid conversationId, string promptText, ProjectContext projectContext,
        Message extractionSource, Guid replyToId, TurnStatus status, Guid? pendingId,
        IProgress<string>? tokenSink, DateTimeOffset now, CancellationToken ct)
    {
        // 4. Retrieve relevant memories, boosted by the resolved project.
        var outcome = await _retriever.RetrieveAsync(userId, promptText, projectContext.ResolvedProjectName, ct);

        // Recent prior turns (exclude the extraction source we just handled).
        var recent = (await _conversations.GetRecentMessagesAsync(
                conversationId, userId, _options.RecentMessageCount + 1, ct))
            .Where(m => m.Id != extractionSource.Id)
            .ToList();

        // 5. Assemble a bounded, labeled context packet (with the user's persona/style).
        var profile = await _profiles.GetOrCreateAsync(userId, ct);
        var packet = _assembler.Assemble(promptText, recent, outcome.Selected, projectContext, profile.Persona);

        // 6. Generate the response — streamed to the sink when one is provided, otherwise in one shot.
        string response;
        if (tokenSink is not null)
        {
            var buffer = new StringBuilder();
            await foreach (var chunk in _chat.StreamAsync(packet.Render(), promptText, ct))
            {
                buffer.Append(chunk);
                tokenSink.Report(chunk);
            }
            response = buffer.Length == 0 ? "(the model returned an empty response)" : buffer.ToString();
        }
        else
        {
            response = await _chat.CompleteAsync(packet.Render(), promptText, jsonMode: false, ct);
        }

        // 7. Store the response.
        var assistantMsg = await StoreMessageAsync(
            userId, conversationId, MessageRole.Assistant, response, replyToId, _clock.GetUtcNow(), ct);

        // Privacy: a "don't remember this conversation" turn produces a reply but creates NO
        // durable derived memory — extraction and project/open-loop updates are skipped. Raw
        // messages are still stored (needed for in-session context), but nothing is baked into
        // long-term memory.
        var conversation = await _conversations.GetConversationAsync(conversationId, userId, ct);
        var remember = _options.EnableExtraction && !(conversation?.DoNotRemember ?? false);

        // 8–9. Extract candidate memories from the exchange and validate/persist accepted ones.
        var exchange = new[] { extractionSource, assistantMsg };
        var extraction = remember
            ? await _pipeline.ProcessAsync(userId, exchange, ct)
            : MemoryExtractionResult.Empty;

        // 10. Reflect accepted memories into project/open-loop state.
        var updates = remember
            ? await _projectUpdater.ApplyAsync(userId, exchange, extraction, projectContext, ct)
            : ProjectUpdateResult.Empty;

        // 11. Record the trace for debugging (`/why`).
        _logger.LogInformation(
            "Turn complete for {UserId}: {Selected} memories, project={Project}, " +
            "extraction {Accepted}A/{Merged}M/{Review}R/{Rejected}X, {Actions} project updates",
            userId, outcome.Selected.Count, projectContext.ResolvedProjectName ?? "(none)",
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

    private async Task<Message> StoreMessageAsync(
        string userId, Guid conversationId, MessageRole role, string content, Guid? replyToId,
        DateTimeOffset timestamp, CancellationToken ct)
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
        };
        await _conversations.AddMessageAsync(message, ct);
        return message;
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
