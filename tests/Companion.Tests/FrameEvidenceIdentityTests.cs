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
/// R-01. Frame state records WHICH turn moved the frame, never what was said in it.
///
/// The load-bearing property is negative and is asserted against the serialized database
/// column rather than against the object model: a test that only checked properties would
/// pass while the words sat in the JSON blob, which is exactly the failure this fixes.
/// </summary>
public class FrameEvidenceIdentityTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);

    private const string User = "usr-scott";
    private const string Other = "usr-someone-else";

    private static FrameTransitionRequest Request(
        string transition, Guid conv, Guid? evidence = null,
        string userId = User, string cause = "explicit", DateTimeOffset? at = null)
        => new()
        {
            UserId = userId,
            ConversationId = conv,
            Transition = transition,
            Cause = cause,
            At = at ?? Now,
            EvidenceMessageId = evidence,
        };

    private static async Task<string> TransitionLogOf(
        TestHost host, string userId, Guid conv)
    {
        using var scope = host.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();
        var session = await db.FrameSessions.AsNoTracking()
            .Where(s => s.UserId == userId && s.ConversationId == conv)
            .OrderByDescending(s => s.LastTransitionAt)
            .FirstAsync();
        return session.TransitionLogJson;
    }

    // ---- the contract carries identity, not text -------------------------------------------

    [Fact]
    public void TheTransitionEntry_HasNoTextEvidenceProperty()
    {
        // Structural, so the guarantee cannot be reintroduced by a later edit without this
        // failing: no property on the entry may carry free text evidence.
        var properties = typeof(FrameTransitionEntry).GetProperties();

        Assert.Contains(properties, p => p.Name == "EvidenceMessageId"
                                         && p.PropertyType == typeof(Guid?));
        Assert.DoesNotContain(properties, p => p.Name.Contains("Evidence", StringComparison.Ordinal)
                                               && p.PropertyType == typeof(string));
    }

    [Fact]
    public void TheBoundaryRecord_HasNoVerbatimStatement()
    {
        var properties = typeof(FrameBoundaryRecord).GetProperties();

        Assert.Contains(properties, p => p.Name == "EvidenceMessageId");
        Assert.DoesNotContain(properties, p => p.Name == "EvidenceStatement");
    }

    [Fact]
    public void TheTransitionRequest_CannotCarryText()
    {
        var properties = typeof(FrameTransitionRequest).GetProperties()
            .Where(p => p.Name.Contains("Evidence", StringComparison.Ordinal))
            .ToList();

        var evidence = Assert.Single(properties);
        Assert.Equal(typeof(Guid?), evidence.PropertyType);
    }

    // ---- sensitive turns, every transition kind ---------------------------------------------

    [Theory]
    [InlineData("enter")]
    [InlineData("continue")]
    [InlineData("switch")]
    [InlineData("exit")]
    public async Task ASensitiveTurn_PersistsNoEvidenceLink_ForAnyTransition(string transition)
    {
        await using var host = new TestHost(Now);
        var store = host.Services.GetRequiredService<IFrameSessionStore>();
        var conv = Guid.NewGuid();

        // Establish a session with a non-sensitive enter so continue/switch/exit have one.
        await store.ApplyAsync(Request("enter", conv, Guid.NewGuid()), "t-setup");

        // A sensitive turn passes null, which is what the turn pipeline does for it.
        await store.ApplyAsync(
            Request(transition, conv, evidence: null, at: Now.AddMinutes(1)), $"t-{transition}");

        var log = JsonSerializer.Deserialize<List<FrameTransitionEntry>>(
            await TransitionLogOf(host, User, conv))!;

        var entry = log.Last();
        Assert.Equal(transition, entry.Transition);
        Assert.Null(entry.EvidenceMessageId);

        // ...and the frame still moved. Privacy must not cost the lifecycle: the sensitive
        // turn is recorded as a transition exactly like any other, minus the link.
        Assert.Equal(2, log.Count);
        Assert.Equal("enter", log[0].Transition);
    }

    [Fact]
    public async Task ASensitiveEnter_StillCreatesAnActiveSession()
    {
        await using var host = new TestHost(Now);
        var store = host.Services.GetRequiredService<IFrameSessionStore>();
        var conv = Guid.NewGuid();

        var entered = await store.ApplyAsync(Request("enter", conv, evidence: null), "t1");

        Assert.True(entered.Applied);
        Assert.NotNull(await store.GetActiveAsync(User, conv));
        Assert.Null(JsonSerializer.Deserialize<List<FrameTransitionEntry>>(
            entered.Session!.TransitionLogJson)![0].EvidenceMessageId);
    }

    [Fact]
    public async Task ABoundaryFromASensitiveTurn_KeepsItsSubjectAndNoWording()
    {
        await using var host = new TestHost(Now);
        var store = host.Services.GetRequiredService<IFrameSessionStore>();
        var conv = Guid.NewGuid();

        var boundary = await store.AddBoundaryAsync(new FrameBoundaryRecord
        {
            UserId = User,
            ConversationId = conv,
            SceneRef = "scene-1",
            Subject = "no third-person narration",
            StatedAt = Now,
            EvidenceMessageId = null,      // sensitive: no link either
        });

        using var scope = host.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();
        var row = await db.FrameBoundaries.AsNoTracking().SingleAsync(b => b.Id == boundary.Id);

        // Enforcement still has what it needs: the structured subject.
        Assert.Equal("no third-person narration", row.Subject);
        Assert.Null(row.EvidenceMessageId);
    }

    // ---- exact-event forgetting ---------------------------------------------------------------

    [Fact]
    public async Task ForgettingOneEvent_SeversOnlyThatEntry()
    {
        await using var host = new TestHost(Now);
        var store = host.Services.GetRequiredService<IFrameSessionStore>();
        var conv = Guid.NewGuid();
        var doomed = Guid.NewGuid();
        var kept = Guid.NewGuid();

        await store.ApplyAsync(Request("enter", conv, doomed), "t1");
        await store.ApplyAsync(Request("continue", conv, kept, at: Now.AddMinutes(1)), "t2");

        Assert.Equal(1, await store.ForgetByEvidenceAsync(User, [doomed], Now.AddMinutes(2)));

        var log = JsonSerializer.Deserialize<List<FrameTransitionEntry>>(
            await TransitionLogOf(host, User, conv))!;

        Assert.Null(log[0].EvidenceMessageId);
        Assert.Equal(kept, log[1].EvidenceMessageId);

        // Operational state is untouched: the frame history still reads correctly.
        Assert.Equal(["enter", "continue"], log.Select(e => e.Transition));
        Assert.Equal("explicit", log[0].Cause);
    }

    [Fact]
    public async Task TwoTransitionsFromTheSameEvent_AreBothSevered()
    {
        // "Same text from different events" and its mirror: one event may legitimately cause
        // more than one recorded transition, and forgetting it must reach every one.
        await using var host = new TestHost(Now);
        var store = host.Services.GetRequiredService<IFrameSessionStore>();
        var conv = Guid.NewGuid();
        var shared = Guid.NewGuid();

        await store.ApplyAsync(Request("enter", conv, shared), "t1");
        await store.ApplyAsync(Request("continue", conv, shared, at: Now.AddMinutes(1)), "t2");

        Assert.Equal(2, await store.ForgetByEvidenceAsync(User, [shared], Now.AddMinutes(2)));

        var log = JsonSerializer.Deserialize<List<FrameTransitionEntry>>(
            await TransitionLogOf(host, User, conv))!;
        Assert.All(log, e => Assert.Null(e.EvidenceMessageId));
    }

    [Fact]
    public async Task DifferentEventsAreNeverConflated_EvenAcrossSessions()
    {
        await using var host = new TestHost(Now);
        var store = host.Services.GetRequiredService<IFrameSessionStore>();
        var convA = Guid.NewGuid();
        var convB = Guid.NewGuid();
        var doomed = Guid.NewGuid();
        var kept = Guid.NewGuid();

        await store.ApplyAsync(Request("enter", convA, doomed), "a1");
        await store.ApplyAsync(Request("enter", convB, kept), "b1");

        Assert.Equal(1, await store.ForgetByEvidenceAsync(User, [doomed], Now.AddMinutes(1)));

        var a = JsonSerializer.Deserialize<List<FrameTransitionEntry>>(
            await TransitionLogOf(host, User, convA))!;
        var b = JsonSerializer.Deserialize<List<FrameTransitionEntry>>(
            await TransitionLogOf(host, User, convB))!;

        Assert.Null(a[0].EvidenceMessageId);
        Assert.Equal(kept, b[0].EvidenceMessageId);
    }

    [Fact]
    public async Task RepeatedForgetting_IsIdempotent()
    {
        await using var host = new TestHost(Now);
        var store = host.Services.GetRequiredService<IFrameSessionStore>();
        var conv = Guid.NewGuid();
        var doomed = Guid.NewGuid();

        await store.ApplyAsync(Request("enter", conv, doomed), "t1");

        Assert.Equal(1, await store.ForgetByEvidenceAsync(User, [doomed], Now.AddMinutes(1)));
        Assert.Equal(0, await store.ForgetByEvidenceAsync(User, [doomed], Now.AddMinutes(2)));
        Assert.Equal(0, await store.ForgetByEvidenceAsync(User, [doomed], Now.AddMinutes(3)));
    }

    [Fact]
    public async Task ForgettingIsUserScoped_EvenWithCollidingEventIds()
    {
        await using var host = new TestHost(Now);
        var store = host.Services.GetRequiredService<IFrameSessionStore>();
        var conv = Guid.NewGuid();          // same conversation id
        var evidence = Guid.NewGuid();      // and the same evidence id

        await store.ApplyAsync(Request("enter", conv, evidence), "t1");
        await store.ApplyAsync(Request("enter", conv, evidence, userId: Other), "t1-other");

        Assert.Equal(1, await store.ForgetByEvidenceAsync(User, [evidence], Now.AddMinutes(1)));

        var mine = JsonSerializer.Deserialize<List<FrameTransitionEntry>>(
            await TransitionLogOf(host, User, conv))!;
        var theirs = JsonSerializer.Deserialize<List<FrameTransitionEntry>>(
            await TransitionLogOf(host, Other, conv))!;

        Assert.Null(mine[0].EvidenceMessageId);
        Assert.Equal(evidence, theirs[0].EvidenceMessageId);
    }

    // ---- the serialized column itself ---------------------------------------------------------

    [Fact]
    public async Task TheSerializedTransitionLog_ContainsOnlyStructuralValues()
    {
        await using var host = new TestHost(Now);
        var store = host.Services.GetRequiredService<IFrameSessionStore>();
        var conv = Guid.NewGuid();

        await store.ApplyAsync(Request("enter", conv, Guid.NewGuid()), "t1");
        await store.ApplyAsync(
            Request("continue", conv, Guid.NewGuid(), at: Now.AddMinutes(1)), "t2");

        using var scope = host.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();

        // Read EVERY text column of the frame tables and prove the words are absent.
        var sessions = await db.FrameSessions.AsNoTracking().ToListAsync();
        var haystack = string.Join("\n", sessions.Select(s => string.Join("|",
            s.SceneRef, s.CharactersJson, s.ActiveCompanionCharacterId,
            s.Narration, s.Continuity, s.NarratorKind, s.NarratorCharacterId,
            s.ViewpointCharacterId, s.Person, s.AppliedKeysJson, s.TransitionLogJson)));

        // The old excerpt key is gone from the serialized form entirely.
        Assert.DoesNotContain("\"Evidence\"", haystack, StringComparison.Ordinal);

        // And every value in the log is structural: a closed transition token, a timestamp,
        // a content-safe cause, and a guid or null. Anything else would be prose.
        foreach (var session in sessions)
        {
            using var doc = JsonDocument.Parse(session.TransitionLogJson);
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                Assert.Contains(entry.GetProperty("Transition").GetString(),
                    new[] { "enter", "continue", "switch", "exit" });
                Assert.True(entry.GetProperty("At").TryGetDateTimeOffset(out _));
                var id = entry.GetProperty("EvidenceMessageId");
                Assert.True(id.ValueKind == JsonValueKind.Null
                            || Guid.TryParse(id.GetString(), out _));
            }
        }
    }

    [Fact]
    public async Task FrameStateSurvivesARestart_WithEvidenceStillSevered()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"frame-restart-{Guid.NewGuid():N}.db");
        var conv = Guid.NewGuid();
        var doomed = Guid.NewGuid();
        try
        {
            await using (var host = new TestHost(Now, connectionString: $"Data Source={dbPath}"))
            {
                var store = host.Services.GetRequiredService<IFrameSessionStore>();
                await store.ApplyAsync(Request("enter", conv, doomed), "t1");
                Assert.Equal(1, await store.ForgetByEvidenceAsync(User, [doomed], Now.AddMinutes(1)));
            }

            // A second process against the same file: forgetting is durable, not in-memory.
            await using (var host = new TestHost(Now, connectionString: $"Data Source={dbPath}"))
            {
                var store = host.Services.GetRequiredService<IFrameSessionStore>();

                var active = await store.GetActiveAsync(User, conv);
                Assert.NotNull(active);              // the frame itself resumed

                var log = JsonSerializer.Deserialize<List<FrameTransitionEntry>>(
                    active!.TransitionLogJson)!;
                Assert.Null(log[0].EvidenceMessageId);
                Assert.Equal("enter", log[0].Transition);

                // And it stays idempotent across the restart.
                Assert.Equal(0, await store.ForgetByEvidenceAsync(User, [doomed], Now.AddMinutes(2)));
            }
        }
        finally
        {
            try { File.Delete(dbPath); } catch (IOException) { }
        }
    }

    // ---- the pure severing rule ------------------------------------------------------------

    [Fact]
    public void SeverTransitionEvidence_MatchesOnIdentityOnly()
    {
        var doomed = Guid.NewGuid();
        var kept = Guid.NewGuid();
        var log = new List<FrameTransitionEntry>
        {
            new("enter", Now, "explicit", doomed),
            new("continue", Now, "in-character", kept),
            new("exit", Now, "explicit-exit", null),
        };

        Assert.Equal(1, FrameIsolation.SeverTransitionEvidence(log, [doomed]));

        Assert.Null(log[0].EvidenceMessageId);
        Assert.Equal(kept, log[1].EvidenceMessageId);
        Assert.Equal(0, FrameIsolation.SeverTransitionEvidence(log, [doomed]));
    }

    [Fact]
    public void SeverTransitionEvidence_TakesIdentitiesOnly()
    {
        var method = typeof(FrameIsolation)
            .GetMethod(nameof(FrameIsolation.SeverTransitionEvidence))!;

        // No string parameter at all: there is nothing here that could become a text matcher.
        Assert.DoesNotContain(method.GetParameters(), p => p.ParameterType == typeof(string));
    }

    // ---- the real /forget path drives it ------------------------------------------------------

    [Fact]
    public async Task TheRealForgetPath_SeversFrameEvidence()
    {
        // The gap R-01 names is precisely that this fan-out did not exist: every store-level
        // test above could pass while /forget never called any of it.
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;

        var conversations = sp.GetRequiredService<IConversationStore>();
        var memories = sp.GetRequiredService<IMemoryStore>();
        var frames = sp.GetRequiredService<IFrameSessionStore>();

        var conversation = await conversations.StartConversationAsync(User, "t", "mock", "test");
        var message = new Message
        {
            Id = Guid.NewGuid(), UserId = User, ConversationId = conversation.Id,
            Role = MessageRole.User, Content = "let's play a scene", Timestamp = Now,
        };
        await conversations.AddMessageAsync(message);

        // A frame transition caused by THAT message, and a boundary stated in it.
        await frames.ApplyAsync(new FrameTransitionRequest
        {
            UserId = User,
            ConversationId = conversation.Id,
            Transition = "enter",
            Cause = "explicit",
            At = Now,
            EvidenceMessageId = message.Id,
        }, "turn-1");

        var active = await frames.GetActiveAsync(User, conversation.Id);
        await frames.AddBoundaryAsync(new FrameBoundaryRecord
        {
            UserId = User,
            ConversationId = conversation.Id,
            SceneRef = active!.SceneRef,
            Subject = "no third-person narration",
            StatedAt = Now,
            EvidenceMessageId = message.Id,
        });

        var memoryId = Guid.NewGuid();
        await memories.AddSemanticAsync(new SemanticMemory
        {
            Id = memoryId, UserId = User, Subject = "user", Predicate = "wanted",
            Value = "a scene", NormalizedFact = "The user wanted to play a scene.",
            FirstObserved = Now, LastConfirmed = Now, CreatedAt = Now,
        });
        await memories.AddEvidenceAsync(User,
        [
            new MemoryEvidence
            {
                Id = Guid.NewGuid(), UserId = User, MemoryId = memoryId,
                MemoryKind = MemoryKind.Semantic, MessageId = message.Id,
                Excerpt = "let's play a scene",
            },
        ]);

        Assert.True(await sp.GetRequiredService<IMemoryCurator>()
            .ForgetAsync(User, memoryId, "user asked to forget"));

        // The transition log lost its link...
        var log = JsonSerializer.Deserialize<List<FrameTransitionEntry>>(
            await TransitionLogOf(host, User, conversation.Id))!;
        Assert.Null(log[0].EvidenceMessageId);

        // ...the boundary was invalidated by exact identity...
        var db = sp.GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();
        var boundary = await db.FrameBoundaries.AsNoTracking()
            .SingleAsync(b => b.UserId == User);
        Assert.Equal(FrameBoundaryStatus.EvidenceForgotten, boundary.Status);

        // ...and the frame itself is still there, because forgetting what was said is not
        // the same as pretending the scene never happened.
        Assert.NotNull(await frames.GetActiveAsync(User, conversation.Id));
        Assert.Equal("enter", log[0].Transition);
    }

    [Fact]
    public void TheCuratorTakesTheFrameStore()
    {
        // Guards the wiring itself: the fan-out is only real if the dependency is there.
        var frames = typeof(MemoryCurator).GetConstructors().Single()
            .GetParameters()
            .SingleOrDefault(p => p.ParameterType == typeof(IFrameSessionStore));

        Assert.NotNull(frames);
    }
}
