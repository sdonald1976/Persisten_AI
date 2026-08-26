using System.Reflection;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// A2. The forgetting contract for an emotional signal, stated as a test rather than as prose.
///
/// Redaction-in-place is only acceptable if what survives is a content-free, NONCONTRIBUTING
/// tombstone. The decisive test here is the reflective one: it enumerates every property on
/// the entity and requires each to be either cleared or on an explicit allowlist. A field
/// added later that carries a reading of the forgotten sentence fails it automatically,
/// which a hand-written list of assertions would not.
///
/// The permitted survivors are exactly: identity (so forgetting twice is idempotent and so
/// the mood log can name which readings it compacted behind), the source KIND (a
/// content-free statement about where the reading came from, never what it said), the
/// operational timestamps the retention sweep needs, and the status flags.
/// </summary>
public class EmotionalSignalTombstoneContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private const string User = "usr-scott";

    /// <summary>What may still be readable on a forgotten row, and why.</summary>
    private static readonly HashSet<string> Permitted =
    [
        nameof(EmotionalSignal.Id),                 // row identity
        nameof(EmotionalSignal.UserId),             // isolation scope
        nameof(EmotionalSignal.MessageId),          // exact identity: idempotent re-forgetting
        nameof(EmotionalSignal.EvidenceEventId),    // exact identity: mood-log compaction handle
        nameof(EmotionalSignal.EvidenceKind),       // where it came from, never what it said
        nameof(EmotionalSignal.Timestamp),          // retention sweep
        nameof(EmotionalSignal.ForgottenAt),        // retention + audit
        nameof(EmotionalSignal.EvidenceForgotten),  // status
        nameof(EmotionalSignal.FollowedUp),         // status: never surfaced again
    ];

    private static async Task<EmotionalSignal> ForgottenRowAsync(TestHost host)
    {
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var store = sp.GetRequiredService<IEmotionStore>();

        var messageId = Guid.NewGuid();
        await store.AddSignalAsync(new EmotionalSignal
        {
            Id = Guid.NewGuid(), UserId = User, MessageId = messageId,
            EvidenceEventId = Guid.NewGuid(), EvidenceKind = "user-message", Timestamp = Now,
            Sentiment = Sentiment.VeryNegative, Valence = -0.8, Label = "devastated",
            Evidence = "I am devastated about the layoffs", Topic = "the layoffs",
            ProjectId = Guid.NewGuid(),
        });

        Assert.Equal(1, await store.ForgetByEvidenceAsync(User, [messageId], [], Now.AddMinutes(1)));

        var db = sp.GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();
        return await db.EmotionalSignals.AsNoTracking().SingleAsync();
    }

    [Fact]
    public async Task NothingSurvivesThatIsNotOnTheAllowlist()
    {
        await using var host = new TestHost(Now);
        var row = await ForgottenRowAsync(host);

        var leaked = new List<string>();
        foreach (var p in typeof(EmotionalSignal).GetProperties(
                     BindingFlags.Public | BindingFlags.Instance))
        {
            if (Permitted.Contains(p.Name))
                continue;

            var value = p.GetValue(row);
            var cleared = value is null
                          || (value is string s && s.Length == 0)
                          || (value is bool b && !b);
            if (!cleared)
                leaked.Add($"{p.Name} = <non-null {p.PropertyType.Name}>");
        }

        Assert.True(leaked.Count == 0,
            "a forgotten emotional signal still carries: " + string.Join(", ", leaked)
            + ". Either clear it in EmotionStore.ForgetByEvidenceAsync, or add it to the "
            + "allowlist above WITH a reason it is content-free and required.");
    }

    [Theory]
    [InlineData(nameof(EmotionalSignal.Evidence))]
    [InlineData(nameof(EmotionalSignal.Topic))]
    [InlineData(nameof(EmotionalSignal.Sentiment))]
    [InlineData(nameof(EmotionalSignal.Valence))]
    [InlineData(nameof(EmotionalSignal.Label))]
    [InlineData(nameof(EmotionalSignal.ProjectId))]
    public async Task EveryNamedDerivativeIsGone(string property)
    {
        // Named individually as well as by the sweep above, so a failure says WHICH of the
        // contract's seven items broke rather than only that something did.
        await using var host = new TestHost(Now);
        var row = await ForgottenRowAsync(host);

        Assert.Null(typeof(EmotionalSignal).GetProperty(property)!.GetValue(row));
    }

    [Fact]
    public async Task NoReplacementReadingIsInvented()
    {
        await using var host = new TestHost(Now);
        var row = await ForgottenRowAsync(host);

        // Not zero, not neutral, not "unknown" — absent. A neutral valence is a claim that
        // she felt nothing, which is a different lie from having no record.
        Assert.Null(row.Valence);
        Assert.Null(row.Sentiment);
    }

    [Fact]
    public async Task ARedactedSignalContributesNothing()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var store = sp.GetRequiredService<IEmotionStore>();

        var messageId = Guid.NewGuid();
        await store.AddSignalAsync(new EmotionalSignal
        {
            Id = Guid.NewGuid(), UserId = User, MessageId = messageId,
            EvidenceEventId = Guid.NewGuid(), EvidenceKind = "user-message", Timestamp = Now,
            Sentiment = Sentiment.VeryNegative, Valence = -0.8, Label = "devastated",
            Evidence = "something private", Topic = "a private topic",
        });

        Assert.NotEmpty(await store.GetRecentSignalsAsync(User, 10));
        await store.ForgetByEvidenceAsync(User, [messageId], [], Now.AddMinutes(1));

        // Excluded at the source: a redacted row is audit metadata, never material for a
        // snapshot, so it cannot reach the prompt however the caller asks.
        Assert.Empty(await store.GetRecentSignalsAsync(User, 10));
    }

    [Fact]
    public async Task ForgettingIsIdempotent_AndTheRowStaysReachableForTheSweep()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var store = sp.GetRequiredService<IEmotionStore>();

        var messageId = Guid.NewGuid();
        await store.AddSignalAsync(new EmotionalSignal
        {
            Id = Guid.NewGuid(), UserId = User, MessageId = messageId,
            EvidenceEventId = Guid.NewGuid(), EvidenceKind = "user-message", Timestamp = Now,
            Sentiment = Sentiment.Negative, Valence = -0.3, Evidence = "x", Topic = "y",
        });

        Assert.Equal(1, await store.ForgetByEvidenceAsync(User, [messageId], [], Now.AddMinutes(1)));
        Assert.Equal(0, await store.ForgetByEvidenceAsync(User, [messageId], [], Now.AddMinutes(2)));

        // The tombstone is why redaction was chosen over deletion: age-based retention still
        // has a row to sweep, and it sweeps on a timestamp that reveals nothing.
        Assert.Equal(1, await store.PruneAsync(Now.AddDays(1)));
    }

    [Fact]
    public async Task TheTombstoneCannotBeReachedFromAnotherUser()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IEmotionStore>();

        var shared = Guid.NewGuid();      // same message id, two users
        foreach (var user in new[] { User, "usr-someone-else" })
            await store.AddSignalAsync(new EmotionalSignal
            {
                Id = Guid.NewGuid(), UserId = user, MessageId = shared,
                EvidenceEventId = Guid.NewGuid(), EvidenceKind = "user-message",
                Timestamp = Now, Sentiment = Sentiment.Negative, Valence = -0.4,
                Evidence = "identical wording", Topic = "identical topic",
            });

        Assert.Equal(1, await store.ForgetByEvidenceAsync(User, [shared], [], Now.AddMinutes(1)));
        Assert.Single(await store.GetRecentSignalsAsync("usr-someone-else", 10));
    }
}
