using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>Persists and reads conversations and messages. All operations are user-scoped.</summary>
public interface IConversationStore
{
    Task<Conversation> StartConversationAsync(
        string userId, string? title, string? modelUsed, string? source, CancellationToken ct = default);

    Task AddMessageAsync(Message message, CancellationToken ct = default);

    /// <summary>Most recent messages in a conversation, returned oldest-first.</summary>
    Task<IReadOnlyList<Message>> GetRecentMessagesAsync(
        Guid conversationId, string userId, int count, CancellationToken ct = default);

    Task<Message?> GetMessageAsync(Guid messageId, string userId, CancellationToken ct = default);
}
