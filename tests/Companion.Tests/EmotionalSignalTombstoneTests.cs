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
/// Phase 0 privacy amendment (docs/SOURCE4_PHASE0_PLAN.md §amendment): a forgotten signal
/// keeps only a TOMBSTONE. `Sentiment`, `Valence` and `Label` are semantic derivatives of the
/// forgotten sentence — a reading OF it, not a neutral fact about it — so they are purged
/// alongside `Evidence` and `Topic`.
///
/// What survives is the minimum for audit and idempotency: identifiers, the forgotten flag,
/// and operational timestamps. Nothing else, and nothing that contributes to a snapshot.
/// </summary>
public class EmotionalSignalTombstoneTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private const string User = CompanionSeeder.DemoUserId;

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"tombstone-{Guid.NewGuid():N}.db");

    private string ConnectionString => $"Data Source={_dbPath}";

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch (IOException) { }
    }

    private static EmotionalSignal Signal(
        Guid messageId, string evidence, string? topic = "the interview",
        DateTimeOffset? at = null, Guid? eventId = null, string userId = User)
        => new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            MessageId = messageId,
            EvidenceEventId = eventId ?? Guid.NewGuid(),
            EvidenceKind = "user-message",
            Timestamp = at ?? Now,
            Sentiment = Sentiment.VeryNegative,
            Valence = -0.87,
            Label = "devastated",
            Evidence = evidence,
            Topic = topic,
            ProjectId = Guid.NewGuid(),
        };

    // ---- 1: a forgotten row serializes with none of it -----------------------------------

    [Fact]
    public async Task Amendment1_AForgottenRow_SerializesWithNoEvidenceTopicSentimentValenceOrLabel()
    {
        await using var host = new TestHost(Now);
        var store = host.Services.CreateScope().ServiceProvider.GetRequiredService<IEmotionStore>();
        var messageId = Guid.NewGuid();
        var signal = Signal(messageId, "I am devastated about the layoffs at work");
        await store.AddSignalAsync(signal);

        Assert.Equal(1, await store.ForgetByEvidenceAsync(User, [messageId], [], Now));

        using var scope = host.CreateScope();
        var row = await scope.ServiceProvider
            .GetRequiredService<Infrastructure.Persistence.CompanionDbContext>()
            .EmotionalSignals.AsNoTracking().SingleAsync(s => s.Id == signal.Id);

        // Every semantic field is gone, not merely the text.
        Assert.Null(row.Evidence);
        Assert.Null(row.Topic);
        Assert.Null(row.Sentiment);
        Assert.Null(row.Valence);
        Assert.Null(row.Label);
        Assert.Null(row.ProjectId);

        // The serialized row proves it: nothing of the reading survives anywhere in it.
        var serialized = JsonSerializer.Serialize(row);
        Assert.DoesNotContain("devastated", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("layoffs", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("interview", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("VeryNegative", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("0.87", serialized);

        // The tombstone: identifiers, status, operational timestamps. That is all.
        Assert.Equal(signal.Id, row.Id);
        Assert.Equal(User, row.UserId);
        Assert.Equal(messageId, row.MessageId);
        Assert.Equal(signal.EvidenceEventId, row.EvidenceEventId);
        Assert.True(row.EvidenceForgotten);
        Assert.Equal(Now, row.ForgottenAt);
        Assert.Equal(Now, row.Timestamp);
    }

    // ---- 2: identical events stay independent --------------------------------------------

    [Fact]
    public async Task Amendment2_ForgettingOneEvent_LeavesAnIdenticalOneFullyIntact()
    {
        await using var host = new TestHost(Now);
        var store = host.Services.CreateScope().ServiceProvider.GetRequiredService<IEmotionStore>();

        // Byte-identical in every semantic field; different evidence identity.
        var forgotten = Guid.NewGuid();
        var kept = Guid.NewGuid();
        var a = Signal(forgotten, "I am devastated about the layoffs at work");
        var b = Signal(kept, "I am devastated about the layoffs at work");
        await store.AddSignalAsync(a);
        await store.AddSignalAsync(b);

        Assert.Equal(1, await store.ForgetByEvidenceAsync(User, [forgotten], [], Now));

        using var scope = host.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();

        var tombstone = await db.EmotionalSignals.AsNoTracking().SingleAsync(s => s.Id == a.Id);
        Assert.True(tombstone.EvidenceForgotten);
        Assert.Null(tombstone.Valence);

        var survivor = await db.EmotionalSignals.AsNoTracking().SingleAsync(s => s.Id == b.Id);
        Assert.False(survivor.EvidenceForgotten);
        Assert.Equal("I am devastated about the layoffs at work", survivor.Evidence);
        Assert.Equal(Sentiment.VeryNegative, survivor.Sentiment);
        Assert.Equal(-0.87, survivor.Valence);
        Assert.Equal("devastated", survivor.Label);
    }

    // ---- 3: reconstruction ignores tombstones, before and after restart -------------------

    [Fact]
    public async Task Amendment3_SnapshotReconstruction_IgnoresTombstones_AcrossARestart()
    {
        var messageId = Guid.NewGuid();
        var signalId = Guid.NewGuid();

        // --- first process lifetime: one real signal, then forget it ---
        await using (var host = new TestHost(Now, connectionString: ConnectionString))
        {
            using var scope = host.CreateScope();
            var sp = scope.ServiceProvider;
            var store = sp.GetRequiredService<IEmotionStore>();

            var signal = Signal(messageId, "I am devastated about the layoffs at work");
            signal.Id = signalId;
            await store.AddSignalAsync(signal);

            var before = await sp.GetRequiredService<IRelationshipTracker>().BuildAsync(User);
            Assert.True(before.HasHistory);

            Assert.Equal(1, await store.ForgetByEvidenceAsync(User, [messageId], [], Now));

            var after = await sp.GetRequiredService<IRelationshipTracker>().BuildAsync(User);
            Assert.False(after.HasHistory);
        }

        // --- restart: same database file, fresh everything else ---
        await using (var restarted = new TestHost(Now, connectionString: ConnectionString))
        {
            using var scope = restarted.CreateScope();
            var sp = scope.ServiceProvider;

            // The tombstone is still on disk...
            var row = await sp.GetRequiredService<Infrastructure.Persistence.CompanionDbContext>()
                .EmotionalSignals.AsNoTracking().SingleAsync(s => s.Id == signalId);
            Assert.True(row.EvidenceForgotten);
            Assert.Null(row.Valence);

            // ...and reconstruction still ignores it completely. Not a neutral reading: none.
            var snapshot = await sp.GetRequiredService<IRelationshipTracker>().BuildAsync(User);
            Assert.False(snapshot.HasHistory);
            Assert.Equal(0, snapshot.SignalCount);
            Assert.Null(snapshot.Describe());
        }
    }

    // ---- 4: the declared 180-day lifecycle covers active AND redacted rows ----------------

    [Fact]
    public async Task Amendment4_TheDeclaredSweep_TreatsActiveAndRedactedRowsAlike()
    {
        await using var host = new TestHost(Now);
        var store = host.Services.CreateScope().ServiceProvider.GetRequiredService<IEmotionStore>();
        var cutoff = Now - SleepCycle.EmotionalSignalRetention;

        var oldActive = Signal(Guid.NewGuid(), "ancient worry", at: cutoff - TimeSpan.FromDays(1));
        var oldForgottenMessage = Guid.NewGuid();
        var oldForgotten = Signal(oldForgottenMessage, "ancient forgotten worry",
            at: cutoff - TimeSpan.FromDays(1));
        var freshActive = Signal(Guid.NewGuid(), "current worry", at: Now - TimeSpan.FromDays(1));
        var freshForgottenMessage = Guid.NewGuid();
        var freshForgotten = Signal(freshForgottenMessage, "current forgotten worry",
            at: Now - TimeSpan.FromDays(1));

        foreach (var s in new[] { oldActive, oldForgotten, freshActive, freshForgotten })
            await store.AddSignalAsync(s);
        await store.ForgetByEvidenceAsync(User, [oldForgottenMessage, freshForgottenMessage], [], Now);

        // The lifecycle is declared by AGE, not by status: a tombstone is neither kept longer
        // for audit nor dropped sooner for privacy. Both old rows go; both fresh rows stay.
        Assert.Equal(2, await store.PruneAsync(cutoff));

        using var scope = host.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();
        var remaining = await db.EmotionalSignals.AsNoTracking()
            .Where(s => s.UserId == User).Select(s => s.Id).ToListAsync();

        Assert.Equal(2, remaining.Count);
        Assert.Contains(freshActive.Id, remaining);
        Assert.Contains(freshForgotten.Id, remaining);
        Assert.DoesNotContain(oldActive.Id, remaining);
        Assert.DoesNotContain(oldForgotten.Id, remaining);
    }

    // ---- 5: mood-transition provenance ---------------------------------------------------

    // Mood-transition coverage moved to MoodCompactionTests: the contract decision of
    // 2026-08-25 replaced in-place redaction with privacy compaction, because redaction left
    // the neighbouring rows able to reconstruct the forgotten valence exactly.
}
