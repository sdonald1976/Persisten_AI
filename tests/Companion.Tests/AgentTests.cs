using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Tests the brain facade (<see cref="IAgent"/>) — the single surface every face drives the
/// companion through. Verifies intent routing, the forget confirmation handshake, and that
/// feedback is reconstructed from the stored conversation (so the brain stays stateless).
/// </summary>
public class AgentTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private const string User = "agent-user";

    private static async Task<Guid> StartConversationAsync(IServiceProvider sp)
    {
        var conv = await sp.GetRequiredService<IConversationStore>()
            .StartConversationAsync(User, "t", "mock", "test");
        return conv.Id;
    }

    private static async Task<SemanticMemory> AddFactAsync(IServiceProvider sp, string fact)
    {
        var store = sp.GetRequiredService<IMemoryStore>();
        var embeddings = sp.GetRequiredService<IEmbeddingModel>();
        var m = new SemanticMemory
        {
            Id = Guid.NewGuid(),
            UserId = User,
            Subject = "user",
            Predicate = "prefers",
            Value = fact,
            NormalizedFact = fact,
            Confidence = 0.8,
            Status = MemoryStatus.Active,
            LastConfirmed = Now,
            CreatedAt = Now,
            Embedding = await embeddings.EmbedAsync(fact),
        };
        await store.AddSemanticAsync(m);
        return m;
    }

    [Fact]
    public async Task PlainMessage_RoutesToChat_AndCarriesTheTrace()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var conversationId = await StartConversationAsync(sp);

        // A genuinely plain message (a bare greeting like "hello there" is now its own intent).
        var reply = await sp.GetRequiredService<IAgent>()
            .HandleAsync(User, conversationId, "what should we work on today?");

        Assert.Equal(AgentReplyKind.Chat, reply.Kind);
        Assert.Equal(IntentKind.Chat, reply.Intent);
        Assert.NotNull(reply.Trace);
        Assert.False(string.IsNullOrWhiteSpace(reply.Text));
    }

    [Fact]
    public async Task ChatReply_StreamsToTheTokenSink()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var conversationId = await StartConversationAsync(sp);

        var chunks = new List<string>();
        var reply = await sp.GetRequiredService<IAgent>().HandleAsync(
            User, conversationId, "tell me something",
            new Progress<string>(c => chunks.Add(c)));

        Assert.Equal(AgentReplyKind.Chat, reply.Kind);
        // The mock chat model streams; the concatenated chunks reconstruct the reply.
        Assert.NotEmpty(chunks);
        Assert.Equal(reply.Text.Trim(), string.Concat(chunks).Trim());
    }

    [Fact]
    public async Task Recall_RoutesToAnAction_ListingMemories()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var conversationId = await StartConversationAsync(sp);
        await AddFactAsync(sp, "enjoys hiking in the mountains");

        var reply = await sp.GetRequiredService<IAgent>()
            .HandleAsync(User, conversationId, "what do you remember about me?");

        Assert.Equal(AgentReplyKind.Action, reply.Kind);
        Assert.Equal(IntentKind.Recall, reply.Intent);
        Assert.Contains("hiking", reply.Text);
    }

    [Fact]
    public async Task Forget_AsksForConfirmation_ThenForgetsOnYes()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var agent = sp.GetRequiredService<IAgent>();
        var conversationId = await StartConversationAsync(sp);
        await AddFactAsync(sp, "follows a low-carb diet");

        var ask = await agent.HandleAsync(User, conversationId, "forget the low-carb thing");
        Assert.Equal(AgentReplyKind.Confirmation, ask.Kind);
        Assert.Equal(IntentKind.Forget, ask.Intent);
        Assert.False(string.IsNullOrWhiteSpace(ask.ConfirmationToken));

        var done = await agent.ConfirmAsync(User, conversationId, ask.ConfirmationToken!, confirmed: true);
        Assert.Equal(AgentReplyKind.Action, done.Kind);

        var remaining = await sp.GetRequiredService<IMemoryStore>().GetRetrievableMemoriesAsync(User);
        Assert.DoesNotContain(remaining, m => m.Content.Contains("low-carb"));
    }

    [Fact]
    public async Task Forget_LeavesMemoryIntact_OnNo()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var agent = sp.GetRequiredService<IAgent>();
        var conversationId = await StartConversationAsync(sp);
        await AddFactAsync(sp, "follows a low-carb diet");

        var ask = await agent.HandleAsync(User, conversationId, "forget the low-carb thing");
        var kept = await agent.ConfirmAsync(User, conversationId, ask.ConfirmationToken!, confirmed: false);

        Assert.Contains("Kept", kept.Text);
        var remaining = await sp.GetRequiredService<IMemoryStore>().GetRetrievableMemoriesAsync(User);
        Assert.Contains(remaining, m => m.Content.Contains("low-carb"));
    }

    [Fact]
    public async Task AdjustStyle_AppendsToThePersona()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var conversationId = await StartConversationAsync(sp);

        var reply = await sp.GetRequiredService<IAgent>()
            .HandleAsync(User, conversationId, "be more concise");

        Assert.Equal(IntentKind.AdjustStyle, reply.Intent);
        var persona = (await sp.GetRequiredService<IProfileStore>().GetOrCreateAsync(User)).Persona;
        Assert.Contains("concise", persona, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PositiveFeedback_IsReconstructedFromTheConversation_AndStored()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var agent = sp.GetRequiredService<IAgent>();
        var conversationId = await StartConversationAsync(sp);

        // A real turn first, so there's a stored (user, assistant) pair to rate.
        await agent.HandleAsync(User, conversationId, "how's it going?");

        var reply = await agent.HandleAsync(User, conversationId, "that was great");
        Assert.Equal(IntentKind.FeedbackPositive, reply.Intent);

        Assert.Equal(1, await sp.GetRequiredService<IFeedbackStore>().CountAsync(User));
    }

    [Fact]
    public async Task Feedback_WithNothingToRate_DoesNotStore()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var conversationId = await StartConversationAsync(sp);

        var reply = await sp.GetRequiredService<IAgent>()
            .HandleAsync(User, conversationId, "that was great");

        Assert.Equal(IntentKind.FeedbackPositive, reply.Intent);
        Assert.Contains("Nothing to rate", reply.Text);
        Assert.Equal(0, await sp.GetRequiredService<IFeedbackStore>().CountAsync(User));
    }
}
