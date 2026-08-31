using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Companion.Infrastructure.Cognition;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The reranker shadow's contract: the authoritative result is always what the turn gets,
/// unaffected by a slow or broken shadow; recording is skipped when the turn forbids it; and
/// the record captures all three orderings id-only.
/// </summary>
public class ShadowComparingRerankerTests
{
    private sealed class FakeMemory(Guid id, string content) : IMemory
    {
        public Guid Id { get; } = id;
        public string UserId => "u";
        public MemoryKind Kind => MemoryKind.Semantic;
        public MemoryOwner Owner => MemoryOwner.User;
        public string Content { get; } = content;
        public double Importance => 0.5;
        public double Confidence => 0.8;
        public MemoryStatus Status => MemoryStatus.Active;
        public DateTimeOffset CreatedAt => DateTimeOffset.MinValue;
        public DateTimeOffset EffectiveAt => DateTimeOffset.MinValue;
        public string? RelatedProject => null;
        public float[]? Embedding { get; set; }
    }

    private static RetrievalResult R(Guid id, string content, double score) => new()
    {
        Memory = new FakeMemory(id, content),
        Score = score,
        Signals = new Dictionary<string, double>(),
        Reason = "t",
    };

    // An identity reranker that returns candidates unchanged, tagged with a name via a delay.
    private sealed class OrderReranker(IReadOnlyList<int> order, TimeSpan delay = default) : IMemoryReranker
    {
        public async Task<IReadOnlyList<RetrievalResult>> RerankAsync(
            string query, IReadOnlyList<RetrievalResult> candidates, int maxResults, CancellationToken ct = default)
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
            return order.Select(i => candidates[i]).Take(maxResults).ToList();
        }
    }

    private sealed class ThrowingReranker : IMemoryReranker
    {
        public Task<IReadOnlyList<RetrievalResult>> RerankAsync(
            string query, IReadOnlyList<RetrievalResult> candidates, int maxResults, CancellationToken ct = default)
            => throw new InvalidOperationException("shadow boom");
    }

    private sealed class CapturingSink : IRerankShadowSink
    {
        public bool IsRecording => true;
        public readonly List<RerankShadowRecord> Records = [];
        public void Record(RerankShadowRecord record) => Records.Add(record);
    }

    private static (List<RetrievalResult> cands, Guid a, Guid b, Guid c) ThreeCandidates()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        return ([R(a, "alpha alpha", 0.9), R(b, "beta beta", 0.8), R(c, "gamma gamma", 0.7)], a, b, c);
    }

    [Fact]
    public async Task TheAuthoritativeResultIsReturned_AndTheShadowIsRecorded()
    {
        var (cands, a, b, c) = ThreeCandidates();
        var sink = new CapturingSink();
        var reranker = new ShadowComparingReranker(
            authoritative: new OrderReranker([0, 1, 2]),   // a,b,c
            crossEncoder: new OrderReranker([2, 1, 0]),     // c,b,a
            rule: new OrderReranker([1, 0, 2]),             // b,a,c
            sink, NullLogger<ShadowComparingReranker>.Instance);

        using (RerankShadowScope.Open(Guid.NewGuid(), "u", mayRecord: true))
        {
            var result = await reranker.RerankAsync("q", cands, 3);
            Assert.Equal(a, result[0].Memory.Id);   // authoritative top-1 is displayed
        }

        var rec = Assert.Single(sink.Records);
        Assert.Equal(a, rec.Authoritative.Ranking[0]);
        Assert.Equal(c, rec.CrossEncoder!.Ranking[0]);
        Assert.Equal(b, rec.Rule!.Ranking[0]);
        Assert.False(rec.CrossEncoderTop1Agrees);   // c != a
        Assert.False(rec.RuleTop1Agrees);           // b != a
        Assert.Equal(3, rec.CandidateIds.Count);
    }

    [Fact]
    public async Task AThrowingShadow_DoesNotAffectTheAuthoritativeResult()
    {
        var (cands, a, _, _) = ThreeCandidates();
        var sink = new CapturingSink();
        var reranker = new ShadowComparingReranker(
            authoritative: new OrderReranker([0, 1, 2]),
            crossEncoder: new ThrowingReranker(),
            rule: new ThrowingReranker(),
            sink, NullLogger<ShadowComparingReranker>.Instance);

        using var scope = RerankShadowScope.Open(Guid.NewGuid(), "u", mayRecord: true);
        var result = await reranker.RerankAsync("q", cands, 3);

        Assert.Equal(a, result[0].Memory.Id);       // unaffected
        var rec = Assert.Single(sink.Records);       // still recorded, as failures
        Assert.True(rec.CrossEncoder!.Failed);
        Assert.True(rec.Rule!.Failed);
    }

    [Fact]
    public async Task ASlowShadow_TimesOut_AndIsRecordedAsFailed_ButTheResultStillReturns()
    {
        var (cands, a, _, _) = ThreeCandidates();
        var sink = new CapturingSink();
        var reranker = new ShadowComparingReranker(
            authoritative: new OrderReranker([0, 1, 2]),
            crossEncoder: new OrderReranker([2, 1, 0], TimeSpan.FromSeconds(30)),  // will time out
            rule: new OrderReranker([1, 0, 2]),
            sink, NullLogger<ShadowComparingReranker>.Instance,
            shadowTimeout: TimeSpan.FromMilliseconds(100));

        using var scope = RerankShadowScope.Open(Guid.NewGuid(), "u", mayRecord: true);
        var result = await reranker.RerankAsync("q", cands, 3);

        Assert.Equal(a, result[0].Memory.Id);
        var rec = Assert.Single(sink.Records);
        Assert.True(rec.CrossEncoder!.Failed);
        Assert.Equal("timeout", rec.CrossEncoder.FailureReason);
        Assert.False(rec.Rule!.Failed);             // the fast one still recorded normally
    }

    [Fact]
    public async Task APrivateTurn_RecordsNothing_ButStillReturnsTheAuthoritativeResult()
    {
        var (cands, a, _, _) = ThreeCandidates();
        var sink = new CapturingSink();
        var reranker = new ShadowComparingReranker(
            new OrderReranker([0, 1, 2]), new OrderReranker([2, 1, 0]),
            new OrderReranker([1, 0, 2]), sink, NullLogger<ShadowComparingReranker>.Instance);

        using (RerankShadowScope.Open(Guid.NewGuid(), "u", mayRecord: false))
        {
            var result = await reranker.RerankAsync("q", cands, 3);
            Assert.Equal(a, result[0].Memory.Id);
        }
        Assert.Empty(sink.Records);
    }

    [Fact]
    public async Task NoScope_RecordsNothing()
    {
        var (cands, a, _, _) = ThreeCandidates();
        var sink = new CapturingSink();
        var reranker = new ShadowComparingReranker(
            new OrderReranker([0, 1, 2]), new OrderReranker([2, 1, 0]),
            new OrderReranker([1, 0, 2]), sink, NullLogger<ShadowComparingReranker>.Instance);

        var result = await reranker.RerankAsync("q", cands, 3);  // no scope open
        Assert.Equal(a, result[0].Memory.Id);
        Assert.Empty(sink.Records);
    }

    [Fact]
    public async Task SingleCandidate_IsNotShadowed()
    {
        var id = Guid.NewGuid();
        var sink = new CapturingSink();
        var reranker = new ShadowComparingReranker(
            new OrderReranker([0]), new OrderReranker([0]), new OrderReranker([0]),
            sink, NullLogger<ShadowComparingReranker>.Instance);
        using var scope = RerankShadowScope.Open(Guid.NewGuid(), "u", mayRecord: true);
        await reranker.RerankAsync("q", [R(id, "x", 0.5)], 3);
        Assert.Empty(sink.Records);
    }
}
