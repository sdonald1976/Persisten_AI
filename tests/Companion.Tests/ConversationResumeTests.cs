using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Infrastructure.Seeding;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// A connection is not a conversation. The web client reconnects about a second after any drop —
/// a sleeping laptop, a wifi blip, a server restart — and recent-turn context is scoped to a
/// single conversation. Opening a fresh one per socket meant the companion lost the thread
/// mid-exchange: asked "are you ready?", told "I am ready", and answered "ready for what?".
///
/// Resuming has to stay inside two boundaries: never another user's thread, and never a
/// conversation the user made private.
/// </summary>
public class ConversationResumeTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
    private const string User = CompanionSeeder.DemoUserId;

    private static IConversationStore Store(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IConversationStore>();

    private static async Task<Message> SayAsync(
        IConversationStore store, Guid conversationId, string text, DateTimeOffset at, string userId = User)
    {
        var message = new Message
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            UserId = userId,
            Role = MessageRole.User,
            Content = text,
            Timestamp = at,
        };
        await store.AddMessageAsync(message);
        return message;
    }

    [Fact]
    public async Task AReconnect_RejoinsTheThreadItWasIn()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var store = Store(scope);

        var first = await store.StartConversationAsync(User, "WebSocket session", null, "ws");
        await SayAsync(store, first.Id, "I want to tell you something. Are you ready?", Now);

        // The socket drops and the client reconnects seconds later.
        var resumed = await store.GetResumableConversationAsync(User, "ws", Now.AddHours(-12));

        Assert.NotNull(resumed);
        Assert.Equal(first.Id, resumed!.Id);

        // …and the question is still in scope, which is the whole point.
        var recent = await store.GetRecentMessagesAsync(resumed.Id, User, 10);
        Assert.Contains(recent, m => m.Content.Contains("Are you ready?"));
    }

    [Fact]
    public async Task TheMostRecentThread_Wins()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var store = Store(scope);

        var older = await store.StartConversationAsync(User, "WebSocket session", null, "ws");
        await SayAsync(store, older.Id, "yesterday's thread", Now.AddHours(-5));
        var newer = await store.StartConversationAsync(User, "WebSocket session", null, "ws");
        await SayAsync(store, newer.Id, "this evening's thread", Now.AddMinutes(-2));

        var resumed = await store.GetResumableConversationAsync(User, "ws", Now.AddHours(-12));

        Assert.Equal(newer.Id, resumed?.Id);
    }

    [Fact]
    public async Task AThreadOlderThanTheWindow_IsNotResumed()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var store = Store(scope);

        var stale = await store.StartConversationAsync(User, "WebSocket session", null, "ws");
        await SayAsync(store, stale.Id, "last week", Now.AddDays(-7));

        Assert.Null(await store.GetResumableConversationAsync(User, "ws", Now.AddHours(-12)));
    }

    [Fact]
    public async Task APrivateConversation_IsNeverSilentlyRejoined()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var store = Store(scope);

        var priv = await store.StartConversationAsync(User, "WebSocket session", null, "ws");
        await SayAsync(store, priv.Id, "just between us", Now);
        await store.SetDoNotRememberAsync(priv.Id, User, true);

        // Privacy is chosen for a session. Resuming it on a later connection would leave the user
        // in private mode without knowing it — the failure mode there is worse than losing context.
        Assert.Null(await store.GetResumableConversationAsync(User, "ws", Now.AddHours(-12)));
    }

    [Fact]
    public async Task AnotherUsersThread_IsNeverResumed()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var store = Store(scope);

        var theirs = await store.StartConversationAsync("someone-else", "WebSocket session", null, "ws");
        await SayAsync(store, theirs.Id, "their private evening", Now, userId: "someone-else");

        Assert.Null(await store.GetResumableConversationAsync(User, "ws", Now.AddHours(-12)));
    }

    [Fact]
    public async Task ADifferentSource_IsNotResumedIntoTheSocket()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var store = Store(scope);

        var api = await store.StartConversationAsync(User, "API session", null, "api");
        await SayAsync(store, api.Id, "a scripted call", Now);

        Assert.Null(await store.GetResumableConversationAsync(User, "ws", Now.AddHours(-12)));
    }

    [Fact]
    public async Task NoPriorThread_MeansNothingToResume()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();

        Assert.Null(await Store(scope).GetResumableConversationAsync(User, "ws", Now.AddHours(-12)));
    }
}
