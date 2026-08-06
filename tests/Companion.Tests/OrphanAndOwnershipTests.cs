using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Persistence/turn-level guarantees for H2/H3: a message can only belong to an existing,
/// owned conversation, and a missing/foreign conversation is an invalid request (never a silent
/// non-private turn), producing no side effects.
/// </summary>
public class OrphanAndOwnershipTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private const string UserA = "user-a";
    private const string UserB = "user-b";

    private static Message Msg(Guid conversationId, string userId, string content) => new()
    {
        Id = Guid.NewGuid(), ConversationId = conversationId, UserId = userId,
        Role = MessageRole.User, Content = content, Timestamp = Now,
    };

    [Fact]
    public async Task AddMessage_ToUnknownConversation_Throws_AndStoresNothing()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var conversations = scope.ServiceProvider.GetRequiredService<IConversationStore>();

        var orphanConv = Guid.NewGuid();
        await Assert.ThrowsAsync<ConversationNotFoundException>(
            () => conversations.AddMessageAsync(Msg(orphanConv, UserA, "hi")));

        Assert.Empty(await conversations.GetRecentMessagesAsync(orphanConv, UserA, 10));
    }

    [Fact]
    public async Task AddMessage_ToForeignConversation_Throws()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var conversations = scope.ServiceProvider.GetRequiredService<IConversationStore>();

        var convA = (await conversations.StartConversationAsync(UserA, "a", "mock", "test")).Id;

        // User B tries to post into User A's conversation.
        await Assert.ThrowsAsync<ConversationNotFoundException>(
            () => conversations.AddMessageAsync(Msg(convA, UserB, "sneaky")));
    }

    [Fact]
    public async Task Respond_ToUnknownConversation_Throws_WithNoSideEffects()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var companion = sp.GetRequiredService<ICompanion>();

        await Assert.ThrowsAsync<ConversationNotFoundException>(
            () => companion.RespondAsync(UserA, Guid.NewGuid(), "I love hiking."));

        // No memory, no message leaked into being.
        Assert.Empty(await sp.GetRequiredService<IMemoryStore>().GetRetrievableMemoriesAsync(UserA));
    }

    [Fact]
    public async Task Respond_ToForeignConversation_Throws()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var convA = (await sp.GetRequiredService<IConversationStore>()
            .StartConversationAsync(UserA, "a", "mock", "test")).Id;

        await Assert.ThrowsAsync<ConversationNotFoundException>(
            () => sp.GetRequiredService<ICompanion>().RespondAsync(UserB, convA, "hello"));
    }

    [Fact]
    public async Task DoNotRemember_OnForeignConversation_IsANoOp()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var conversations = scope.ServiceProvider.GetRequiredService<IConversationStore>();

        var convA = (await conversations.StartConversationAsync(UserA, "a", "mock", "test")).Id;

        // User B tries to flip A's conversation to private.
        await conversations.SetDoNotRememberAsync(convA, UserB, true);

        var stillA = await conversations.GetConversationAsync(convA, UserA);
        Assert.False(stillA!.DoNotRemember); // unchanged — B can't touch A's conversation
    }
}
