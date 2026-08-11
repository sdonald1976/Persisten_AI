using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Infrastructure.Seeding;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Old thoughts resurface on their own: a weeks-old musing whose subject matches the current
/// turn re-enters the prompt (age-labeled, so "I'd been thinking a while back…" is honest),
/// while unrelated old thoughts stay in the diary. Fresh musings still color turns by default.
/// </summary>
public class MusingRetrievalTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
    private const string User = CompanionSeeder.DemoUserId;

    private static async Task SeedMusingAsync(IServiceProvider sp, string musing, DateTimeOffset createdAt)
    {
        var embeddings = sp.GetRequiredService<IEmbeddingModel>();
        await sp.GetRequiredService<IReflectionStore>().AddAsync(
            new Reflection
            {
                UserId = User, CreatedAt = createdAt, CoveredThrough = createdAt,
                Musing = musing, Embedding = await embeddings.EmbedAsync(musing),
            },
            Array.Empty<Curiosity>());
    }

    private static async Task<Guid> NewConversationAsync(IServiceProvider sp)
        => (await sp.GetRequiredService<IConversationStore>()
            .StartConversationAsync(User, "t", "mock", "test")).Id;

    [Fact]
    public async Task AnOldMusing_Resurfaces_WhenTheConversationComesBackToIt()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await SeedMusingAsync(sp,
            "She keeps circling back to the garden and the tomato seedlings — it seems to steady her.",
            Now.AddDays(-30));
        var conv = await NewConversationAsync(sp);

        // The turn is about the same subject → the month-old thought comes back, with its age.
        var trace = await sp.GetRequiredService<ICompanion>()
            .RespondAsync(User, conv, "the garden is finally coming together, the tomato seedlings took");
        var rendered = trace.Packet.Render();

        Assert.Contains("tomato seedlings", rendered);
        Assert.Contains("a thought from", rendered); // age-labeled: "I'd been thinking…" stays honest
    }

    [Fact]
    public async Task AnUnrelatedOldMusing_StaysInTheDiary()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await SeedMusingAsync(sp,
            "She keeps circling back to the garden and the tomato seedlings — it seems to steady her.",
            Now.AddDays(-30));
        var conv = await NewConversationAsync(sp);

        var trace = await sp.GetRequiredService<ICompanion>()
            .RespondAsync(User, conv, "can you help me debug this websocket handshake error?");

        // Too old to surface by freshness, not relevant enough to surface by similarity.
        Assert.DoesNotContain("tomato seedlings", trace.Packet.Render());
    }

    [Fact]
    public async Task AFreshMusing_StillColorsUnrelatedTurns()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await SeedMusingAsync(sp, "I keep thinking about how tired she sounded.", Now.AddHours(-8));
        var conv = await NewConversationAsync(sp);

        var trace = await sp.GetRequiredService<ICompanion>()
            .RespondAsync(User, conv, "can you help me debug this websocket handshake error?");

        // Fresh = it's simply on her mind, whatever the topic — and with no age label.
        var rendered = trace.Packet.Render();
        Assert.Contains("how tired she sounded", rendered);
        Assert.DoesNotContain("a thought from", rendered);
    }

    [Fact]
    public async Task RelevanceOutranksFreshness()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await SeedMusingAsync(sp,
            "She keeps circling back to the garden and the tomato seedlings — it seems to steady her.",
            Now.AddDays(-30));
        await SeedMusingAsync(sp, "I keep thinking about how tired she sounded.", Now.AddHours(-8));
        var conv = await NewConversationAsync(sp);

        var trace = await sp.GetRequiredService<ICompanion>()
            .RespondAsync(User, conv, "the garden is finally coming together, the tomato seedlings took");

        // The on-topic month-old thought wins over the fresh off-topic one.
        Assert.Contains("tomato seedlings", trace.Packet.Render());
    }
}
