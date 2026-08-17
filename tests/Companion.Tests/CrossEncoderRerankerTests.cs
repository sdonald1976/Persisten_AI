using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Companion.Infrastructure.Cognition;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The cross-encoder reranker, and mostly the ways it declines to act.
///
/// It sits in front of retrieval, which is the one place in the turn where being confidently wrong
/// is invisible: a reordered packet still reads fluently, and the memories that should have been
/// there are simply absent with nothing saying so. So every path where the model has less than a
/// complete answer falls back to the retriever's own ranking, which is at least explainable — every
/// score in it can say which signal contributed what.
/// </summary>
public class CrossEncoderRerankerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static RetrievalResult Result(string content, double score) => new()
    {
        Memory = new SemanticMemory
        {
            Id = Guid.NewGuid(),
            UserId = "u",
            NormalizedFact = content,
            Confidence = 0.9,
            Status = MemoryStatus.Active,
            LastConfirmed = Now,
            CreatedAt = Now,
        },
        Score = score,
        Signals = new Dictionary<string, double> { ["test"] = score },
        Reason = "test",
    };

    private static CrossEncoderMemoryReranker Build(ITextPairScorer scorer)
        => new(scorer, new RuleBasedMemoryReranker(), NullLogger<CrossEncoderMemoryReranker>.Instance);

    private sealed class Scorer : ITextPairScorer
    {
        private readonly double[]? _scores;
        public Scorer(params double[]? scores) => _scores = scores;

        public string Name => "test";
        public bool IsAvailable { get; init; } = true;
        public CognitiveModelStatus Status => new() { Name = Name, Available = IsAvailable };
        public int Calls { get; private set; }

        public Task<IReadOnlyList<double>> ScoreAsync(
            string query, IReadOnlyList<string> candidates, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<double>>(_scores ?? Array.Empty<double>());
        }
    }

    [Fact]
    public async Task ReordersByTheModelsScore()
    {
        var reranked = await Build(new Scorer(0.1, 9.0, 0.5)).RerankAsync(
            "the buoy one",
            new[] { Result("Halyard, a C# service.", 0.9), Result("The marine sensor project.", 0.2), Result("A corgi called Kanga.", 0.5) },
            maxResults: 3);

        Assert.Equal("The marine sensor project.", reranked[0].Memory.Content);
    }

    /// <summary>No model, no change — the ordering the retriever produced survives untouched.</summary>
    [Fact]
    public async Task WithNoModel_TheRetrieversOrderIsKept()
    {
        var candidates = new[] { Result("first", 0.9), Result("second", 0.5) };

        var reranked = await Build(new Scorer { IsAvailable = false })
            .RerankAsync("q", candidates, maxResults: 2);

        Assert.Equal("first", reranked[0].Memory.Content);
        Assert.Equal("second", reranked[1].Memory.Content);
    }

    /// <summary>
    /// A partial answer is no answer. Ordering some of the list by one signal and the rest by
    /// another is a ranking that is neither, and worse than either alone.
    /// </summary>
    [Fact]
    public async Task AShortScoreList_IsRefusedRatherThanPartlyApplied()
    {
        var candidates = new[] { Result("first", 0.9), Result("second", 0.5), Result("third", 0.1) };

        var reranked = await Build(new Scorer(5.0)).RerankAsync("q", candidates, maxResults: 3);

        Assert.Equal(new[] { "first", "second", "third" }, reranked.Select(r => r.Memory.Content));
    }

    /// <summary>One candidate cannot be reordered, so the model is not worth waking up for it.</summary>
    [Fact]
    public async Task ASingleCandidate_SkipsTheModelEntirely()
    {
        var scorer = new Scorer(1.0);

        await Build(scorer).RerankAsync("q", new[] { Result("only", 0.9) }, maxResults: 3);

        Assert.Equal(0, scorer.Calls);
    }

    [Fact]
    public async Task TheResultIsCappedAtMaxResults()
    {
        var reranked = await Build(new Scorer(1.0, 2.0, 3.0)).RerankAsync(
            "q",
            new[] { Result("a", 0.1), Result("b", 0.2), Result("c", 0.3) },
            maxResults: 2);

        Assert.Equal(2, reranked.Count);
        Assert.Equal("c", reranked[0].Memory.Content);
    }
}
