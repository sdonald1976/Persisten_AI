using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Companion.Infrastructure.Renderer;
using Companion.Infrastructure.Seeding;
using Companion.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// A3. Shadow comparisons are forgotten by ownership and exact evidence, never by text.
///
/// This table quotes verbatim user messages and — for renderer subjects — both replies. It
/// had no user column at all, so the only forgetting available was a case-insensitive
/// substring search that deleted rows merely sharing a phrase, missed any paraphrase, and
/// reached across every user of the instance.
/// </summary>
public class ShadowComparisonIdentityTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private const string User = "usr-scott";
    private const string Other = "usr-someone-else";

    // The exact phrase the old substring rule would have matched on.
    private const string SamePhrase = "the appointment with Dr. Feldspar";

    private static TestHost ShadowHost() => new(
        Now,
        settings: new Dictionary<string, string?>
        {
            ["Companion:RendererShadow:Enabled"] = "true",
            ["Companion:RendererShadow:Endpoint"] = "http://127.0.0.1:59993",
            ["Companion:RendererShadow:TimeoutSeconds"] = "2",
        });

    private static ShadowComparison Row(string userId, Guid? message, string text) => new()
    {
        Id = Guid.NewGuid(),
        Subject = RendererShadowService.RendererShadowSubject,
        Legacy = text,
        Model = text,
        Applied = "legacy",
        Input = text,
        UserId = userId,
        SourceMessageId = message,
        Timestamp = Now,
    };

    [Fact]
    public async Task ThePhraseIsIrrelevant_OnlyTheTurnMatters()
    {
        await using var host = ShadowHost();
        using var scope = host.CreateScope();
        var recorder = scope.ServiceProvider.GetRequiredService<IShadowRecorder>();

        var doomed = Guid.NewGuid();
        var kept = Guid.NewGuid();
        await recorder.RecordAsync(Row(User, doomed, SamePhrase));
        await recorder.RecordAsync(Row(User, kept, SamePhrase));       // identical wording

        Assert.Equal(1, await recorder.ForgetByEvidenceAsync(User, [doomed], Now));

        var left = await recorder.GetDisagreementsAsync(
            RendererShadowService.RendererShadowSubject, 50);
        Assert.Single(left);
        Assert.Equal(kept, left[0].SourceMessageId);
    }

    [Fact]
    public async Task AParaphraseFromTheSameTurn_IsStillRemoved()
    {
        // The mirror of the previous test, and the half substring matching always missed.
        await using var host = ShadowHost();
        using var scope = host.CreateScope();
        var recorder = scope.ServiceProvider.GetRequiredService<IShadowRecorder>();

        var turn = Guid.NewGuid();
        await recorder.RecordAsync(Row(User, turn, SamePhrase));
        await recorder.RecordAsync(Row(User, turn, "the Feldspar booking, reworded entirely"));

        Assert.Equal(2, await recorder.ForgetByEvidenceAsync(User, [turn], Now));
        Assert.Empty(await recorder.GetDisagreementsAsync(
            RendererShadowService.RendererShadowSubject, 50));
    }

    [Fact]
    public async Task AnotherUsersRowsAreUnreachable_EvenWithTheSameMessageId()
    {
        await using var host = ShadowHost();
        using var scope = host.CreateScope();
        var recorder = scope.ServiceProvider.GetRequiredService<IShadowRecorder>();

        var shared = Guid.NewGuid();
        await recorder.RecordAsync(Row(User, shared, SamePhrase));
        await recorder.RecordAsync(Row(Other, shared, SamePhrase));

        Assert.Equal(1, await recorder.ForgetByEvidenceAsync(User, [shared], Now));

        var left = await recorder.GetDisagreementsAsync(
            RendererShadowService.RendererShadowSubject, 50);
        Assert.Single(left);
        Assert.Equal(Other, left[0].UserId);
    }

    [Fact]
    public async Task RowsWithoutOwnership_AreNeverAttributed()
    {
        await using var host = ShadowHost();
        using var scope = host.CreateScope();
        var recorder = scope.ServiceProvider.GetRequiredService<IShadowRecorder>();

        await recorder.RecordAsync(Row(userId: null!, message: null, text: SamePhrase));

        // A legacy row cannot name its turn, so it is never matched. The migration purges
        // these; guessing an owner would delete somebody else's diagnostics.
        Assert.Equal(0, await recorder.ForgetByEvidenceAsync(User, [Guid.NewGuid()], Now));
    }

    [Fact]
    public async Task ForgettingIsIdempotent()
    {
        await using var host = ShadowHost();
        using var scope = host.CreateScope();
        var recorder = scope.ServiceProvider.GetRequiredService<IShadowRecorder>();

        var turn = Guid.NewGuid();
        await recorder.RecordAsync(Row(User, turn, SamePhrase));

        Assert.Equal(1, await recorder.ForgetByEvidenceAsync(User, [turn], Now));
        Assert.Equal(0, await recorder.ForgetByEvidenceAsync(User, [turn], Now));
    }

    [Fact]
    public async Task PairRows_AreFoundByTheirMemory_NotByAMessage()
    {
        await using var host = ShadowHost();
        using var scope = host.CreateScope();
        var recorder = scope.ServiceProvider.GetRequiredService<IShadowRecorder>();

        var memory = Guid.NewGuid();
        await recorder.RecordAsync(new ShadowComparison
        {
            Id = Guid.NewGuid(), Subject = CognitiveCapture.PairSubject,
            Legacy = "superseded", Model = null, Applied = "legacy", Input = "{}",
            UserId = User, SourceMemoryId = memory, Timestamp = Now,
        });

        // No message id will find it; the memory id is the only handle.
        Assert.Equal(0, await recorder.ForgetByEvidenceAsync(User, [Guid.NewGuid()], Now));
        Assert.Equal(1, await recorder.ForgetByEvidenceAsync(User, [], Now, memory));
    }

    // ---- retention ------------------------------------------------------------------------

    [Fact]
    public async Task RetentionSweepsByAge_IndependentlyOfForgetting()
    {
        await using var host = ShadowHost();
        using var scope = host.CreateScope();
        var recorder = scope.ServiceProvider.GetRequiredService<IShadowRecorder>();

        await recorder.RecordAsync(Row(User, Guid.NewGuid(), SamePhrase));

        Assert.Equal(0, await recorder.PruneAsync(Now - TimeSpan.FromDays(1)));
        Assert.Equal(1, await recorder.PruneAsync(Now + TimeSpan.FromDays(1)));
    }

    [Fact]
    public void TheRetentionWindowIsDeclared()
    {
        // Named rather than inlined, so the duration is reviewable and so the sweep and the
        // documentation cannot drift apart.
        Assert.Equal(TimeSpan.FromDays(30), SleepCycle.ShadowComparisonRetention);
    }

    // ---- the forget contract carries no text ------------------------------------------------

    [Fact]
    public void TheForgetSignatureTakesNoText()
    {
        var method = typeof(IShadowRecorder)
            .GetMethod(nameof(IShadowRecorder.ForgetByEvidenceAsync))!;

        // Exactly one string parameter, and it is the isolation scope rather than a matcher.
        var strings = method.GetParameters().Where(p => p.ParameterType == typeof(string)).ToList();
        Assert.Single(strings);
        Assert.Equal("userId", strings[0].Name);

        var ids = Assert.Single(method.GetParameters()
            .Where(p => p.ParameterType.IsGenericType
                        && p.ParameterType.GetGenericTypeDefinition() == typeof(IReadOnlyCollection<>)));
        Assert.Equal(typeof(Guid), ids.ParameterType.GetGenericArguments()[0]);
    }

    [Fact]
    public void NoSubstringMatchingRemainsInTheRecorder()
    {
        // Source-level, because this is the defect: it was a Contains() call, and a later
        // edit could reintroduce one without any behavioural test noticing on small data.
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Companion.Infrastructure", "Cognition", "ShadowRecorder.cs"));

        var forgetStart = source.IndexOf("ForgetByEvidenceAsync", StringComparison.Ordinal);
        var forgetEnd = source.IndexOf("PruneAsync", forgetStart, StringComparison.Ordinal);
        var body = forgetEnd > forgetStart ? source[forgetStart..forgetEnd] : source[forgetStart..];

        // Set membership on Guids is fine and is how the rule works. What must never come
        // back is TEXT matching: a case-insensitive comparison, or Contains applied to any of
        // the three columns that hold conversation.
        Assert.DoesNotContain("StringComparison", body, StringComparison.Ordinal);
        foreach (var column in new[] { "Input", "Legacy", "Model" })
            Assert.DoesNotContain($"{column}!.Contains", body, StringComparison.Ordinal);
        foreach (var column in new[] { "Input", "Legacy", "Model" })
            Assert.DoesNotContain($"{column}.Contains", body, StringComparison.Ordinal);
    }

    // ---- the flag still only governs observation --------------------------------------------

    [Fact]
    public async Task WithShadowDisabled_NothingIsRecordedAndForgettingIsHarmless()
    {
        await using var host = new TestHost(Now, settings: new Dictionary<string, string?>
        {
            ["Companion:RendererShadow:Enabled"] = "false",
        });
        using var scope = host.CreateScope();
        var recorder = scope.ServiceProvider.GetRequiredService<IShadowRecorder>();

        await recorder.RecordAsync(Row(User, Guid.NewGuid(), SamePhrase));

        Assert.Equal(0, await recorder.ForgetByEvidenceAsync(User, [Guid.NewGuid()], Now));
        Assert.Empty(await recorder.GetDisagreementsAsync(
            RendererShadowService.RendererShadowSubject, 50));
    }

    [Fact]
    public async Task ARealTurn_StoresAndDisplaysTheSameReply_WithOwnershipRecorded()
    {
        await using var host = ShadowHost();
        Guid conversationId;
        using (var seed = host.CreateScope())
            conversationId = (await seed.ServiceProvider.GetRequiredService<IConversationStore>()
                .StartConversationAsync(CompanionSeeder.DemoUserId, "t", "mock", "test")).Id;

        string reply;
        using (var scope = host.CreateScope())
        {
            var trace = await scope.ServiceProvider.GetRequiredService<ICompanion>()
                .RespondAsync(CompanionSeeder.DemoUserId, conversationId,
                    "What did we decide about the shed?");
            reply = trace.Response;
            Assert.False(string.IsNullOrWhiteSpace(reply));
        }

        using var read = host.CreateScope();
        var db = read.ServiceProvider
            .GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();

        // The stored assistant message is the displayed one, unchanged by A3.
        var stored = await db.Messages.AsNoTracking()
            .Where(m => m.Role == MessageRole.Assistant)
            .OrderByDescending(m => m.Timestamp)
            .FirstAsync();
        Assert.Equal(reply, stored.Content);

        // And every shadow row this turn wrote can name its owner and its turn.
        var rows = await db.ShadowComparisons.AsNoTracking().ToListAsync();
        Assert.All(rows, r =>
        {
            Assert.Equal(CompanionSeeder.DemoUserId, r.UserId);
            Assert.NotNull(r.SourceMessageId);
        });
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "docs")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root not found");
    }
}
