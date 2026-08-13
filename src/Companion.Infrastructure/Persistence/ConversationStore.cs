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
        // Refuse orphan inserts at the persistence boundary: a message may only be stored into a
        // conversation that exists AND is owned by the same user. This is enforced here (not only
        // in the API) so no code path can create an orphan or cross-user message.
        var conversation = await _db.Conversations
            .FirstOrDefaultAsync(c => c.Id == message.ConversationId && c.UserId == message.UserId, ct);
        if (conversation is null)
            throw new ConversationNotFoundException(message.ConversationId);

        _db.Messages.Add(message);
        conversation.LastActivityAt = message.Timestamp;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<Conversation?> GetConversationAsync(
        Guid conversationId, string userId, CancellationToken ct = default)
        => await _db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId && c.UserId == userId, ct);

    public async Task<Conversation?> GetResumableConversationAsync(
        string userId, string source, DateTimeOffset activeSince, CancellationToken ct = default)
        => await _db.Conversations
            .Where(c => c.UserId == userId
                && c.Source == source
                && !c.DoNotRemember
                && c.LastActivityAt >= activeSince)
            .OrderByDescending(c => c.LastActivityAt)
            .FirstOrDefaultAsync(ct);

    public async Task SetDoNotRememberAsync(
        Guid conversationId, string userId, bool value, CancellationToken ct = default)
    {
        var conversation = await _db.Conversations
            .FirstOrDefaultAsync(c => c.Id == conversationId && c.UserId == userId, ct);
        if (conversation is null)
            return;
        conversation.DoNotRemember = value;
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

    public async Task<DateTimeOffset?> GetLastMessageAtAsync(string userId, CancellationToken ct = default)
    {
        var any = await _db.Messages
            .Where(m => m.UserId == userId)
            .OrderByDescending(m => m.Timestamp)
            .Select(m => (DateTimeOffset?)m.Timestamp)
            .FirstOrDefaultAsync(ct);
        return any;
    }

    public async Task<DateTimeOffset?> GetFirstMessageAtAsync(string userId, CancellationToken ct = default)
        => await _db.Messages
            .Where(m => m.UserId == userId && m.Role == MessageRole.User)
            .OrderBy(m => m.Timestamp)
            .Select(m => (DateTimeOffset?)m.Timestamp)
            .FirstOrDefaultAsync(ct);

    public async Task<int> CountUserMessagesAsync(string userId, CancellationToken ct = default)
        => await _db.Messages.CountAsync(m => m.UserId == userId && m.Role == MessageRole.User, ct);

    public async Task<IReadOnlyList<Message>> GetRememberableMessagesSinceAsync(
        string userId, DateTimeOffset? after, int max, CancellationToken ct = default)
    {
        if (max <= 0)
            return Array.Empty<Message>();

        // The privacy gate lives in the query itself: a do-not-remember conversation's messages
        // can't reach the reflection pass through any call site.
        var query =
            from m in _db.Messages
            join c in _db.Conversations on m.ConversationId equals c.Id
            where m.UserId == userId && c.UserId == userId && !c.DoNotRemember
            select m;

        if (after is { } cutoff)
            query = query.Where(m => m.Timestamp > cutoff);

        // Newest N so a long backlog keeps the freshest material, then chronological for reading.
        var newestDesc = await query
            .OrderByDescending(m => m.Timestamp)
            .ThenByDescending(m => m.Id)
            .Take(max)
            .ToListAsync(ct);

        newestDesc.Reverse();
        return newestDesc;
    }
}
