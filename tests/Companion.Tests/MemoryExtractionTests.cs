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

public class MemoryExtractionTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private const string User = "u1";

    private static Message UserMsg(string text) => new()
    {
        Id = Guid.NewGuid(),
        ConversationId = Guid.NewGuid(),
        UserId = User,
        Role = MessageRole.User,
        Content = text,
        Timestamp = Now,
    };

    private static MemoryPipeline BuildPipeline(
        IServiceProvider sp, IMemoryExtractor extractor, TimeProvider clock, CompanionOptions? options = null)
        => new(
            extractor,
            sp.GetRequiredService<IMemoryStore>(),
            sp.GetRequiredService<IEmbeddingModel>(),
            Options.Create(options ?? new CompanionOptions()),
            clock,
            NullLogger<MemoryPipeline>.Instance);

    [Fact]
    public async Task NewFact_IsAccepted_WithEvidenceAndAuditTrail()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var msg = UserMsg("I love spicy Thai food.");
        var candidate = new MemoryCandidate
        {
            Kind = MemoryKind.Semantic,
            Subject = "user",
            Predicate = "prefers",
            Value = "spicy Thai food",
            Content = "The user prefers spicy Thai food.",
            ProposedConfidence = 0.7,
            Evidence = new[] { new CandidateEvidence(msg.Id, "I love spicy Thai food") },
        };
        var pipeline = BuildPipeline(scope.ServiceProvider, new StubExtractor(candidate), host.Clock);

        var result = await pipeline.ProcessAsync(User, new[] { msg });

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(MemoryDecisionKind.Accepted, decision.Outcome);
        Assert.True(decision.FinalConfidence > 0.7); // boosted for a direct user statement
        Assert.NotNull(decision.ResultingMemoryId);

        var store = scope.ServiceProvider.GetRequiredService<IMemoryStore>();
        Assert.NotEmpty(await store.GetEvidenceAsync(decision.ResultingMemoryId!.Value));
        var revisions = await store.GetRevisionsAsync(decision.ResultingMemoryId!.Value);
        Assert.Contains(revisions, r => r.Kind == RevisionKind.Created);
    }

    [Fact]
    public async Task DuplicateFact_IsMerged_NotDuplicated()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMemoryStore>();

        MemoryCandidate Candidate(Message m) => new()
        {
            Kind = MemoryKind.Semantic,
            Subject = "user",
            Predicate = "prefers",
            Value = "spicy Thai food",
            Content = "The user prefers spicy Thai food.",
            ProposedConfidence = 0.7,
            Evidence = new[] { new CandidateEvidence(m.Id, "spicy Thai food") },
        };

        var first = UserMsg("I love spicy Thai food.");
        var second = UserMsg("Still love spicy Thai food.");

        var r1 = await BuildPipeline(scope.ServiceProvider, new StubExtractor(Candidate(first)), host.Clock)
            .ProcessAsync(User, new[] { first });
        var r2 = await BuildPipeline(scope.ServiceProvider, new StubExtractor(Candidate(second)), host.Clock)
            .ProcessAsync(User, new[] { second });

        Assert.Equal(MemoryDecisionKind.Accepted, r1.Decisions[0].Outcome);
        Assert.Equal(MemoryDecisionKind.Merged, r2.Decisions[0].Outcome);
        Assert.Equal(r1.Decisions[0].ResultingMemoryId, r2.Decisions[0].MatchedMemoryId);

        // Only one memory for the fact, and it now has evidence from both mentions.
        var thai = (await store.GetRetrievableMemoriesAsync(User))
            .Where(m => m.Content.Contains("Thai", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Single(thai);
        Assert.True((await store.GetEvidenceAsync(thai[0].Id)).Count >= 2);
    }

    [Fact]
    public async Task CandidateWithoutValidEvidence_IsRejected()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var msg = UserMsg("I love spicy Thai food.");
        var candidate = new MemoryCandidate
        {
            Kind = MemoryKind.Semantic,
            Content = "The user prefers spicy Thai food.",
            ProposedConfidence = 0.9,
            // cites a message that is NOT part of the exchange
            Evidence = new[] { new CandidateEvidence(Guid.NewGuid(), "spicy Thai food") },
        };

        var result = await BuildPipeline(scope.ServiceProvider, new StubExtractor(candidate), host.Clock)
            .ProcessAsync(User, new[] { msg });

        Assert.Equal(MemoryDecisionKind.Rejected, result.Decisions[0].Outcome);
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<IMemoryStore>()
            .GetRetrievableMemoriesAsync(User));
    }

    [Fact]
    public async Task LowConfidenceNewFact_IsRejected()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var msg = UserMsg("Maybe I sort of like jazz, not sure.");
        var candidate = new MemoryCandidate
        {
            Kind = MemoryKind.Semantic,
            Subject = "user",
            Predicate = "prefers",
            Value = "jazz",
            Content = "The user prefers jazz.",
            ProposedConfidence = 0.1, // 0.1 + 0.15 (direct) = 0.25 < 0.35 default threshold
            Evidence = new[] { new CandidateEvidence(msg.Id, "sort of like jazz") },
        };

        var result = await BuildPipeline(scope.ServiceProvider, new StubExtractor(candidate), host.Clock)
            .ProcessAsync(User, new[] { msg });

        Assert.Equal(MemoryDecisionKind.Rejected, result.Decisions[0].Outcome);
    }

    [Fact]
    public async Task SameSlotDifferentValue_IsHeldForReview_AndNotRetrievable()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMemoryStore>();
        var embeddings = scope.ServiceProvider.GetRequiredService<IEmbeddingModel>();

        // Existing fact occupying the (user, diet) slot.
        var existing = new SemanticMemory
        {
            Id = Guid.NewGuid(),
            UserId = User,
            Subject = "user",
            Predicate = "diet",
            Value = "low carb",
            NormalizedFact = "The user follows a low-carb diet.",
            Confidence = 0.8,
            Status = MemoryStatus.Active,
            LastConfirmed = Now,
            CreatedAt = Now,
            Embedding = await embeddings.EmbedAsync("The user follows a low-carb diet."),
        };
        await store.AddSemanticAsync(existing);

        var msg = UserMsg("Actually I'm doing keto now.");
        var candidate = new MemoryCandidate
        {
            Kind = MemoryKind.Semantic,
            Subject = "user",
            Predicate = "diet",
            Value = "keto",
            Content = "The user follows a keto diet.",
            ProposedConfidence = 0.8,
            Evidence = new[] { new CandidateEvidence(msg.Id, "doing keto now") },
        };

        // Thresholds chosen so any same-slot, different-value fact is treated as a change.
        var options = new CompanionOptions { DuplicateSimilarityThreshold = 0.99, ContradictionSimilarityThreshold = 0.0 };
        var result = await BuildPipeline(scope.ServiceProvider, new StubExtractor(candidate), host.Clock, options)
            .ProcessAsync(User, new[] { msg });

        var decision = result.Decisions[0];
        Assert.Equal(MemoryDecisionKind.NeedsReview, decision.Outcome);
        Assert.Equal(existing.Id, decision.MatchedMemoryId);

        // The unreviewed candidate must NOT be retrievable; the original still is.
        var retrievable = await store.GetRetrievableMemoriesAsync(User);
        Assert.DoesNotContain(retrievable, m => m.Content.Contains("keto", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(retrievable, m => m.Content.Contains("low-carb", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RuleBasedExtractor_TurnsNaturalLanguageIntoAcceptedMemory()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        // Use the real DI-registered rule-based extractor + pipeline.
        var pipeline = scope.ServiceProvider.GetRequiredService<IMemoryPipeline>();

        var msg = UserMsg("I deployed the service to the cluster.");
        var result = await pipeline.ProcessAsync(User, new[] { msg });

        Assert.Contains(result.Decisions, d => d.Outcome == MemoryDecisionKind.Accepted);
        Assert.Contains(await scope.ServiceProvider.GetRequiredService<IMemoryStore>()
            .GetRetrievableMemoriesAsync(User),
            m => m.Kind == MemoryKind.Episodic);
    }
}
