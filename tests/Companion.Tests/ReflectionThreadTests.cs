using System.Text.Json;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Trains of thought: a musing that develops an earlier one joins its thread, a genuinely new
/// thought starts its own, a settled thread stops being resumed, and the model can't graft its
/// thought onto a reflection it was never shown.
/// </summary>
public class ReflectionThreadTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
    private const string User = "thread-user";

    /// <summary>Reflection output with an optional continuation claim.</summary>
    private static string Pass(string musing, string? continues = null, bool settled = false)
        => JsonSerializer.Serialize(new
        {
            musing,
            continuesThought = continues,
            thoughtSettled = settled,
            curiosities = Array.Empty<object>(),
        });

    /// <summary>Feeds the reflector scripted passes, with real conversation material between them.</summary>
    private static async Task<List<Reflection>> RunPassesAsync(
        TestHost host, QueuedChatModel chat, params string[] messagesPerPass)
    {
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var conversations = sp.GetRequiredService<IConversationStore>();
        var reflector = sp.GetRequiredService<IReflector>();
        var conversation = await conversations.StartConversationAsync(User, "t", "mock", "test");

        foreach (var message in messagesPerPass)
        {
            // Reflection needs new user material each pass to have anything to think about, and
            // it must be NEWER than the last pass's watermark — so timestamps follow the clock.
            var at = host.Clock.GetUtcNow();
            for (var i = 0; i < 3; i++)
            {
                at = at.AddMinutes(1);
                await conversations.AddMessageAsync(new Message
                {
                    Id = Guid.NewGuid(), UserId = User, ConversationId = conversation.Id,
                    Role = MessageRole.User, Content = $"{message} ({i})", Timestamp = at,
                });
            }
            host.Clock.Advance(TimeSpan.FromHours(1));
            await reflector.ReflectResultAsync(User);
        }

        return (await sp.GetRequiredService<IReflectionStore>().GetRecentAsync(User, 20))
            .Where(r => r.HasMusing)
            .OrderBy(r => r.CreatedAt)
            .ToList();
    }

    [Fact]
    public async Task AMusingThatContinuesAnEarlierOne_JoinsItsThread()
    {
        // The second pass must reference the first pass's REAL id, so it's queued once known.
        var chat = new QueuedChatModel(Pass("I keep coming back to how he talks about the greenhouse."));
        await using var host = new TestHost(Start, configureServices: s => s.AddSingleton<IChatModel>(chat));

        // First pass alone, so we can learn the real id it must reference.
        var afterFirst = await RunPassesAsync(host, chat, "the greenhouse sensors again");
        var rootId = Assert.Single(afterFirst).Id;

        // Now script the second pass to continue it by short id.
        var continuing = Pass("Following that thought — maybe it's the automation he likes, not the plants.",
            continues: rootId.ToString("N")[..8]);
        chat.Enqueue(continuing);
        var all = await RunPassesAsync(host, chat, "more greenhouse automation talk");

        Assert.Equal(2, all.Count);
        var (root, child) = (all[0], all[1]);
        Assert.Equal(root.Id, root.ThreadId);            // the first is its own root
        Assert.Equal(root.ThreadId, child.ThreadId);     // …and the second joined it
        Assert.Equal(root.Id, child.ContinuesReflectionId);
    }

    [Fact]
    public async Task ANewThought_StartsItsOwnThread()
    {
        var chat = new QueuedChatModel(
            Pass("A thought about the greenhouse."),
            Pass("Something entirely different about his sleep.")); // no continuation claim
        await using var host = new TestHost(Start, configureServices: s => s.AddSingleton<IChatModel>(chat));

        var all = await RunPassesAsync(host, chat, "greenhouse chat", "talking about sleep");

        Assert.Equal(2, all.Count);
        Assert.NotEqual(all[0].ThreadId, all[1].ThreadId);
        Assert.All(all, r => Assert.Equal(r.Id, r.ThreadId));
        Assert.All(all, r => Assert.Null(r.ContinuesReflectionId));
    }

    [Fact]
    public async Task AnInventedContinuationId_IsIgnored_AndStartsAFreshThread()
    {
        // The model must not be able to graft its thought onto an arbitrary or made-up row.
        var chat = new QueuedChatModel(
            Pass("First thought."),
            Pass("Second thought.", continues: "deadbeef"));
        await using var host = new TestHost(Start, configureServices: s => s.AddSingleton<IChatModel>(chat));

        var all = await RunPassesAsync(host, chat, "first topic", "second topic");

        Assert.Equal(2, all.Count);
        Assert.Null(all[1].ContinuesReflectionId);
        Assert.Equal(all[1].Id, all[1].ThreadId);
    }

    [Fact]
    public async Task ThreadsEndpointGroupsTheDiary_NewestDevelopmentFirst()
    {
        await using var host = new TestHost(Start);
        using var scope = host.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IReflectionStore>();

        var threadId = Guid.NewGuid();
        await store.AddAsync(new Reflection
        {
            Id = threadId, UserId = User, CreatedAt = Start, CoveredThrough = Start,
            Musing = "the first step", ThreadId = threadId,
        }, Array.Empty<Curiosity>());
        var second = Guid.NewGuid();
        await store.AddAsync(new Reflection
        {
            Id = second, UserId = User, CreatedAt = Start.AddHours(2), CoveredThrough = Start.AddHours(2),
            Musing = "the second step", ThreadId = threadId, ContinuesReflectionId = threadId,
        }, Array.Empty<Curiosity>());
        var loner = Guid.NewGuid();
        await store.AddAsync(new Reflection
        {
            Id = loner, UserId = User, CreatedAt = Start.AddHours(1), CoveredThrough = Start.AddHours(1),
            Musing = "an unrelated thought", ThreadId = loner,
        }, Array.Empty<Curiosity>());

        var threads = await store.GetThreadsAsync(User, 10);

        Assert.Equal(2, threads.Count);
        var developed = threads[0];                       // most recently developed comes first
        Assert.Equal(threadId, developed.ThreadId);
        Assert.Equal(2, developed.Length);
        Assert.Equal("the second step", developed.Latest.Musing);
        Assert.Equal(Start, developed.StartedAt);
        Assert.Equal(1, threads[1].Length);
    }

    [Fact]
    public async Task PreThreadingEntries_StillRenderAsTheirOwnThreads()
    {
        // Rows written before threading have an empty ThreadId; they must not all collapse into
        // one bogus thread when an existing database is upgraded.
        await using var host = new TestHost(Start);
        using var scope = host.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IReflectionStore>();

        for (var i = 0; i < 3; i++)
        {
            await store.AddAsync(new Reflection
            {
                Id = Guid.NewGuid(), UserId = User, CreatedAt = Start.AddHours(i),
                CoveredThrough = Start.AddHours(i), Musing = $"legacy thought {i}",
                ThreadId = Guid.Empty,
            }, Array.Empty<Curiosity>());
        }

        var threads = await store.GetThreadsAsync(User, 10);

        Assert.Equal(3, threads.Count);
        Assert.All(threads, t => Assert.Equal(1, t.Length));
    }
}
