using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Turns.Admission;
using Companion.Infrastructure.Seeding;
using Companion.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Phase B. The extracted first stage of a turn.
///
/// These pin the behaviour that was already there, at its new address: an extraction is only
/// safe if the tests describe what the code did, not what it ought to do. Nothing here is a
/// new rule — the ordering, the exceptions, and the unconditional storage of the raw message
/// all predate the move.
/// </summary>
public class TurnAdmissionTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static string User => CompanionSeeder.DemoUserId;

    private static async Task<(TestHost Host, Guid ConversationId)> HostAsync()
    {
        var host = new TestHost(Now);
        using var seed = host.CreateScope();
        var conversation = await seed.ServiceProvider.GetRequiredService<IConversationStore>()
            .StartConversationAsync(User, "t", "mock", "test");
        return (host, conversation.Id);
    }

    // ---- success ---------------------------------------------------------------------------

    [Fact]
    public async Task AdmitsAValidTurn_AndFixesItsIdentity()
    {
        var (host, conversationId) = await HostAsync();
        await using var _ = host;
        using var scope = host.CreateScope();
        var admission = scope.ServiceProvider.GetRequiredService<TurnAdmission>();

        var result = await admission.AdmitAsync(User, conversationId, "What did we decide?");

        Assert.Equal(conversationId, result.Conversation.Id);
        Assert.Equal(User, result.UserMessage.UserId);
        Assert.Equal(MessageRole.User, result.UserMessage.Role);
        Assert.Equal("What did we decide?", result.UserMessage.Content);
        Assert.NotEqual(Guid.Empty, result.UserMessage.Id);
        Assert.Equal(Now, result.Now);
        Assert.Null(result.Pending);
    }

    [Fact]
    public async Task TheRawMessageIsStoredUnconditionally()
    {
        // Storage is not privacy-conditional. A sensitive turn skips durable DERIVED memory,
        // which is a later gate; it does not skip the transcript.
        var (host, conversationId) = await HostAsync();
        await using var _ = host;
        using var scope = host.CreateScope();

        await scope.ServiceProvider.GetRequiredService<TurnAdmission>()
            .AdmitAsync(User, conversationId, "Keep this private: something sensitive.");

        var db = scope.ServiceProvider
            .GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();
        Assert.Single(await db.Messages.AsNoTracking()
            .Where(m => m.Role == MessageRole.User).ToListAsync());
    }

    [Fact]
    public async Task TheTemporalAnchorIsReadBeforeThisMessageIsStored()
    {
        // The whole point of reading it early: the gap must describe the previous absence,
        // not be reset to zero by the turn that is asking about it.
        var (host, conversationId) = await HostAsync();
        await using var _ = host;
        using var scope = host.CreateScope();
        var admission = scope.ServiceProvider.GetRequiredService<TurnAdmission>();

        var first = await admission.AdmitAsync(User, conversationId, "first");
        Assert.Null(first.LastSeenBefore);              // nothing said before this

        var second = await admission.AdmitAsync(User, conversationId, "second");
        Assert.Equal(first.UserMessage.Timestamp, second.LastSeenBefore);
    }

    // ---- failure ---------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task AnEmptyMessageIsRejected_BeforeAnythingIsStored(string message)
    {
        var (host, conversationId) = await HostAsync();
        await using var _ = host;
        using var scope = host.CreateScope();

        await Assert.ThrowsAsync<ArgumentException>(() => scope.ServiceProvider
            .GetRequiredService<TurnAdmission>()
            .AdmitAsync(User, conversationId, message));

        var db = scope.ServiceProvider
            .GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();
        Assert.Empty(await db.Messages.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task AnUnknownConversationIsRejected_AndStoresNothing()
    {
        var (host, _unused) = await HostAsync();
        await using var _ = host;
        using var scope = host.CreateScope();

        await Assert.ThrowsAsync<ConversationNotFoundException>(() => scope.ServiceProvider
            .GetRequiredService<TurnAdmission>()
            .AdmitAsync(User, Guid.NewGuid(), "hello"));

        var db = scope.ServiceProvider
            .GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();
        Assert.Empty(await db.Messages.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task AnotherUsersConversationIsRejected_NotSilentlyAdopted()
    {
        var (host, conversationId) = await HostAsync();
        await using var _ = host;
        using var scope = host.CreateScope();

        // A foreign conversation is an invalid request, never an invitation to create one.
        await Assert.ThrowsAsync<ConversationNotFoundException>(() => scope.ServiceProvider
            .GetRequiredService<TurnAdmission>()
            .AdmitAsync("usr-someone-else", conversationId, "hello"));

        var db = scope.ServiceProvider
            .GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();
        Assert.Empty(await db.Messages.AsNoTracking().ToListAsync());
    }

    // ---- admission checks --------------------------------------------------------------------

    [Fact]
    public async Task APendingClarificationIsSurfaced_SoTheTurnCanBeDiverted()
    {
        var (host, conversationId) = await HostAsync();
        await using var _ = host;
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;

        var first = await sp.GetRequiredService<TurnAdmission>()
            .AdmitAsync(User, conversationId, "the shed");

        await sp.GetRequiredService<IPendingClarificationStore>().AddAsync(new PendingClarification
        {
            Id = Guid.NewGuid(),
            UserId = User,
            ConversationId = conversationId,
            OriginalMessageId = first.UserMessage.Id,
            OriginalText = "the shed",
            Question = "Which shed?",
            CreatedAt = Now,
        });

        var second = await sp.GetRequiredService<TurnAdmission>()
            .AdmitAsync(User, conversationId, "the garden one");

        Assert.NotNull(second.Pending);
        Assert.Equal("Which shed?", second.Pending!.Question);
    }

    [Fact]
    public async Task APendingClarificationInAnotherConversation_DoesNotDivertThisOne()
    {
        var (host, conversationId) = await HostAsync();
        await using var _ = host;
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;

        var other = await sp.GetRequiredService<IConversationStore>()
            .StartConversationAsync(User, "other", "mock", "test");
        await sp.GetRequiredService<IPendingClarificationStore>().AddAsync(new PendingClarification
        {
            Id = Guid.NewGuid(), UserId = User, ConversationId = other.Id,
            OriginalMessageId = Guid.NewGuid(), OriginalText = "the shed",
            Question = "Which shed?", CreatedAt = Now,
        });

        var result = await sp.GetRequiredService<TurnAdmission>()
            .AdmitAsync(User, conversationId, "unrelated");

        Assert.Null(result.Pending);
    }

    [Fact]
    public async Task EachAdmissionProducesADistinctTurnIdentity()
    {
        var (host, conversationId) = await HostAsync();
        await using var _ = host;
        using var scope = host.CreateScope();
        var admission = scope.ServiceProvider.GetRequiredService<TurnAdmission>();

        var a = await admission.AdmitAsync(User, conversationId, "same words");
        var b = await admission.AdmitAsync(User, conversationId, "same words");

        // Identical text, two turns, two identities — which is what makes exact-identity
        // forgetting able to tell them apart.
        Assert.NotEqual(a.UserMessage.Id, b.UserMessage.Id);
    }

    // ---- the result is a typed section, not a bag ---------------------------------------------

    [Fact]
    public void TheResultIsTypedThroughout()
    {
        var properties = typeof(TurnAdmissionResult).GetProperties();

        Assert.NotEmpty(properties);
        // No dictionary, no object, no dynamic: every section is a named type.
        Assert.DoesNotContain(properties, p =>
            p.PropertyType == typeof(object)
            || (p.PropertyType.IsGenericType
                && p.PropertyType.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            || typeof(System.Collections.IDictionary).IsAssignableFrom(p.PropertyType));
    }

    [Fact]
    public void AdmissionOwnsOnlyItsOwnDependencies()
    {
        // It must not have acquired retrieval, planning, tools, rendering or shadow on the
        // way out of the turn method.
        var parameters = typeof(TurnAdmission).GetConstructors().Single().GetParameters();

        Assert.Equal(3, parameters.Length);
        foreach (var forbidden in new[]
                 {
                     "IRetriever", "IContextAssembler", "IReplyGenerator", "IRendererShadow",
                     "IShadowRecorder", "IMemoryPipeline", "IFrameSessionStore", "ToolLoop",
                 })
            Assert.DoesNotContain(parameters, p => p.ParameterType.Name == forbidden);
    }

    // ---- the real turn still behaves identically ----------------------------------------------

    [Fact]
    public async Task ARealTurn_StoresAndDisplaysTheSameReply()
    {
        var (host, conversationId) = await HostAsync();
        await using var _ = host;

        string reply;
        using (var scope = host.CreateScope())
        {
            var trace = await scope.ServiceProvider.GetRequiredService<ICompanion>()
                .RespondAsync(User, conversationId, "What did we decide about the shed?");
            Assert.Equal(TurnStatus.Answered, trace.Status);
            reply = trace.Response;
        }

        using var read = host.CreateScope();
        var db = read.ServiceProvider
            .GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();
        var stored = await db.Messages.AsNoTracking()
            .Where(m => m.Role == MessageRole.Assistant)
            .OrderByDescending(m => m.Timestamp)
            .FirstAsync();

        Assert.Equal(reply, stored.Content);
        Assert.False(string.IsNullOrWhiteSpace(reply));
    }
}
