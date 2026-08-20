using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Infrastructure.Models;
using Companion.Infrastructure.Seeding;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Degradation invariants found by the full-solution audit: an infrastructure failure around
/// the conversation must cost the user quality, never the conversation itself. Also covers the
/// embedding cache that collapses a turn's repeated embeddings of the same text.
/// </summary>
public class ResilienceTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// An embedding server that works until it doesn't — so a test can seed real data and then
    /// simulate the server dying mid-session (the realistic failure, and the one that used to
    /// take the whole conversation down with it).
    /// </summary>
    private sealed class FlakyEmbeddingModel : IEmbeddingModel
    {
        private readonly IEmbeddingModel _inner = new MockEmbeddingModel();
        public bool Dead { get; set; }
        public int CallsWhileDead { get; private set; }
        public int Dimensions => _inner.Dimensions;

        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        {
            if (!Dead)
                return _inner.EmbedAsync(text, ct);
            CallsWhileDead++;
            throw new HttpRequestException("embedding endpoint refused the connection");
        }
    }

    /// <summary>Counts real embedding work so cache hits are observable.</summary>
    private sealed class CountingEmbeddingModel : IEmbeddingModel
    {
        private readonly IEmbeddingModel _inner = new MockEmbeddingModel();
        private readonly object _gate = new();
        public List<string> Texts { get; } = new();
        public int Calls => Texts.Count;
        public int Dimensions => _inner.Dimensions;
        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        {
            lock (_gate) Texts.Add(text);
            return _inner.EmbedAsync(text, ct);
        }
    }

    /// <summary>An extraction stage that always blows up, after the reply is already stored.</summary>
    private sealed class ExplodingPipeline : IMemoryPipeline
    {
        public Task<MemoryExtractionResult> ProcessAsync(
            string userId, IReadOnlyList<Message> exchange,
            ReferenceResolution? resolution = null, CancellationToken ct = default)
            => throw new InvalidOperationException("extraction model returned garbage");
    }

    private static async Task<Guid> SeedConversationAsync(TestHost host)
    {
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await sp.GetRequiredService<CompanionSeeder>().SeedAsync(Now);
        return (await sp.GetRequiredService<IConversationStore>()
            .StartConversationAsync(CompanionSeeder.DemoUserId, "t", "mock", "test")).Id;
    }

    [Fact]
    public async Task EmbeddingServerDies_TheTurnStillAnswers_OnKeywordsAlone()
    {
        var flaky = new FlakyEmbeddingModel();
        await using var host = new TestHost(
            Now, configureServices: s => s.AddSingleton<IEmbeddingModel>(flaky));
        var conversationId = await SeedConversationAsync(host);

        flaky.Dead = true; // the server goes down between sessions

        using var scope = host.CreateScope();
        var trace = await scope.ServiceProvider.GetRequiredService<ICompanion>().RespondAsync(
            CompanionSeeder.DemoUserId, conversationId, "I finally tested the Jetson at home.");

        // The conversation survived an outage that used to throw straight out of retrieval.
        Assert.Equal(TurnStatus.Answered, trace.Status);
        Assert.False(string.IsNullOrWhiteSpace(trace.Response));
        Assert.True(flaky.CallsWhileDead > 0); // it really did try
    }

    [Fact]
    public async Task DerivedStateFailure_DoesNotDestroyADeliveredReply()
    {
        await using var host = new TestHost(
            Now, configureServices: s => s.AddScoped<IMemoryPipeline>(_ => new ExplodingPipeline()));
        var conversationId = await SeedConversationAsync(host);

        TurnTrace trace;
        using (var scope = host.CreateScope())
        {
            trace = await scope.ServiceProvider.GetRequiredService<ICompanion>().RespondAsync(
                CompanionSeeder.DemoUserId, conversationId, "I ordered a new soldering iron today.");
        }

        Assert.Equal(TurnStatus.Answered, trace.Status);
        Assert.False(string.IsNullOrWhiteSpace(trace.Response));
        Assert.Equal(0, trace.Extraction.Accepted); // derived memory was lost, as expected

        // And the exchange is intact on disk — no half-written turn.
        using var verify = host.CreateScope();
        var messages = await verify.ServiceProvider.GetRequiredService<IConversationStore>()
            .GetRecentMessagesAsync(conversationId, CompanionSeeder.DemoUserId, 10);
        Assert.Contains(messages, m => m.Role == MessageRole.User && m.Content.Contains("soldering"));
        Assert.Contains(messages, m => m.Role == MessageRole.Assistant);
    }

    [Fact]
    public async Task EmbeddingCache_CollapsesRepeatsOfTheSameText()
    {
        var counting = new CountingEmbeddingModel();
        var cache = new CachingEmbeddingModel(counting);

        var first = await cache.EmbedAsync("the same question");
        var second = await cache.EmbedAsync("the same question");
        await cache.EmbedAsync("a different question");

        Assert.Equal(2, counting.Calls);          // not 3
        Assert.Equal(first, second);              // identical values…
        Assert.NotSame(first, second);            // …but never a shared mutable array
        Assert.Equal(counting.Dimensions, cache.Dimensions);
    }

    [Fact]
    public async Task ATurn_EmbedsTheUserMessageOnlyOnce()
    {
        // Project resolution, open-loop scoring, and memory retrieval all embed the turn's text;
        // the cache means the provider sees it once.
        var counting = new CountingEmbeddingModel();
        await using var host = new TestHost(
            Now, configureServices: s => s.AddSingleton<IEmbeddingModel>(new CachingEmbeddingModel(counting)));
        var conversationId = await SeedConversationAsync(host);
        var before = counting.Calls;

        using var scope = host.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ICompanion>().RespondAsync(
            CompanionSeeder.DemoUserId, conversationId, "I finally tested the Jetson at home.");

        // Exactly one embedding of the user message itself; the turn's other embedding work is
        // for NEW text (extracted memories, created projects), which legitimately differs.
        Assert.True(counting.Calls > before);
        Assert.Equal(1, counting.Texts.Count(t => t == "I finally tested the Jetson at home."));
    }
}
