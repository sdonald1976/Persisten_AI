using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Companion.Core.Services;

/// <summary>
/// Orchestrates one conversation turn (Phase 2 subset of the full pipeline):
/// store message → retrieve → assemble bounded context → generate → store response → trace.
/// Memory extraction and project/open-loop updates (steps 8–10) are added in Phase 3+;
/// their insertion point is marked below.
/// </summary>
public sealed class Companion : ICompanion
{
    private readonly IConversationStore _conversations;
    private readonly IRetriever _retriever;
    private readonly IContextAssembler _assembler;
    private readonly IChatModel _chat;
    private readonly IMemoryPipeline _pipeline;
    private readonly CompanionOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<Companion> _logger;

    public Companion(
        IConversationStore conversations,
        IRetriever retriever,
        IContextAssembler assembler,
        IChatModel chat,
        IMemoryPipeline pipeline,
        IOptions<CompanionOptions> options,
        TimeProvider clock,
        ILogger<Companion> logger)
    {
        _conversations = conversations;
        _retriever = retriever;
        _assembler = assembler;
        _chat = chat;
        _pipeline = pipeline;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    public async Task<TurnTrace> RespondAsync(
        string userId, Guid conversationId, string userMessage, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            throw new ArgumentException("User message must not be empty.", nameof(userMessage));

        var now = _clock.GetUtcNow();

        // 1–2. Store the raw user message.
        var userMsg = new Message
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            UserId = userId,
            Role = MessageRole.User,
            Content = userMessage,
            TokenCount = ContextAssembler.EstimateTokens(userMessage),
            Timestamp = now,
        };
        await _conversations.AddMessageAsync(userMsg, ct);

        // 3–4. Detect project references and retrieve relevant memories (ranked + explained).
        var outcome = await _retriever.RetrieveAsync(userId, userMessage, ct);

        // Recent prior turns (exclude the message we just stored).
        var recent = (await _conversations.GetRecentMessagesAsync(
                conversationId, userId, _options.RecentMessageCount + 1, ct))
            .Where(m => m.Id != userMsg.Id)
            .ToList();

        // 5. Assemble a bounded, labeled context packet.
        var packet = _assembler.Assemble(userMessage, recent, outcome.Selected);

        // 6. Generate the response.
        var response = await _chat.CompleteAsync(packet.Render(), userMessage, ct);

        // 7. Store the response.
        var assistantMsg = new Message
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            UserId = userId,
            Role = MessageRole.Assistant,
            Content = response,
            ReplyToId = userMsg.Id,
            TokenCount = ContextAssembler.EstimateTokens(response),
            Timestamp = _clock.GetUtcNow(),
        };
        await _conversations.AddMessageAsync(assistantMsg, ct);

        // 8–9. Extract candidate memories from the exchange and validate/persist accepted
        // ones. Extraction only proposes; the pipeline decides. (Project & open-loop updates
        // — step 10 — arrive in Phase 4.)
        var extraction = _options.EnableExtraction
            ? await _pipeline.ProcessAsync(userId, new[] { userMsg, assistantMsg }, ct)
            : MemoryExtractionResult.Empty;

        // 11. Record the trace for debugging (`/why`).
        _logger.LogInformation(
            "Turn complete for {UserId}: {Selected} memories used, project={Project}, " +
            "extraction: {Accepted} accepted / {Merged} merged / {Review} review / {Rejected} rejected",
            userId, outcome.Selected.Count, outcome.DetectedProject ?? "(none)",
            extraction.Accepted, extraction.Merged, extraction.NeedsReview, extraction.Rejected);

        return new TurnTrace
        {
            UserMessage = userMessage,
            DetectedProject = outcome.DetectedProject,
            Retrieved = outcome.Selected,
            Excluded = outcome.Excluded,
            Packet = packet,
            Response = response,
            Extraction = extraction,
        };
    }
}
