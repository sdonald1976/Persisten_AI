using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Companion.Infrastructure.Persistence;

/// <summary>EF Core-backed conversation/message store. Every query is scoped by userId.</summary>
public sealed class ConversationStore : IConversationStore
{
    private readonly CompanionDbContext _db;
    private readonly TimeProvider _clock;

    public ConversationStore(CompanionDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<Conversation> StartConversationAsync(
        string userId, string? title, string? modelUsed, string? source, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Title = title,
            ModelUsed = modelUsed,
            Source = source,
            StartedAt = now,
            LastActivityAt = now,
        };
        _db.Conversations.Add(conversation);
        await _db.SaveChangesAsync(ct);
        return conversation;
    }

    public async Task AddMessageAsync(Message message, CancellationToken ct = default)
    {
        _db.Messages.Add(message);

        var conversation = await _db.Conversations
            .FirstOrDefaultAsync(c => c.Id == message.ConversationId && c.UserId == message.UserId, ct);
        if (conversation is not null)
            conversation.LastActivityAt = message.Timestamp;

        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<Message>> GetRecentMessagesAsync(
        Guid conversationId, string userId, int count, CancellationToken ct = default)
    {
        var recentDesc = await _db.Messages
            .Where(m => m.ConversationId == conversationId && m.UserId == userId)
            .OrderByDescending(m => m.Timestamp)
            .ThenByDescending(m => m.Id)
            .Take(count)
            .ToListAsync(ct);

        recentDesc.Reverse(); // return oldest-first
        return recentDesc;
    }

    public async Task<Message?> GetMessageAsync(Guid messageId, string userId, CancellationToken ct = default)
        => await _db.Messages.FirstOrDefaultAsync(m => m.Id == messageId && m.UserId == userId, ct);
}
