using System.Text;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Companion.Core.Services;

/// <summary>
/// Orchestrates one conversation turn:
/// store message → resolve project & build project context → retrieve memories →
/// assemble bounded context → generate → store → extract & validate memories (Phase 3) →
/// update project/open-loop state (Phase 4) → trace.
/// </summary>
public sealed class Companion : ICompanion
{
    private readonly IConversationStore _conversations;
    private readonly IProjectContextService _projectContext;
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

        // 3. Resolve the project reference and build project-aware context (summary + open loops).
        var projectContext = await _projectContext.BuildAsync(userId, userMessage, ct);

        // 4. Retrieve relevant memories, boosted by the resolved project.
        var outcome = await _retriever.RetrieveAsync(
            userId, userMessage, projectContext.ResolvedProjectName, ct);

        // Recent prior turns (exclude the message we just stored).
        var recent = (await _conversations.GetRecentMessagesAsync(
                conversationId, userId, _options.RecentMessageCount + 1, ct))
            .Where(m => m.Id != userMsg.Id)
            .ToList();

        // 5. Assemble a bounded, labeled context packet (with the user's persona/style).
        var profile = await _profiles.GetOrCreateAsync(userId, ct);
        var packet = _assembler.Assemble(userMessage, recent, outcome.Selected, projectContext, profile.Persona);

        // 6. Generate the response — streamed to the sink when one is provided, otherwise in one shot.
        string response;
        if (tokenSink is not null)
        {
            var buffer = new StringBuilder();
            await foreach (var chunk in _chat.StreamAsync(packet.Render(), userMessage, ct))
            {
                buffer.Append(chunk);
                tokenSink.Report(chunk);
            }
            response = buffer.Length == 0 ? "(the model returned an empty response)" : buffer.ToString();
        }
        else
        {
            response = await _chat.CompleteAsync(packet.Render(), userMessage, ct);
        }

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

        // 8–9. Extract candidate memories from the exchange and validate/persist accepted ones.
        var exchange = new[] { userMsg, assistantMsg };
        var extraction = _options.EnableExtraction
            ? await _pipeline.ProcessAsync(userId, exchange, ct)
            : MemoryExtractionResult.Empty;

        // 10. Reflect accepted memories into project/open-loop state.
        var updates = _options.EnableExtraction
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
            UserMessage = userMessage,
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
}
