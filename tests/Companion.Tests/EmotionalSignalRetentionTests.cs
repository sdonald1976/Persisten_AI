using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Companion.Infrastructure.Seeding;
using Companion.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Source 4 Phase 0 acceptance evidence (docs/SOURCE4_PHASE0_PLAN.md): the 9 declared cases
/// and 6 pass criteria for the EmotionalSignal retention repair, fixed before the code ran.
///
/// The whole point is that forgetting travels by EXACT IDENTITY. Cases 2 and 3 are the
/// adversarial ones: signals whose cue text overlaps, or is byte-identical, must be
/// unaffected unless their own id was forgotten.
/// </summary>
public class EmotionalSignalRetentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private const string User = CompanionSeeder.DemoUserId;
    private const string Other = "usr-someone-else";

    private static EmotionalSignal Signal(
        string userId, Guid messageId, string evidence, string? topic = "the interview",
        DateTimeOffset? at = null, Guid? eventId = null)
        => new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            MessageId = messageId,
            EvidenceEventId = eventId ?? Guid.NewGuid(),
            EvidenceKind = "user-message",
            Timestamp = at ?? Now,
            Sentiment = Sentiment.Negative,
            Valence = -0.6,
            Label = "stressed",
            Evidence = evidence,
            Topic = topic,
        };

    private static async Task<IEmotionStore> StoreAsync(TestHost host, params EmotionalSignal[] signals)
    {
        var store = host.Services.CreateScope().ServiceProvider.GetRequiredService<IEmotionStore>();
        foreach (var s in signals)
            await store.AddSignalAsync(s);
        return store;
    }

    private static async Task<EmotionalSignal?> RowAsync(TestHost host, Guid id)
    {
        using var scope = host.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<Infrastructure.Persistence.CompanionDbContext>()
            .EmotionalSignals.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id);
    }

    // ---- case 1: the basic redaction ---------------------------------------------------

    [Fact]
    public async Task Case1_ForgettingTheEvidenceMessage_LeavesOnlyATombstone()
    {
        await using var host = new TestHost(Now);
        var messageId = Guid.NewGuid();
        var signal = Signal(User, messageId, "I am so stressed about the interview");
        var store = await StoreAsync(host, signal);

        Assert.Equal(1, await store.ForgetByEvidenceAsync(User, [messageId], [], Now));

        var row = await RowAsync(host, signal.Id);
        Assert.NotNull(row);
        Assert.True(row!.EvidenceForgotten);
        Assert.Equal(Now, row.ForgottenAt);
        // The user's words are gone, and so is every reading OF them. The 2026-08-25
        // amendment corrected this: a sentiment bucket, a valence and a lexicon label are
        // semantic derivatives of the forgotten sentence, not neutral metadata.
        // Full coverage lives in EmotionalSignalTombstoneTests.
        Assert.Null(row.Evidence);
        Assert.Null(row.Topic);
        Assert.Null(row.Sentiment);
        Assert.Null(row.Valence);
        Assert.Null(row.Label);
        // What stays is the tombstone: identifiers, status, operational timestamps.
        Assert.Equal(Now, row.Timestamp);
    }

    // ---- cases 2 + 3: the adversarial ones ---------------------------------------------

    [Fact]
    public async Task Case2_OverlappingCueText_DoesNotRedactTheUnforgottenSignal()
    {
        await using var host = new TestHost(Now);
        var forgotten = Guid.NewGuid();
        var kept = Guid.NewGuid();
        var a = Signal(User, forgotten, "I am so stressed about the interview");
        var b = Signal(User, kept, "I am so stressed about the interview on Thursday");
        var store = await StoreAsync(host, a, b);

        Assert.Equal(1, await store.ForgetByEvidenceAsync(User, [forgotten], [], Now));

        Assert.True((await RowAsync(host, a.Id))!.EvidenceForgotten);
        var survivor = await RowAsync(host, b.Id);
        Assert.False(survivor!.EvidenceForgotten);
        Assert.Equal("I am so stressed about the interview on Thursday", survivor.Evidence);
    }

    [Fact]
    public async Task Case3_ByteIdenticalCueText_FromADifferentMessage_IsUntouched()
    {
        await using var host = new TestHost(Now);
        var forgotten = Guid.NewGuid();
        var kept = Guid.NewGuid();
        const string same = "I am so stressed about the interview";
        var a = Signal(User, forgotten, same);
        var b = Signal(User, kept, same);
        var store = await StoreAsync(host, a, b);

        Assert.Equal(1, await store.ForgetByEvidenceAsync(User, [forgotten], [], Now));

        // Identical text, different evidence: text similarity is never consulted.
        Assert.True((await RowAsync(host, a.Id))!.EvidenceForgotten);
        var survivor = await RowAsync(host, b.Id);
        Assert.False(survivor!.EvidenceForgotten);
        Assert.Equal(same, survivor.Evidence);
    }

    // ---- cases 4 + 5: missing evidence --------------------------------------------------

    [Fact]
    public async Task Case4_ForgettingWithNoEvidenceIds_RedactsNothing()
    {
        await using var host = new TestHost(Now);
        var signal = Signal(User, Guid.NewGuid(), "I am so stressed");
        var store = await StoreAsync(host, signal);

        Assert.Equal(0, await store.ForgetByEvidenceAsync(User, [], [], Now));
        Assert.False((await RowAsync(host, signal.Id))!.EvidenceForgotten);
    }

    [Fact]
    public async Task Case5_ASignalWhoseEvidenceWasNotForgotten_IsUntouched()
    {
        await using var host = new TestHost(Now);
        var signal = Signal(User, Guid.NewGuid(), "I am so stressed");
        var store = await StoreAsync(host, signal);

        Assert.Equal(0, await store.ForgetByEvidenceAsync(User, [Guid.NewGuid()], [Guid.NewGuid()], Now));
        var row = await RowAsync(host, signal.Id);
        Assert.False(row!.EvidenceForgotten);
        Assert.Equal("I am so stressed", row.Evidence);
    }

    // ---- case 6: idempotence -----------------------------------------------------------

    [Fact]
    public async Task Case6_ForgettingTwice_IsIdempotent_AndDoesNotRecount()
    {
        await using var host = new TestHost(Now);
        var messageId = Guid.NewGuid();
        var signal = Signal(User, messageId, "I am so stressed");
        var store = await StoreAsync(host, signal);

        Assert.Equal(1, await store.ForgetByEvidenceAsync(User, [messageId], [], Now));
        var firstForgottenAt = (await RowAsync(host, signal.Id))!.ForgottenAt;

        Assert.Equal(0, await store.ForgetByEvidenceAsync(User, [messageId], [], Now.AddDays(1)));
        Assert.Equal(firstForgottenAt, (await RowAsync(host, signal.Id))!.ForgottenAt);
    }

    // ---- case 7: cross-user isolation ---------------------------------------------------

    [Fact]
    public async Task Case7_ForgettingForOneUser_NeverTouchesAnother()
    {
        await using var host = new TestHost(Now);
        // The same evidence ids under two users — the pathological case for a query that
        // forgot to scope by user.
        var messageId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var mine = Signal(User, messageId, "I am so stressed", eventId: eventId);
        var theirs = Signal(Other, messageId, "I am so stressed", eventId: eventId);
        var store = await StoreAsync(host, mine, theirs);

        Assert.Equal(1, await store.ForgetByEvidenceAsync(User, [messageId], [eventId], Now));

        Assert.True((await RowAsync(host, mine.Id))!.EvidenceForgotten);
        var others = await RowAsync(host, theirs.Id);
        Assert.False(others!.EvidenceForgotten);
        Assert.Equal("I am so stressed", others.Evidence);
    }

    // ---- case 8: simultaneous conversations ---------------------------------------------

    [Fact]
    public async Task Case8_TwoSimultaneousConversations_OnlyTheForgottenOnesSignalIsRedacted()
    {
        await using var host = new TestHost(Now);
        // Signals are user-scoped, not conversation-scoped, so the isolation that matters is
        // per-evidence: forgetting a message from one conversation must not reach the other.
        var conversationA = Guid.NewGuid();
        var conversationB = Guid.NewGuid();
        var a = Signal(User, conversationA, "the deadline is crushing me", "the deadline");
        var b = Signal(User, conversationB, "the deadline is crushing me", "the deadline");
        var store = await StoreAsync(host, a, b);

        Assert.Equal(1, await store.ForgetByEvidenceAsync(User, [conversationA], [], Now));

        Assert.True((await RowAsync(host, a.Id))!.EvidenceForgotten);
        Assert.False((await RowAsync(host, b.Id))!.EvidenceForgotten);
    }

    // ---- case 9: the declared retention lifecycle ---------------------------------------

    [Fact]
    public async Task Case9_TheRetentionSweep_DeletesOnlyRowsPastTheDeclaredWindow()
    {
        await using var host = new TestHost(Now);
        var old = Signal(User, Guid.NewGuid(), "ancient worry",
            at: Now - SleepCycle.EmotionalSignalRetention - TimeSpan.FromDays(1));
        var fresh = Signal(User, Guid.NewGuid(), "current worry",
            at: Now - TimeSpan.FromDays(1));
        var store = await StoreAsync(host, old, fresh);

        Assert.Equal(1, await store.PruneAsync(Now - SleepCycle.EmotionalSignalRetention));

        Assert.Null(await RowAsync(host, old.Id));
        Assert.NotNull(await RowAsync(host, fresh.Id));
    }

    // ---- pass criteria that span the cases ----------------------------------------------

    /// <summary>Criterion 1: the forget path cannot compare text — it takes no strings.</summary>
    [Fact]
    public void Criterion1_TheForgetSignature_AcceptsIdentitiesOnly_NeverText()
    {
        var method = typeof(IEmotionStore).GetMethod(nameof(IEmotionStore.ForgetByEvidenceAsync))!;
        var collectionParams = method.GetParameters()
            .Where(p => p.ParameterType.IsGenericType)
            .ToList();

        Assert.Equal(2, collectionParams.Count);
        Assert.All(collectionParams, p =>
            Assert.Equal(typeof(Guid), p.ParameterType.GetGenericArguments()[0]));
        // The only string is the user id, which is the isolation scope, not a matcher.
        Assert.Single(method.GetParameters().Where(p => p.ParameterType == typeof(string)));
    }

    /// <summary>Criterion 3: a redacted signal contributes nothing to the snapshot.</summary>
    [Fact]
    public async Task Criterion3_ARedactedSignal_ContributesNothingToTheRelationshipSnapshot()
    {
        await using var host = new TestHost(Now);
        var messageId = Guid.NewGuid();
        var signal = Signal(User, messageId, "I am devastated about the layoffs", "the layoffs");
        signal.Valence = -0.9;
        signal.Sentiment = Sentiment.VeryNegative;
        var store = await StoreAsync(host, signal);

        using (var scope = host.CreateScope())
        {
            var before = await scope.ServiceProvider
                .GetRequiredService<IRelationshipTracker>().BuildAsync(User);
            Assert.True(before.HasHistory);
        }

        await store.ForgetByEvidenceAsync(User, [messageId], [], Now);

        using (var scope = host.CreateScope())
        {
            var after = await scope.ServiceProvider
                .GetRequiredService<IRelationshipTracker>().BuildAsync(User);
            // Not "a neutral reading" — no history at all. The authority died with the evidence.
            Assert.False(after.HasHistory);
            Assert.Null(after.Describe());
        }
    }

    /// <summary>Criterion 2, end to end: the REAL /forget path redacts by identity.</summary>
    [Fact]
    public async Task Criterion2_TheRealForgetPath_RedactsTheSignalTakenFromThatMessage()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var memories = sp.GetRequiredService<IMemoryStore>();
        var conversations = sp.GetRequiredService<IConversationStore>();

        var conversation = await conversations.StartConversationAsync(User, "t", "mock", "test");
        var message = new Message
        {
            Id = Guid.NewGuid(),
            UserId = User,
            ConversationId = conversation.Id,
            Role = MessageRole.User,
            Content = "I am so stressed about the interview",
            Timestamp = Now,
        };
        await conversations.AddMessageAsync(message);

        var memoryId = Guid.NewGuid();
        await memories.AddSemanticAsync(new SemanticMemory
        {
            Id = memoryId,
            UserId = User,
            Subject = "user",
            Predicate = "feels",
            Value = "stressed about the interview",
            NormalizedFact = "The user is stressed about the interview.",
            FirstObserved = Now,
            LastConfirmed = Now,
            CreatedAt = Now,
        });
        await memories.AddEvidenceAsync(User,
        [
            new MemoryEvidence
            {
                Id = Guid.NewGuid(),
                UserId = User,
                MemoryId = memoryId,
                MemoryKind = MemoryKind.Semantic,
                MessageId = message.Id,
                Excerpt = "I am so stressed about the interview",
            },
        ]);

        var signal = Signal(User, message.Id, "I am so stressed about the interview");
        await sp.GetRequiredService<IEmotionStore>().AddSignalAsync(signal);

        Assert.True(await sp.GetRequiredService<IMemoryCurator>()
            .ForgetAsync(User, memoryId, "user asked to forget"));

        var row = await RowAsync(host, signal.Id);
        Assert.True(row!.EvidenceForgotten);
        Assert.Null(row.Evidence);
        Assert.Null(row.Topic);
    }
}
