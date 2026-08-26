using System.Text.Json;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Companion.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// A4. Frame sessions age out, and the sweep cannot take a scene somebody is still in.
///
/// `PruneAsync` existed and nothing called it, so frame sessions and their transition logs
/// grew without bound. It also had two defects the wiring would have made live: an ACTIVE
/// scene that was never exited was never reaped at all, and boundaries were matched by
/// SceneRef alone — a short generated token — so a collision could reach another user's row.
/// </summary>
public class FrameRetentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private const string User = "usr-scott";
    private const string Other = "usr-someone-else";

    private static FrameTransitionRequest Request(
        string transition, Guid conv, string? scene = null, DateTimeOffset? at = null,
        string userId = User, Guid? evidence = null) => new()
    {
        UserId = userId,
        ConversationId = conv,
        Transition = transition,
        Cause = "explicit",
        At = at ?? Now,
        SceneRef = scene,
        EvidenceMessageId = evidence,
    };

    [Fact]
    public async Task AnActiveFrameInsideItsWindow_IsNeverPruned()
    {
        await using var host = new TestHost(Now);
        var store = host.Services.GetRequiredService<IFrameSessionStore>();
        var conv = Guid.NewGuid();

        await store.ApplyAsync(Request("enter", conv, "scene-live"), "t1");

        // Both windows well in the past: nothing qualifies.
        Assert.Equal(0, await store.PruneAsync(Now.AddDays(-1), Now.AddDays(-1)));
        Assert.NotNull(await store.GetActiveAsync(User, conv));
    }

    [Fact]
    public async Task AnExitedFrame_AgesOut()
    {
        await using var host = new TestHost(Now);
        var store = host.Services.GetRequiredService<IFrameSessionStore>();
        var conv = Guid.NewGuid();

        await store.ApplyAsync(Request("enter", conv, "scene-a", Now.AddDays(-200)), "t1");
        await store.ApplyAsync(Request("exit", conv, "scene-a", Now.AddDays(-199)), "t2");

        Assert.Equal(0, await store.PruneAsync(Now.AddDays(-365), Now.AddDays(-365)));  // too young
        Assert.Equal(1, await store.PruneAsync(Now.AddDays(-100), Now.AddDays(-365)));
    }

    [Fact]
    public async Task AnAbandonedActiveFrame_IsReapedOnTheLongerWindow()
    {
        // The case the old sweep had no answer for: entered, never exited, untouched since.
        await using var host = new TestHost(Now);
        var store = host.Services.GetRequiredService<IFrameSessionStore>();
        var conv = Guid.NewGuid();

        await store.ApplyAsync(Request("enter", conv, "scene-abandoned", Now.AddDays(-400)), "t1");

        // Still safe on the ended window alone, because it never ended.
        Assert.Equal(0, await store.PruneAsync(Now.AddDays(-100), Now.AddDays(-500)));
        Assert.NotNull(await store.GetActiveAsync(User, conv));

        Assert.Equal(1, await store.PruneAsync(Now.AddDays(-100), Now.AddDays(-365)));
        Assert.Null(await store.GetActiveAsync(User, conv));
    }

    [Fact]
    public async Task TheAbandonedWindowIsLongerThanTheEndedOne()
    {
        // Stated as a test because the ordering is the safety property: reaping a resumable
        // scene is worse than keeping a dead one a while longer.
        await Task.CompletedTask;
        Assert.True(SleepCycle.AbandonedFrameAge > SleepCycle.EndedFrameRetention);
    }

    [Fact]
    public async Task PruningCannotReachAnotherUsersBoundary_OnASharedSceneRef()
    {
        await using var host = new TestHost(Now);
        var store = host.Services.GetRequiredService<IFrameSessionStore>();
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        const string collidingScene = "scene-7c1f";        // deliberately the same token

        await store.ApplyAsync(Request("enter", mine, collidingScene, Now.AddDays(-200)), "m1");
        await store.AddBoundaryAsync(new FrameBoundaryRecord
        {
            UserId = User, ConversationId = mine, SceneRef = collidingScene,
            Subject = "no third-person narration", StatedAt = Now.AddDays(-200),
        });
        await store.ApplyAsync(Request("exit", mine, collidingScene, Now.AddDays(-199)), "m2");

        // Another user, same scene ref, still live.
        await store.ApplyAsync(
            Request("enter", theirs, collidingScene, Now, userId: Other), "o1");
        await store.AddBoundaryAsync(new FrameBoundaryRecord
        {
            UserId = Other, ConversationId = theirs, SceneRef = collidingScene,
            Subject = "no third-person narration", StatedAt = Now,
        });

        var removed = await store.PruneAsync(Now.AddDays(-100), Now.AddDays(-365));

        Assert.Equal(2, removed);                       // exactly my session and my boundary
        Assert.NotNull(await store.GetActiveAsync(Other, theirs));
        Assert.Single(await store.GetActiveBoundariesAsync(Other, theirs, collidingScene));
    }

    [Fact]
    public async Task PruningIsIdempotent()
    {
        await using var host = new TestHost(Now);
        var store = host.Services.GetRequiredService<IFrameSessionStore>();
        var conv = Guid.NewGuid();

        await store.ApplyAsync(Request("enter", conv, "scene-a", Now.AddDays(-200)), "t1");
        await store.ApplyAsync(Request("exit", conv, "scene-a", Now.AddDays(-199)), "t2");

        Assert.Equal(1, await store.PruneAsync(Now.AddDays(-100), Now.AddDays(-365)));
        Assert.Equal(0, await store.PruneAsync(Now.AddDays(-100), Now.AddDays(-365)));
        Assert.Equal(0, await store.PruneAsync(Now.AddDays(-100), Now.AddDays(-365)));
    }

    [Fact]
    public async Task ConcurrentSweeps_DoNotDoubleCount()
    {
        await using var host = new TestHost(Now);
        var store = host.Services.GetRequiredService<IFrameSessionStore>();

        for (var i = 0; i < 5; i++)
        {
            var conv = Guid.NewGuid();
            await store.ApplyAsync(Request("enter", conv, $"scene-{i}", Now.AddDays(-200)), $"e{i}");
            await store.ApplyAsync(Request("exit", conv, $"scene-{i}", Now.AddDays(-199)), $"x{i}");
        }

        var sweeps = await Task.WhenAll(
            store.PruneAsync(Now.AddDays(-100), Now.AddDays(-365)),
            store.PruneAsync(Now.AddDays(-100), Now.AddDays(-365)));

        // However the two interleave, five sessions existed and five were removed.
        Assert.Equal(5, sweeps.Sum());
    }

    [Fact]
    public async Task AContentWithheldSession_PrunesLikeAnyOther()
    {
        // A sensitive turn records no evidence link (R-01). That must not make the session
        // invisible to the sweep — an unforgettable-because-empty row that also never ages
        // out would be the worst of both.
        await using var host = new TestHost(Now);
        var store = host.Services.GetRequiredService<IFrameSessionStore>();
        var conv = Guid.NewGuid();

        await store.ApplyAsync(
            Request("enter", conv, "scene-private", Now.AddDays(-200), evidence: null), "t1");
        await store.ApplyAsync(
            Request("exit", conv, "scene-private", Now.AddDays(-199), evidence: null), "t2");

        using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider
                .GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();
            var log = JsonSerializer.Deserialize<List<FrameTransitionEntry>>(
                (await db.FrameSessions.AsNoTracking().SingleAsync()).TransitionLogJson)!;
            Assert.All(log, e => Assert.Null(e.EvidenceMessageId));
        }

        Assert.Equal(1, await store.PruneAsync(Now.AddDays(-100), Now.AddDays(-365)));
    }

    [Fact]
    public async Task PruningSurvivesRestart_AndLeavesNoOrphanedBoundary()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"a4-{Guid.NewGuid():N}.db");
        try
        {
            await using (var host = new TestHost(Now, connectionString: $"Data Source={dbPath}"))
            {
                var store = host.Services.GetRequiredService<IFrameSessionStore>();
                var conv = Guid.NewGuid();
                await store.ApplyAsync(Request("enter", conv, "scene-x", Now.AddDays(-200)), "t1");
                await store.AddBoundaryAsync(new FrameBoundaryRecord
                {
                    UserId = User, ConversationId = conv, SceneRef = "scene-x",
                    Subject = "no third-person narration", StatedAt = Now.AddDays(-200),
                });
                await store.ApplyAsync(Request("exit", conv, "scene-x", Now.AddDays(-199)), "t2");
                Assert.Equal(2, await store.PruneAsync(Now.AddDays(-100), Now.AddDays(-365)));
            }

            await using (var host = new TestHost(Now, connectionString: $"Data Source={dbPath}"))
            {
                using var scope = host.CreateScope();
                var db = scope.ServiceProvider
                    .GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();

                // Neither survives, so no boundary is left scoped to a scene that is gone.
                Assert.Empty(await db.FrameSessions.AsNoTracking().ToListAsync());
                Assert.Empty(await db.FrameBoundaries.AsNoTracking().ToListAsync());
            }
        }
        finally
        {
            try { File.Delete(dbPath); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task ForgettingAndPruning_AreIndependent()
    {
        // Privacy forgetting reaches a session the sweep has not aged out, and the sweep
        // reaps a session nothing was ever forgotten from. Neither substitutes for the other.
        await using var host = new TestHost(Now);
        var store = host.Services.GetRequiredService<IFrameSessionStore>();
        var conv = Guid.NewGuid();
        var message = Guid.NewGuid();

        await store.ApplyAsync(Request("enter", conv, "scene-a", Now, evidence: message), "t1");

        Assert.Equal(0, await store.PruneAsync(Now.AddDays(-1), Now.AddDays(-1)));  // too young
        Assert.Equal(1, await store.ForgetByEvidenceAsync(User, [message], Now));   // still reached
        Assert.NotNull(await store.GetActiveAsync(User, conv));                     // frame survives
    }
}
