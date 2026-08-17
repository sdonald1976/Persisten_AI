using Companion.Core;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The store must end a conversation holding what the user actually said — no more and no less.
///
/// Each case here is a transcript that really happened against a running companion with real
/// models, replayed through the pipeline. They are grouped together because they share a cause:
/// every one of them passed through components that were individually correct and individually
/// tested, and the existing suite missed them all by constructing candidates the way the pipeline
/// wished the extraction model behaved rather than the way it does.
/// </summary>
public class MemoryFidelityTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private const string User = "fidelity-user";
    private static readonly Guid Conversation = Guid.NewGuid();

    private static Message UserMsg(string text) => new()
    {
        Id = Guid.NewGuid(),
        ConversationId = Conversation,
        UserId = User,
        Role = MessageRole.User,
        Content = text,
        Timestamp = Now,
    };

    private static MemoryCandidate Fact(
        string predicate, string value, string content, Message source, string excerpt) => new()
        {
            Kind = MemoryKind.Semantic,
            Subject = "user",
            Predicate = predicate,
            Value = value,
            Content = content,
            ProposedConfidence = 0.9,
            Evidence = new[] { new CandidateEvidence(source.Id, excerpt) },
        };

    private static MemoryPipeline BuildPipeline(
        IServiceProvider sp, TimeProvider clock, CompanionOptions options, params MemoryCandidate[] candidates)
    {
        var store = sp.GetRequiredService<IMemoryStore>();
        var embeddings = sp.GetRequiredService<IEmbeddingModel>();
        return new MemoryPipeline(
            new StubExtractor(candidates), store,
            new MemoryCurator(store, embeddings, clock, NullLogger<MemoryCurator>.Instance),
            embeddings,
            sp.GetRequiredService<IProfileStore>(),
            sp.GetRequiredService<IPersonalityService>(),
            Options.Create(options), clock, NullLogger<MemoryPipeline>.Instance);
    }

    /// <summary>
    /// Similarity is deliberately taken out of the way in most of these: the rule under test is
    /// which fact replaces which, and the thresholds are calibrated against the real embedding
    /// model in <see cref="CompanionOptions.ReplacementSimilarityThreshold"/>, where the measured
    /// numbers are recorded. A test that also depended on the mock embedding's geometry would be
    /// measuring the wrong thing.
    /// </summary>
    private static CompanionOptions RuleOnly() => new()
    {
        DuplicateSimilarityThreshold = 0.99,
        ContradictionSimilarityThreshold = 0.0,
        ReplacementSimilarityThreshold = 0.0,
    };

    private static async Task<List<SemanticMemory>> ActiveFactsAsync(IServiceScope scope)
        => (await scope.ServiceProvider.GetRequiredService<IMemoryStore>().GetRetrievableMemoriesAsync(User))
            .OfType<SemanticMemory>()
            .Where(m => m.Status == MemoryStatus.Active)
            .ToList();

    /// <summary>
    /// "Did I ever tell you what timber I bought for the beds?" — she answered "I don't recall you
    /// mentioning that", and stored it as a fact in the same turn. Evidence verification passed
    /// honestly: the words are in a real user message. They are just not a claim.
    /// </summary>
    [Fact]
    public async Task AQuestion_DoesNotBecomeAFact()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();

        var asked = UserMsg("Did I ever tell you what timber I bought for the beds?");
        var pipeline = BuildPipeline(scope.ServiceProvider, host.Clock, RuleOnly(), Fact(
            "buys_materials_for", "raised beds",
            "The user bought timber for the raised beds.", asked, "what timber I bought for the beds"));

        var decision = Assert.Single((await pipeline.ProcessAsync(User, new[] { asked })).Decisions);

        Assert.Equal(MemoryDecisionKind.Rejected, decision.Outcome);
        Assert.Empty(await ActiveFactsAsync(scope));
    }

    /// <summary>
    /// Starting a second project must not delete the first. Both landed in <c>user/works_on</c>, and
    /// the audit trail recorded the loss as "User stated a new value for 'works_on'" — a person can
    /// have two projects, and nothing in the user's words said otherwise.
    /// </summary>
    [Fact]
    public async Task ASecondProject_DoesNotDeleteTheFirst()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();

        var first = UserMsg("I'm rebuilding the greenhouse irrigation before the frost.");
        await BuildPipeline(scope.ServiceProvider, host.Clock, RuleOnly(), Fact(
                "works_on", "greenhouse irrigation",
                "The user is rebuilding the greenhouse irrigation at their allotment.",
                first, "I'm rebuilding the greenhouse irrigation"))
            .ProcessAsync(User, new[] { first });

        var second = UserMsg("I've started a second thing too - a raised-bed build over at the Marsh Lane plot.");
        await BuildPipeline(scope.ServiceProvider, host.Clock, RuleOnly(), Fact(
                "works_on", "raised-bed build at Marsh Lane plot",
                "The user is working on a raised-bed build at the Marsh Lane plot.",
                second, "I've started a second thing too - a raised-bed build"))
            .ProcessAsync(User, new[] { second });

        var active = await ActiveFactsAsync(scope);
        Assert.Equal(2, active.Count);
        Assert.Contains(active, m => m.Value!.Contains("irrigation"));
        Assert.Contains(active, m => m.Value!.Contains("Marsh Lane"));
    }

    /// <summary>
    /// The same rule from the other side: changing a preference must replace it, even though the
    /// extraction model chose an unrelated predicate for the new value ("drinks_coffee_black" then
    /// "prefers"), so the two never shared a slot. The user's own "Actually I've gone off…" is what
    /// carries the meaning, and it survives whatever the model does with the schema.
    /// </summary>
    [Fact]
    public async Task ChangingAPreference_ReplacesIt_EvenAcrossADifferentPredicate()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();

        var stated = UserMsg("I drink my coffee black, no sugar.");
        await BuildPipeline(scope.ServiceProvider, host.Clock, RuleOnly(), Fact(
                "drinks_coffee_black", "no sugar",
                "The user drinks their coffee black without any sugar.",
                stated, "I drink my coffee black, no sugar"))
            .ProcessAsync(User, new[] { stated });

        var changed = UserMsg("Actually I've gone off black coffee. I take oat milk lattes now.");
        var result = await BuildPipeline(scope.ServiceProvider, host.Clock, RuleOnly(), Fact(
                "prefers", "oat milk lattes over black coffee",
                "The user prefers oat milk lattes over black coffee.",
                changed, "Actually I've gone off black coffee. I take oat milk lattes now."))
            .ProcessAsync(User, new[] { changed });

        Assert.Equal(MemoryDecisionKind.Superseded, Assert.Single(result.Decisions).Outcome);

        var active = Assert.Single(await ActiveFactsAsync(scope));
        Assert.Contains("oat milk", active.NormalizedFact);

        // History is kept, not destroyed — the point of superseding rather than deleting.
        var all = await scope.ServiceProvider.GetRequiredService<IMemoryStore>()
            .GetRetrievableMemoriesAsync(User);
        Assert.Contains(all.OfType<SemanticMemory>(),
            m => m.Status == MemoryStatus.Superseded && m.NormalizedFact.Contains("black"));
    }

    /// <summary>
    /// A single-valued fact still replaces without being told to: nobody says "actually" before
    /// correcting their own name, and a person has one.
    /// </summary>
    [Fact]
    public async Task ASingleValuedFact_ReplacesWithoutBeingTold()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();

        var first = UserMsg("I live in Norwich.");
        await BuildPipeline(scope.ServiceProvider, host.Clock, RuleOnly(), Fact(
                "lives_in", "Norwich", "The user lives in Norwich.", first, "I live in Norwich"))
            .ProcessAsync(User, new[] { first });

        var moved = UserMsg("I live in Cambridge.");
        var result = await BuildPipeline(scope.ServiceProvider, host.Clock, RuleOnly(), Fact(
                "lives_in", "Cambridge", "The user lives in Cambridge.", moved, "I live in Cambridge"))
            .ProcessAsync(User, new[] { moved });

        Assert.Equal(MemoryDecisionKind.Superseded, Assert.Single(result.Decisions).Outcome);
        Assert.Contains("Cambridge", Assert.Single(await ActiveFactsAsync(scope)).NormalizedFact);
    }

    /// <summary>
    /// Two dislikes are two dislikes. This is the same shape as the two projects, and the reason
    /// similarity cannot be the deciding signal: measured on the real embedding model, "dislikes
    /// coriander" vs "dislikes olives" (0.753) scores HIGHER than the coffee change that must
    /// replace (0.763 is barely above it), so no threshold separates them.
    /// </summary>
    [Fact]
    public async Task TwoDislikes_BothSurvive()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();

        var first = UserMsg("I don't like coriander.");
        await BuildPipeline(scope.ServiceProvider, host.Clock, RuleOnly(), Fact(
                "dislikes", "coriander", "The user dislikes coriander.", first, "I don't like coriander"))
            .ProcessAsync(User, new[] { first });

        var second = UserMsg("I don't like olives either.");
        await BuildPipeline(scope.ServiceProvider, host.Clock, RuleOnly(), Fact(
                "dislikes", "olives", "The user dislikes olives.", second, "I don't like olives"))
            .ProcessAsync(User, new[] { second });

        Assert.Equal(2, (await ActiveFactsAsync(scope)).Count);
    }
}
