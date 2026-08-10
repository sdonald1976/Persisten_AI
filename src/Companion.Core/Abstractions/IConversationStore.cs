using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>Persists and reads conversations and messages. All operations are user-scoped.</summary>
public interface IConversationStore
{
    Task<Conversation> StartConversationAsync(
        string userId, string? title, string? modelUsed, string? source, CancellationToken ct = default);

    Task AddMessageAsync(Message message, CancellationToken ct = default);

    /// <summary>The conversation, scoped to its owner (null if not found or not owned).</summary>
    Task<Conversation?> GetConversationAsync(Guid conversationId, string userId, CancellationToken ct = default);

    /// <summary>Sets the privacy flag: when true, the conversation creates no durable derived memory.</summary>
    Task SetDoNotRememberAsync(Guid conversationId, string userId, bool value, CancellationToken ct = default);

    /// <summary>Most recent messages in a conversation, returned oldest-first.</summary>
    Task<IReadOnlyList<Message>> GetRecentMessagesAsync(
        Guid conversationId, string userId, int count, CancellationToken ct = default);

    Task<Message?> GetMessageAsync(Guid messageId, string userId, CancellationToken ct = default);

    /// <summary>
    /// Timestamp of the user's most recent message across all their conversations, or null if they
    /// have never sent one. Used to tell how long it's been since you last talked (time-aware greetings).
    /// </summary>
    Task<DateTimeOffset?> GetLastMessageAtAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// Messages across all the user's conversations strictly newer than <paramref name="after"/>
    /// (or all of them when null), oldest first, capped at <paramref name="max"/>. Excludes every
    /// message in a do-not-remember conversation — private turns are never reflected on, the same
    /// gate that keeps them out of extraction. Feeds the reflection pass.
    /// </summary>
    Task<IReadOnlyList<Message>> GetRememberableMessagesSinceAsync(
        string userId, DateTimeOffset? after, int max, CancellationToken ct = default);
}
