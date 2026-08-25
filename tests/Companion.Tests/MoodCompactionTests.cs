using System.Text.Json;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Companion.Infrastructure.Seeding;
using Companion.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The privacy-compaction boundary (contract decision, 2026-08-25).
///
/// In-place redaction was not enough: her spirits trajectory is a deterministic function of
/// the valences that moved it, so a nulled transition's NEIGHBOURS reconstructed it exactly.
/// Compaction is therefore TOTAL — the whole chain is deleted and replaced by a single opaque
/// baseline carrying where she actually stands. Cutting only at-or-before the boundary was
/// tried and does not work: the row immediately after the cut carries the boundary's own
/// result as its PreviousSpirits, so the forgotten value falls straight back out.
///
/// Her present mood is deliberately NOT rewound. Forgetting the record of a moment does not
/// undo having been affected by it; what it removes is every row the forgotten valence could
/// be recomputed from.
///
/// The central assertion in this file is the INVERTED form of the limitation that used to be
/// characterised here: the forgotten valence must not be recoverable from any persisted row.
/// </summary>
public class MoodCompactionTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private const string User = CompanionSeeder.DemoUserId;
    private const string Other = "usr-someone-else";

    /// <summary>The forgotten moment, distinctive enough to search a whole database for.</summary>
    private const double SecretValence = -0.87;

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"compaction-{Guid.NewGuid():N}.db");

    private string ConnectionString => $"Data Source={_dbPath}";

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch (IOException) { }
    }

    /// <summary>
    /// Every way a valence can be solved for out of a set of transitions: from a row's own
    /// two endpoints, and from any adjacent pair's endpoints. This is the attack the
    /// compaction has to defeat, written once and pointed at every scenario.
    /// </summary>
    private static bool ValenceIsRecoverable(
        IReadOnlyList<CompanionMoodTransition> rows, double target)
    {
        const double w = MoodReplay.NudgeWeight;
        const double tolerance = 1e-6;

        bool Matches(double candidate) => Math.Abs(candidate - target) < tolerance;

        var ordered = rows.OrderBy(t => t.Version).ToList();

        foreach (var t in ordered)
        {
            // Directly stored.
            if (t.AppliedValence is { } stored && Matches(stored))
                return true;

            // Solved from the row's own endpoints.
            if (t.PreviousSpirits is { } p && Matches((t.NewSpirits - p * (1 - w)) / w))
                return true;
        }

        // Solved from any pair of endpoints the log still exposes, adjacent or not — a
        // deliberately generous attacker.
        var known = ordered
            .SelectMany(t => new[] { t.PreviousSpirits, (double?)t.NewSpirits })
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .Distinct()
            .ToList();

        foreach (var before in known)
            foreach (var after in known)
                if (Matches((after - before * (1 - w)) / w))
                    return true;

        return false;
    }

    private static async Task<Guid> NudgeAsync(
        IServiceProvider sp, double valence, string userId = User)
    {
        var evidenceEventId = Guid.NewGuid();
        await sp.GetRequiredService<ICompanionStateTracker>()
            .NudgeAsync(userId, valence, evidenceEventId);
        return evidenceEventId;
    }

    // ---- the core property, at every position in the chain -------------------------------

    public static TheoryData<int> ForgottenPositions => new() { 0, 2, 4 };

    [Theory]
    [MemberData(nameof(ForgottenPositions))]
    public async Task TheForgottenValence_IsUnrecoverable_WhereverItSatInTheChain(int position)
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var log = sp.GetRequiredService<ICompanionMoodLog>();

        // Five real moments; exactly one of them is the secret. Position 0 = first,
        // 2 = middle, 4 = latest.
        var valences = new[] { 0.5, -0.2, 0.7, -0.4, 0.3 };
        valences[position] = SecretValence;

        var events = new List<Guid>();
        foreach (var v in valences)
        {
            events.Add(await NudgeAsync(sp, v));
            host.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        // Before: it is sitting right there, as the old redaction-only design left it.
        Assert.True(ValenceIsRecoverable(await log.GetHistoryAsync(User), SecretValence));

        var spiritsBefore = (await sp.GetRequiredService<ICompanionStateTracker>()
            .BuildAsync(User)).Spirits;

        var result = await log.CompactForgottenAsync(
            User, [events[position]], spiritsBefore, Now);
        Assert.True(result.Compacted);
        // Compaction is TOTAL: a row surviving next to the boundary derives it, so every
        // transition goes and one baseline stands in their place.
        Assert.Equal(valences.Length, result.RowsRemoved);

        var after = await log.GetHistoryAsync(User);

        // THE INVERTED PROPERTY: no persisted row, alone or paired with any other, yields it.
        Assert.False(ValenceIsRecoverable(after, SecretValence),
            $"the forgotten valence was still recoverable after compacting position {position}");

        // ...and it is nowhere in the serialized rows either.
        Assert.DoesNotContain("0.87", JsonSerializer.Serialize(after));

        // The baseline is opaque: no predecessor, no valence, no source event.
        var baseline = Assert.Single(after, t => t.IsBaseline);
        Assert.Null(baseline.PreviousSpirits);
        Assert.Null(baseline.AppliedValence);
        Assert.Null(baseline.SourceEvidenceEventId);
        Assert.Equal(Now, baseline.CompactedAt);
    }

    // ---- her present mood is preserved, not rewound --------------------------------------

    [Fact]
    public async Task CompactionPreservesHerPresentMood_AsAnOpaqueBaseline()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var tracker = sp.GetRequiredService<ICompanionStateTracker>();
        var log = sp.GetRequiredService<ICompanionMoodLog>();

        var forgotten = await NudgeAsync(sp, SecretValence);
        await NudgeAsync(sp, 0.4);
        var spirits = (await tracker.BuildAsync(User)).Spirits;

        await log.CompactForgottenAsync(User, [forgotten], spirits, Now);

        // She is exactly where she was. Being affected happened; only the record of why is gone.
        var baseline = Assert.Single(await log.GetHistoryAsync(User));
        Assert.True(baseline.IsBaseline);
        Assert.Equal(spirits, baseline.NewSpirits, 6);
        Assert.Equal(spirits, (await tracker.BuildAsync(User)).Spirits, 6);
    }

    // ---- later transitions continue from the baseline ------------------------------------

    [Fact]
    public async Task LaterTransitions_ContinueFromTheBaseline_WithContiguousVersions()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var log = sp.GetRequiredService<ICompanionMoodLog>();

        var forgotten = await NudgeAsync(sp, SecretValence);
        await NudgeAsync(sp, 0.4);
        var spirits = (await sp.GetRequiredService<ICompanionStateTracker>().BuildAsync(User)).Spirits;
        var compaction = await log.CompactForgottenAsync(User, [forgotten], spirits, Now);

        await NudgeAsync(sp, 0.6);
        await NudgeAsync(sp, -0.1);

        var history = await log.GetHistoryAsync(User);
        // The baseline plus exactly the two transitions that came after it.
        Assert.Equal(3, history.Count);
        Assert.Equal(compaction.BaselineVersion, history[0].Version);
        Assert.Equal(compaction.BaselineVersion + 1, history[1].Version);
        Assert.Equal(compaction.BaselineVersion + 2, history[2].Version);
        // The first post-baseline transition starts exactly where the baseline left her.
        Assert.Equal(history[0].NewSpirits, history[1].PreviousSpirits!.Value, 6);
        Assert.False(ValenceIsRecoverable(history, SecretValence));
    }

    // ---- replay across the boundary is unavailable, and says so ---------------------------

    [Fact]
    public async Task ReplayAcrossTheBoundary_IsUnavailable_AndDiagnosedRatherThanApproximated()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var log = sp.GetRequiredService<ICompanionMoodLog>();

        await NudgeAsync(sp, 0.5);
        var forgotten = await NudgeAsync(sp, SecretValence);
        await NudgeAsync(sp, 0.3);

        // Before compaction the whole history replays exactly.
        var before = MoodReplay.Replay(await log.GetHistoryAsync(User));
        Assert.True(before.CoversFullHistory);
        Assert.Null(before.Diagnosis);

        var spirits = (await sp.GetRequiredService<ICompanionStateTracker>().BuildAsync(User)).Spirits;
        await log.CompactForgottenAsync(User, [forgotten], spirits, Now);
        await NudgeAsync(sp, -0.25);

        var after = MoodReplay.Replay(await log.GetHistoryAsync(User));
        // Replay from the baseline forward still works and is exact...
        Assert.NotNull(after.Spirits);
        // ...but it does not cover the full history, and says so plainly.
        Assert.False(after.CoversFullHistory);
        Assert.Contains("compacted at version", after.Diagnosis);
        Assert.Contains("removed to sever a forgotten moment", after.Diagnosis);
    }

    // ---- restart --------------------------------------------------------------------------

    [Fact]
    public async Task TheBoundarySurvivesARestart_AndTheValenceStaysUnrecoverable()
    {
        Guid forgotten;
        double spirits;

        await using (var host = new TestHost(Now, connectionString: ConnectionString))
        {
            using var scope = host.CreateScope();
            var sp = scope.ServiceProvider;
            await NudgeAsync(sp, 0.5);
            forgotten = await NudgeAsync(sp, SecretValence);
            await NudgeAsync(sp, 0.3);
            spirits = (await sp.GetRequiredService<ICompanionStateTracker>().BuildAsync(User)).Spirits;

            var result = await sp.GetRequiredService<ICompanionMoodLog>()
                .CompactForgottenAsync(User, [forgotten], spirits, Now);
            Assert.True(result.Compacted);
        }

        await using (var restarted = new TestHost(Now, connectionString: ConnectionString))
        {
            using var scope = restarted.CreateScope();
            var sp = scope.ServiceProvider;
            var history = await sp.GetRequiredService<ICompanionMoodLog>().GetHistoryAsync(User);

            // The deleted rows are genuinely gone from disk, not merely filtered on read.
            var rows = await sp.GetRequiredService<Infrastructure.Persistence.CompanionDbContext>()
                .CompanionMoodTransitions.AsNoTracking().Where(t => t.UserId == User).ToListAsync();
            Assert.Single(rows);
            Assert.True(rows[0].IsBaseline);
            Assert.Null(rows[0].PreviousSpirits);
            Assert.Null(rows[0].AppliedValence);

            Assert.False(ValenceIsRecoverable(history, SecretValence));
            Assert.Equal(spirits, rows[0].NewSpirits, 6);
            Assert.False(MoodReplay.Replay(history).CoversFullHistory);
        }
    }

    // ---- concurrency: a nudge racing a forget ---------------------------------------------

    [Fact]
    public async Task AConcurrentNudgeAndCompaction_LeaveNoRecoverableValence_AndAValidChain()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var log = sp.GetRequiredService<ICompanionMoodLog>();

        await NudgeAsync(sp, 0.5);
        var forgotten = await NudgeAsync(sp, SecretValence);
        var spirits = (await sp.GetRequiredService<ICompanionStateTracker>().BuildAsync(User)).Spirits;

        // Six nudges landing while the compaction runs. Whichever order the database
        // serialises them in, the secret must not survive and the chain must stay usable.
        var compaction = log.CompactForgottenAsync(User, [forgotten], spirits, Now);
        var nudges = Enumerable.Range(0, 6).Select(i => NudgeAsync(sp, 0.1 * (i + 1))).ToArray();
        await Task.WhenAll(nudges.Cast<Task>().Append(compaction));

        var history = await log.GetHistoryAsync(User);

        Assert.False(ValenceIsRecoverable(history, SecretValence));
        // Exactly one baseline, versions unique, and nothing before the baseline survived.
        Assert.Single(history, t => t.IsBaseline);
        Assert.Equal(history.Count, history.Select(t => t.Version).Distinct().Count());
        var baseline = history.Single(t => t.IsBaseline);
        Assert.DoesNotContain(history, t => t.Version < baseline.Version);
    }

    // ---- cross-user isolation ---------------------------------------------------------------

    [Fact]
    public async Task CompactingOneUser_LeavesAnotherUsersMoodHistoryEntirelyIntact()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var log = sp.GetRequiredService<ICompanionMoodLog>();

        // Both users live the same three moments, including the same valence.
        var mine = new List<Guid>();
        var theirs = new List<Guid>();
        foreach (var v in new[] { 0.5, SecretValence, 0.3 })
        {
            mine.Add(await NudgeAsync(sp, v));
            theirs.Add(await NudgeAsync(sp, v, Other));
        }

        var spirits = (await sp.GetRequiredService<ICompanionStateTracker>().BuildAsync(User)).Spirits;
        var result = await log.CompactForgottenAsync(User, [mine[1]], spirits, Now);
        Assert.True(result.Compacted);

        // Mine is compacted...
        Assert.False(ValenceIsRecoverable(await log.GetHistoryAsync(User), SecretValence));

        // ...and theirs is untouched, still three rows, still no baseline, still theirs.
        var others = await log.GetHistoryAsync(Other);
        Assert.Equal(3, others.Count);
        Assert.DoesNotContain(others, t => t.IsBaseline);
        Assert.Contains(others, t => t.AppliedValence is { } v && Math.Abs(v - SecretValence) < 1e-9);
        Assert.All(others, t => Assert.Equal(Other, t.UserId));
    }

    // ---- nothing to compact ------------------------------------------------------------------

    [Fact]
    public async Task CompactingAnUnknownEvent_ChangesNothing()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var log = sp.GetRequiredService<ICompanionMoodLog>();

        await NudgeAsync(sp, 0.5);
        var before = await log.GetHistoryAsync(User);

        var result = await log.CompactForgottenAsync(User, [Guid.NewGuid()], 0.0, Now);

        Assert.False(result.Compacted);
        Assert.Equal(0, result.RowsRemoved);
        var after = await log.GetHistoryAsync(User);
        Assert.Equal(before.Count, after.Count);
        Assert.DoesNotContain(after, t => t.IsBaseline);
    }

    // ---- the real /forget path drives it ------------------------------------------------------

    [Fact]
    public async Task TheRealForgetPath_CompactsTheChain_AndPreservesHerMood()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var conversations = sp.GetRequiredService<IConversationStore>();
        var memories = sp.GetRequiredService<IMemoryStore>();

        var conversation = await conversations.StartConversationAsync(User, "t", "mock", "test");
        var message = new Message
        {
            Id = Guid.NewGuid(), UserId = User, ConversationId = conversation.Id,
            Role = MessageRole.User, Content = "I am devastated about the layoffs", Timestamp = Now,
        };
        await conversations.AddMessageAsync(message);

        var evidenceEventId = Guid.NewGuid();
        await sp.GetRequiredService<IEmotionStore>().AddSignalAsync(new EmotionalSignal
        {
            Id = Guid.NewGuid(), UserId = User, MessageId = message.Id,
            EvidenceEventId = evidenceEventId, EvidenceKind = "user-message", Timestamp = Now,
            Sentiment = Sentiment.VeryNegative, Valence = SecretValence, Label = "devastated",
            Evidence = "I am devastated about the layoffs", Topic = "the layoffs",
        });
        await sp.GetRequiredService<ICompanionStateTracker>()
            .NudgeAsync(User, SecretValence, evidenceEventId);
        await NudgeAsync(sp, 0.4);

        var spiritsBefore = (await sp.GetRequiredService<ICompanionStateTracker>()
            .BuildAsync(User)).Spirits;

        var memoryId = Guid.NewGuid();
        await memories.AddSemanticAsync(new SemanticMemory
        {
            Id = memoryId, UserId = User, Subject = "user", Predicate = "feels",
            Value = "devastated about the layoffs",
            NormalizedFact = "The user is devastated about the layoffs.",
            FirstObserved = Now, LastConfirmed = Now, CreatedAt = Now,
        });
        await memories.AddEvidenceAsync(User,
        [
            new MemoryEvidence
            {
                Id = Guid.NewGuid(), UserId = User, MemoryId = memoryId,
                MemoryKind = MemoryKind.Semantic, MessageId = message.Id,
                Excerpt = "I am devastated about the layoffs",
            },
        ]);

        Assert.True(await sp.GetRequiredService<IMemoryCurator>()
            .ForgetAsync(User, memoryId, "user asked to forget"));

        var history = await sp.GetRequiredService<ICompanionMoodLog>().GetHistoryAsync(User);
        Assert.False(ValenceIsRecoverable(history, SecretValence));
        Assert.Single(history, t => t.IsBaseline);
        // Her mood is preserved across the boundary rather than rewound.
        Assert.Equal(spiritsBefore, history.Single(t => t.IsBaseline).NewSpirits, 6);
    }
}
