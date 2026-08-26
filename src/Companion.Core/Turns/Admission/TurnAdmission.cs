using Companion.Core.Abstractions;
using Companion.Core.Domain;

namespace Companion.Core.Turns.Admission;

/// <summary>
/// What admission established, as typed sections rather than a bag.
///
/// Everything here is fixed before any interpretation happens and none of it changes for the
/// rest of the turn — which is the property that makes it a section rather than a scratchpad.
/// A later stage may read it; nothing may rewrite it.
/// </summary>
public sealed record TurnAdmissionResult
{
    /// <summary>The conversation this turn belongs to, already proven to belong to the user.</summary>
    public required Conversation Conversation { get; init; }

    /// <summary>The stored user message. Its id is the turn's evidence identity.</summary>
    public required Message UserMessage { get; init; }

    /// <summary>The single instant the turn is anchored to.</summary>
    public required DateTimeOffset Now { get; init; }

    /// <summary>
    /// When the user was last heard from, read BEFORE this message was stored so the gap
    /// describes the actual absence rather than this very turn.
    /// </summary>
    public required DateTimeOffset? LastSeenBefore { get; init; }

    /// <summary>
    /// A clarification already awaiting an answer in this conversation, if any. Its presence
    /// is what diverts the turn away from being treated as a new request.
    /// </summary>
    public PendingClarification? Pending { get; init; }
}

/// <summary>
/// The first stage of a turn: prove the request is admissible, fix the turn's identity, and
/// establish the metadata everything downstream reads.
///
/// It owns exactly what <c>Companion.RespondAsync</c> already did before any interpretation
/// began, in the same order, with the same failure behaviour. It does NOT own retrieval,
/// working context, intent, frames, planning, tools, prompting, rendering, post-turn effects
/// or shadow recording — several of which sit immediately after it in the same method and
/// deliberately stayed there.
///
/// Privacy classification is not here either. It currently runs inside the turn body, after
/// the conversation is resolved, and moving it forward would change when a sensitive turn is
/// recognised relative to storage. This extraction moves code; it does not relocate policy.
/// </summary>
public sealed class TurnAdmission(
    IConversationStore conversations,
    IPendingClarificationStore pending,
    TimeProvider clock)
{
    /// <summary>
    /// Admits one turn, in the order the turn has always used.
    ///
    /// Throws <see cref="ArgumentException"/> on an empty message and
    /// <see cref="ConversationNotFoundException"/> on a conversation that does not exist or
    /// does not belong to this user — both exactly as before, because a missing conversation
    /// is an invalid request rather than an invitation to create one.
    /// </summary>
    public async Task<TurnAdmissionResult> AdmitAsync(
        string userId, Guid conversationId, string userMessage, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            throw new ArgumentException("User message must not be empty.", nameof(userMessage));

        // The conversation must exist and belong to this user before ANY work happens — no
        // message storage, retrieval, generation, extraction, or project/open-loop mutation
        // on an unknown or foreign conversation.
        var conversation = await conversations.GetConversationAsync(conversationId, userId, ct)
            ?? throw new ConversationNotFoundException(conversationId);

        var now = clock.GetUtcNow();

        // Read before this message is stored, or the gap it reports includes this turn.
        var lastSeenBefore = await conversations.GetLastMessageAtAsync(userId, ct);

        // Raw storage is unconditional. A private turn skips durable DERIVED memory, which is
        // a later gate; it does not skip the transcript.
        var stored = await StoreUserMessageAsync(userId, conversationId, userMessage, now, ct);

        return new TurnAdmissionResult
        {
            Conversation = conversation,
            UserMessage = stored,
            Now = now,
            LastSeenBefore = lastSeenBefore,
            Pending = await pending.GetActiveAsync(userId, conversationId, ct),
        };
    }

    private async Task<Message> StoreUserMessageAsync(
        string userId, Guid conversationId, string content, DateTimeOffset timestamp,
        CancellationToken ct)
    {
        var message = new Message
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            UserId = userId,
            Role = MessageRole.User,
            Content = content,
            ReplyToId = null,
            TokenCount = Services.ContextAssembler.EstimateTokens(content),
            Timestamp = timestamp,
        };
        await conversations.AddMessageAsync(message, ct);
        return message;
    }
}
