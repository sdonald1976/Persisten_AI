using System.Text.Json;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Infrastructure.Seeding;
using Companion.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Frame persistence: concurrency, idempotency, exact-identity forget, retention, and
/// cross-user isolation — the same five properties every other store here had to earn.
/// </summary>
public class FrameSessionStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private const string User = CompanionSeeder.DemoUserId;
    private const string Other = "usr-someone-else";

    private static FrameTransitionRequest Request(
        string transition, Guid conversationId, string userId = User,
        string? scene = "scene-1", string cause = "explicit", DateTimeOffset? at = null)
        => new()
        {
            UserId = userId,
            ConversationId = conversationId,
            Transition = transition,
            Cause = cause,
            At = at ?? Now,
            SceneRef = scene,
            Narration = "licensed",
            Continuity = "maintain",
        };

    private static FrameBoundaryRecord Boundary(
        Guid conversationId, string scene = "scene-1", Guid? evidenceMessageId = null,
        string userId = User, string subject = "no third-person narration")
        => new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ConversationId = conversationId,
            SceneRef = scene,
            Subject = subject,
            StatedAt = Now,
            EvidenceMessageId = evidenceMessageId,
        };

    // ---- lifecycle ------------------------------------------------------------------------

    [Fact]
    public async Task EnterCreatesASession_AndExitEndsIt()
    {
        await using var host = new TestHost(Now);
        var store = host.Services.GetRequiredService<IFrameSessionStore>();
        var conv = Guid.NewGuid();

        var entered = await store.ApplyAsync(Request("enter", conv), "turn-1");
        Assert.True(entered.Applied);
        Assert.Equal(FrameSessionStatus.Active, entered.Session!.Status);
        Assert.NotNull(await store.GetActiveAsync(User, conv));

        var exited = await store.ApplyAsync(Request("exit", conv, at: Now.AddMinutes(5)), "turn-2");
        Assert.True(exited.Applied);
        Assert.Equal(FrameSessionStatus.Ended, exited.Session!.Status);
        Assert.Null(await store.GetActiveAsync(User, conv));
    }

    [Fact]
    public async Task TransitionsWithNoSession_DoNothing()
    {
        await using var host = new TestHost(Now);
        var store = host.Services.GetRequiredService<IFrameSessionStore>();

        foreach (var t in new[] { "continue", "switch", "exit" })
        {
            var r = await store.ApplyAsync(Request(t, Guid.NewGuid()), $"turn-{t}");
            Assert.False(r.Applied);
            Assert.Null(r.Session);
        }
    }

    [Fact]
    public async Task TheTransitionLog_RecordsEveryStepWithTheEventThatCausedIt()
    {
        await using var host = new TestHost(Now);
        var store = host.Services.GetRequiredService<IFrameSessionStore>();
        var conv = Guid.NewGuid();

        var enterMsg = Guid.NewGuid();
        var exitMsg = Guid.NewGuid();
        await store.ApplyAsync(Request("enter", conv) with { EvidenceMessageId = enterMsg }, "t1");
        await store.ApplyAsync(Request("continue", conv), "t2");
        var final = await store.ApplyAsync(
            Request("exit", conv, cause: "explicit-exit") with { EvidenceMessageId = exitMsg }, "t3");

        var log = JsonSerializer.Deserialize<List<FrameTransitionEntry>>(
            final.Session!.TransitionLogJson)!;

        // "never entered" and "stayed in after I said stop" are separable afterwards, which
        // is the whole reason this log exists.
        Assert.Equal(["enter", "continue", "exit"], log.Select(e => e.Transition));
        Assert.Equal(enterMsg, log[0].EvidenceMessageId);
        Assert.Equal(exitMsg, log[2].EvidenceMessageId);
        Assert.Equal("explicit-exit", log[2].Cause);
    }

    // ---- idempotency ----------------------------------------------------------------------

    [Fact]
    public async Task ReplayingATurn_DoesNotTransitionTwice()
    {
        await using var host = new TestHost(Now);
        var store = host.Services.GetRequiredService<IFrameSessionStore>();
        var conv = Guid.NewGuid();
        await store.ApplyAsync(Request("enter", conv), "turn-1");

        var first = await store.ApplyAsync(Request("continue", conv), "turn-2");
        var replay = await store.ApplyAsync(Request("continue", conv), "turn-2");

        Assert.True(first.Applied);
        Assert.False(replay.Applied);
        Assert.Equal(first.Session!.Version, replay.Session!.Version);

        var log = JsonSerializer.Deserialize<List<FrameTransitionEntry>>(
            replay.Session.TransitionLogJson)!;
        Assert.Equal(2, log.Count);      // enter + one continue, not two
    }

    [Fact]
    public async Task ReEnteringAnActiveFrame_ContinuesItRatherThanStartingASecond()
    {
        await using var host = new TestHost(Now);
        var store = host.Services.GetRequiredService<IFrameSessionStore>();
        var conv = Guid.NewGuid();

        var first = await store.ApplyAsync(Request("enter", conv), "t1");
        var second = await store.ApplyAsync(Request("enter", conv), "t2");

        Assert.Equal(first.Session!.SessionId, second.Session!.SessionId);

        using var scope = host.CreateScope();
        var count = await scope.ServiceProvider
            .GetRequiredService<Infrastructure.Persistence.CompanionDbContext>()
            .FrameSessions.CountAsync(s => s.ConversationId == conv);
        Assert.Equal(1, count);
    }

    // ---- concurrency ------------------------------------------------------------------------

    [Fact]
    public async Task ConcurrentTransitions_AllLandExactlyOnce_WithAMonotonicVersion()
    {
        await using var host = new TestHost(Now);
        var store = host.Services.GetRequiredService<IFrameSessionStore>();
        var conv = Guid.NewGuid();
        await store.ApplyAsync(Request("enter", conv), "t0");

        // Ten simultaneous continues with distinct keys. None may be lost silently.
        var results = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(i => store.ApplyAsync(Request("continue", conv), $"turn-{i}")));

        var applied = results.Count(r => r.Applied);
        var conflicted = results.Count(r => r.Conflicted);
        Assert.Equal(10, applied + conflicted);

        // Whatever the interleaving, the surviving session is coherent: one session, a
        // version that counts what actually applied, and a log to match.
        var session = await store.GetActiveAsync(User, conv);
        Assert.NotNull(session);
        var log = JsonSerializer.Deserialize<List<FrameTransitionEntry>>(session!.TransitionLogJson)!;
        Assert.Equal(applied + 1, log.Count);          // + the enter
        Assert.Equal(applied + 1, session.Version);
    }

    // ---- boundaries: scene-scoped, ended not deleted -------------------------------------------

    [Fact]
    public async Task ExitingAScene_EndsItsBoundaries_AndLeavesOtherScenesAlone()
    {
        await using var host = new TestHost(Now);
        var store = host.Services.GetRequiredService<IFrameSessionStore>();
        var conv = Guid.NewGuid();

        await store.ApplyAsync(Request("enter", conv), "t1");
        var mine = await store.AddBoundaryAsync(Boundary(conv));
        var elsewhere = await store.AddBoundaryAsync(Boundary(conv, "scene-2"));

        Assert.Single(await store.GetActiveBoundariesAsync(User, conv, "scene-1"));

        await store.ApplyAsync(Request("exit", conv, at: Now.AddMinutes(3)), "t2");

        Assert.Empty(await store.GetActiveBoundariesAsync(User, conv, "scene-1"));
        Assert.Single(await store.GetActiveBoundariesAsync(User, conv, "scene-2"));

        // Ended, not deleted: the evidence survives so "she ignored my boundary" stays
        // answerable after the scene is over.
        using var scope = host.CreateScope();
        var row = await scope.ServiceProvider
            .GetRequiredService<Infrastructure.Persistence.CompanionDbContext>()
            .FrameBoundaries.AsNoTracking().SingleAsync(b => b.Id == mine.Id);
        Assert.Equal(FrameBoundaryStatus.FrameEnded, row.Status);
        Assert.Equal("no third-person narration", row.Subject);
        Assert.NotNull(row.DeactivatedAt);
        _ = elsewhere;
    }

    // ---- /forget by exact identity ---------------------------------------------------------------

    [Fact]
    public async Task ForgettingEvidence_RedactsByIdentity()
    {
        await using var host = new TestHost(Now);
        var store = host.Services.GetRequiredService<IFrameSessionStore>();
        var conv = Guid.NewGuid();
        var forgotten = Guid.NewGuid();

        var doomed = await store.AddBoundaryAsync(Boundary(conv, evidenceMessageId: forgotten));
        // Same scene, identical wording, different evidence: identity decides, not resemblance.
        var kept = await store.AddBoundaryAsync(Boundary(conv, evidenceMessageId: Guid.NewGuid()));

        Assert.Equal(1, await store.ForgetByEvidenceAsync(User, [forgotten], Now));
        Assert.Equal(0, await store.ForgetByEvidenceAsync(User, [forgotten], Now.AddDays(1)));

        using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();
        var gone = await db.FrameBoundaries.AsNoTracking().SingleAsync(b => b.Id == doomed.Id);
        Assert.Equal(FrameBoundaryStatus.EvidenceForgotten, gone.Status);

        var survivor = await db.FrameBoundaries.AsNoTracking().SingleAsync(b => b.Id == kept.Id);
        Assert.Equal(FrameBoundaryStatus.Active, survivor.Status);
        Assert.NotNull(survivor.EvidenceMessageId);
    }

    [Fact]
    public void TheForgetSignature_TakesIdentitiesOnly()
    {
        var method = typeof(IFrameSessionStore).GetMethod(nameof(IFrameSessionStore.ForgetByEvidenceAsync))!;

        // Only the user id is a string, and it is the isolation scope rather than a matcher.
        Assert.Single(method.GetParameters().Where(p => p.ParameterType == typeof(string)));
        var ids = Assert.Single(method.GetParameters().Where(p => p.ParameterType.IsGenericType));
        Assert.Equal(typeof(Guid), ids.ParameterType.GetGenericArguments()[0]);
    }

    // ---- cross-user isolation ---------------------------------------------------------------------

    [Fact]
    public async Task OneUsersFrame_IsInvisibleAndUntouchableFromAnother()
    {
        await using var host = new TestHost(Now);
        var store = host.Services.GetRequiredService<IFrameSessionStore>();
        var conv = Guid.NewGuid();          // deliberately the SAME conversation id
        var evidence = Guid.NewGuid();      // and the SAME evidence id

        await store.ApplyAsync(Request("enter", conv), "t1");
        await store.ApplyAsync(Request("enter", conv, userId: Other), "t1-other");
        await store.AddBoundaryAsync(Boundary(conv, evidenceMessageId: evidence));
        var theirs = await store.AddBoundaryAsync(
            Boundary(conv, evidenceMessageId: evidence, userId: Other));

        // Forgetting for one user touches exactly one row despite the colliding ids.
        Assert.Equal(1, await store.ForgetByEvidenceAsync(User, [evidence], Now));

        using var scope = host.CreateScope();
        var row = await scope.ServiceProvider
            .GetRequiredService<Infrastructure.Persistence.CompanionDbContext>()
            .FrameBoundaries.AsNoTracking().SingleAsync(b => b.Id == theirs.Id);
        Assert.Equal(FrameBoundaryStatus.Active, row.Status);
        Assert.NotNull(row.EvidenceMessageId);

        // ...and exiting one user's frame leaves the other's active.
        await store.ApplyAsync(Request("exit", conv, at: Now.AddMinutes(1)), "t2");
        Assert.Null(await store.GetActiveAsync(User, conv));
        Assert.NotNull(await store.GetActiveAsync(Other, conv));
    }

    // ---- retention -------------------------------------------------------------------------------

    [Fact]
    public async Task PruningRemovesEndedSessionsAndTheirBoundaries_ButNeverLiveOnes()
    {
        await using var host = new TestHost(Now);
        var store = host.Services.GetRequiredService<IFrameSessionStore>();
        var oldConv = Guid.NewGuid();
        var liveConv = Guid.NewGuid();

        await store.ApplyAsync(Request("enter", oldConv, scene: "scene-old", at: Now.AddDays(-200)), "o1");
        await store.AddBoundaryAsync(Boundary(oldConv, "scene-old"));
        await store.ApplyAsync(Request("exit", oldConv, scene: "scene-old", at: Now.AddDays(-199)), "o2");

        await store.ApplyAsync(Request("enter", liveConv, scene: "scene-live"), "l1");
        await store.AddBoundaryAsync(Boundary(liveConv, "scene-live"));

        var removed = await store.PruneAsync(Now.AddDays(-180));

        Assert.Equal(2, removed);                                  // the session and its boundary
        Assert.NotNull(await store.GetActiveAsync(User, liveConv));
        Assert.Single(await store.GetActiveBoundariesAsync(User, liveConv, "scene-live"));
    }
}
