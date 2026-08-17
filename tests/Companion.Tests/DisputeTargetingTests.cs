using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Which memory "that's wrong" is about.
///
/// It used to be whichever was newest, because the dispute path threw the rest of the sentence
/// away before looking. Told "That's wrong actually. The irrigation isn't at the allotment — it's
/// at Marsh Lane", she flagged an unrelated fact she had learned seconds earlier, left the fact
/// being corrected active, and discarded the correction. Getting this wrong is worse than not
/// acting: a true memory is marked unreliable and a false one keeps being asserted.
/// </summary>
public class DisputeTargetingTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private const string User = "dispute-user";

    private static async Task<SemanticMemory> AddFactAsync(
        IServiceProvider sp, string fact, DateTimeOffset createdAt)
    {
        var store = sp.GetRequiredService<IMemoryStore>();
        var embeddings = sp.GetRequiredService<IEmbeddingModel>();
        var m = new SemanticMemory
        {
            Id = Guid.NewGuid(),
            UserId = User,
            Subject = "user",
            Predicate = "works_on",
            Value = fact,
            NormalizedFact = fact,
            Confidence = 0.9,
            Status = MemoryStatus.Active,
            LastConfirmed = createdAt,
            CreatedAt = createdAt,
            Embedding = await embeddings.EmbedAsync(fact),
        };
        await store.AddSemanticAsync(m);
        return m;
    }

    private static async Task<Guid> StartConversationAsync(IServiceProvider sp)
        => (await sp.GetRequiredService<IConversationStore>()
            .StartConversationAsync(User, "t", "mock", "test")).Id;

    /// <summary>The rest of the sentence is the whole point — it has to reach the resolver.</summary>
    [Fact]
    public void TheCorrectionIsKeptAsTheReference()
    {
        var intent = new RuleBasedIntentParser().ParseAsync(
            "That's wrong actually. The irrigation isn't at the allotment - it's at Marsh Lane").Result;

        Assert.Equal(IntentKind.Dispute, intent.Kind);
        Assert.Contains("irrigation", intent.Argument);
    }

    [Fact]
    public async Task NamingTheFact_FlagsThatOne_NotTheNewest()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;

        var irrigation = await AddFactAsync(sp, "The user is rebuilding the greenhouse irrigation.", Now);
        var newest = await AddFactAsync(sp, "The user has a corgi called Kanga.", Now.AddMinutes(5));

        var reply = await sp.GetRequiredService<IAgent>().HandleAsync(
            User, await StartConversationAsync(sp),
            "That's wrong, the greenhouse irrigation is finished.");

        Assert.Equal(IntentKind.Dispute, reply.Intent);

        var store = sp.GetRequiredService<IMemoryStore>();
        Assert.Equal(MemoryStatus.Disputed, (await store.GetSemanticAsync(irrigation.Id, User))!.Status);
        Assert.Equal(MemoryStatus.Active, (await store.GetSemanticAsync(newest.Id, User))!.Status);
    }

    /// <summary>
    /// When the reference fits two memories about equally well, asking is the only safe move —
    /// the same relative-confidence rule the project resolver uses for an ambiguous reference.
    /// Nothing is flagged until the user says which.
    /// </summary>
    [Fact]
    public async Task AnAmbiguousCorrection_AsksInsteadOfGuessing()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;

        var a = await AddFactAsync(sp, "The user is rebuilding the irrigation at the allotment.", Now);
        var b = await AddFactAsync(sp, "The user is building raised beds at Marsh Lane.", Now.AddMinutes(5));

        var reply = await sp.GetRequiredService<IAgent>().HandleAsync(
            User, await StartConversationAsync(sp),
            "That's wrong. The irrigation isn't at the allotment - it's at Marsh Lane.");

        Assert.Contains("Which one", reply.Text);

        var store = sp.GetRequiredService<IMemoryStore>();
        Assert.Equal(MemoryStatus.Active, (await store.GetSemanticAsync(a.Id, User))!.Status);
        Assert.Equal(MemoryStatus.Active, (await store.GetSemanticAsync(b.Id, User))!.Status);
    }

    /// <summary>
    /// A bare "that's wrong" still has to do something, and the newest memory remains the best
    /// available reading of "that".
    /// </summary>
    [Fact]
    public async Task ABareDispute_StillFlagsTheMostRecent()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;

        await AddFactAsync(sp, "The user is rebuilding the greenhouse irrigation.", Now);
        var newest = await AddFactAsync(sp, "The user has a corgi called Kanga.", Now.AddMinutes(5));

        await sp.GetRequiredService<IAgent>().HandleAsync(
            User, await StartConversationAsync(sp), "That's wrong.");

        Assert.Equal(
            MemoryStatus.Disputed,
            (await sp.GetRequiredService<IMemoryStore>().GetSemanticAsync(newest.Id, User))!.Status);
    }
}
