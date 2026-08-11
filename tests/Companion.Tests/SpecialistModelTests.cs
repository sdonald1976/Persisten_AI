using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Companion.Infrastructure.Models;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Companion.Tests;

public class SpecialistModelTests
{
    [Fact]
    public async Task LlmReranker_ReordersCandidates_ByReturnedIds()
    {
        var first = Result("The user likes cooking.");
        var second = Result("The user is debugging a Jetson board.");
        var reranker = new LlmMemoryReranker(
            new CannedChatModel($$"""{"ids":["{{second.Memory.Id:N}}"]}"""),
            new RuleBasedMemoryReranker(),
            NullLogger<LlmMemoryReranker>.Instance);

        var reranked = await reranker.RerankAsync(
            "Any update on the board?", new[] { first, second }, maxResults: 1);

        Assert.Single(reranked);
        Assert.Equal(second.Memory.Id, reranked[0].Memory.Id);
    }

    [Fact]
    public async Task LlmReranker_FallsBackToHybridOrder_OnBadOutput()
    {
        var first = Result("The user likes cooking.");
        var second = Result("The user is debugging a Jetson board.");
        var reranker = new LlmMemoryReranker(
            new CannedChatModel("not json"),
            new RuleBasedMemoryReranker(),
            NullLogger<LlmMemoryReranker>.Instance);

        var reranked = await reranker.RerankAsync(
            "Any update on the board?", new[] { first, second }, maxResults: 1);

        Assert.Equal(first.Memory.Id, reranked[0].Memory.Id);
    }

    [Theory]
    [InlineData("keep this private, I changed my bank PIN")]
    [InlineData("My OpenAI key is sk-abcdefghijklmnopqrstuvwxyz1234")]
    public async Task RuleBasedPrivacyClassifier_SkipsSensitiveMessages(string text)
    {
        var classifier = new RuleBasedPrivacyClassifier();
        Assert.True(await classifier.ShouldSkipDerivedMemoryAsync(text));
    }

    [Fact]
    public async Task LlmPrivacyClassifier_CanSkipSofterSensitiveMessages()
    {
        var classifier = new LlmPrivacyClassifier(
            new CannedChatModel("SKIP"),
            new RuleBasedPrivacyClassifier(),
            NullLogger<LlmPrivacyClassifier>.Instance);

        Assert.True(await classifier.ShouldSkipDerivedMemoryAsync(
            "My friend told me something deeply personal that I should not bring up later."));
    }

    private static RetrievalResult Result(string content)
    {
        var memory = new SemanticMemory
        {
            Id = Guid.NewGuid(),
            UserId = "u",
            Subject = "user",
            Predicate = "fact",
            Value = content,
            NormalizedFact = content,
            Confidence = 1,
            Importance = 1,
            Status = MemoryStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            LastConfirmed = DateTimeOffset.UtcNow,
            Embedding = new[] { 1f, 0f, 0f },
        };

        return new RetrievalResult
        {
            Memory = memory,
            Score = 1,
            Signals = new Dictionary<string, double> { ["test"] = 1 },
            Reason = "test",
        };
    }
}
