using Companion.Core;
using Companion.Core.Abstractions;
using Companion.Infrastructure.Seeding;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Corpus capture: writing down what the heuristics said about real sentences, so that the data a
/// specialist model is eventually judged on stops being one person's guesses about English.
///
/// Two things are being protected here and they pull against each other. The capture is worthless
/// unless it records the ordinary turns as well as the interesting ones — the rate a rule fires at
/// is the number every precision estimate so far has had to assume rather than measure. And it is
/// unacceptable if it writes down a sentence the user asked her to forget. So most of what follows
/// is about the things it declines to record.
/// </summary>
public class CognitiveCaptureTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static TestHost Host(bool capture = true)
        => new(Now, settings: new Dictionary<string, string?>
        {
            ["CognitiveModels:Capture"] = capture ? "true" : "false",
        });

    [Fact]
    public async Task OffByDefault_AndSaysSoSoCallersSkipTheWork()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();

        Assert.False(scope.ServiceProvider.GetRequiredService<ICognitiveCapture>().IsCapturing);
    }

    /// <summary>
    /// Switching shadow mode on must not start writing user text. They are different decisions —
    /// one costs an inference, the other keeps sentences — and a single flag would make the second
    /// a side effect of the first.
    /// </summary>
    [Fact]
    public async Task ShadowModeAlone_DoesNotStartCapturing()
    {
        await using var host = new TestHost(Now, settings: new Dictionary<string, string?>
        {
            ["CognitiveModels:ShadowMode"] = "true",
        });
        using var scope = host.CreateScope();

        Assert.True(scope.ServiceProvider.GetRequiredService<IShadowRecorder>().IsRecording);
        Assert.False(scope.ServiceProvider.GetRequiredService<ICognitiveCapture>().IsCapturing);
    }

    /// <summary>
    /// Every judgement on every message, including the ones that answer no. A capture log holding
    /// only the sentences a rule fired on cannot measure how often it fires, which is the single
    /// number this corpus most needs.
    /// </summary>
    [Fact]
    public async Task RecordsEveryJudgement_IncludingTheNegativeOnes()
    {
        await using var host = Host();
        using var scope = host.CreateScope();
        var capture = scope.ServiceProvider.GetRequiredService<ICognitiveCapture>();
        var recorder = scope.ServiceProvider.GetRequiredService<IShadowRecorder>();

        await capture.CaptureUserMessageAsync("I still need to finish the shed roof.");

        var rows = await recorder.GetCapturesAsync(subject: null, count: 100);
        Assert.Equal(
            new[] { "memory.decision", "memory.unfinished", "tool.capability" },
            rows.Select(r => r.Subject).OrderBy(s => s, StringComparer.Ordinal));

        Assert.Equal("true", Assert.Single(rows, r => r.Subject == "memory.unfinished").Legacy);
        Assert.Equal("false", Assert.Single(rows, r => r.Subject == "memory.decision").Legacy);
    }

    /// <summary>
    /// The subject is the corpus decision key, not a name invented here. A captured row and a
    /// generated row have to be the same row about the same judgement or they can never be trained
    /// on together, and a near-miss like "unfinished" against "memory.unfinished" would look right
    /// in both files and silently produce two datasets.
    /// </summary>
    [Fact]
    public async Task SubjectsMatchTheCorpusDecisionKeys()
    {
        await using var host = Host();
        using var scope = host.CreateScope();
        var recorder = scope.ServiceProvider.GetRequiredService<IShadowRecorder>();
        var capture = scope.ServiceProvider.GetRequiredService<ICognitiveCapture>();

        await capture.CaptureUserMessageAsync("we've decided to use SQLite");
        await capture.CaptureReplyAsync("I'll check in about the roof tomorrow.");

        var subjects = (await recorder.GetCapturesAsync(null, 100)).Select(r => r.Subject).ToHashSet();
        Assert.Equal(
            new HashSet<string>
            {
                "memory.decision", "memory.unfinished", "tool.capability", "companion.commitment",
            },
            subjects);
    }

    /// <summary>
    /// The verdict survives, the credential does not. Skipping the row entirely would be the easy
    /// answer and the wrong one: it would bias the very rate the capture exists to measure, and the
    /// rate is knowable without keeping the sentence.
    /// </summary>
    [Fact]
    public async Task ASentenceThatLooksLikeACredential_KeepsTheVerdictAndDropsTheText()
    {
        await using var host = Host();
        using var scope = host.CreateScope();
        var recorder = scope.ServiceProvider.GetRequiredService<IShadowRecorder>();

        await scope.ServiceProvider.GetRequiredService<ICognitiveCapture>()
            .CaptureUserMessageAsync("I still need to rotate sk-abcdefghijklmnopqrstuvwxyz012345");

        var row = Assert.Single(await recorder.GetCapturesAsync("memory.unfinished", 10));
        Assert.Equal("true", row.Legacy);
        Assert.Null(row.Input);
    }

    [Fact]
    public async Task AnOrdinarySentence_IsKept()
    {
        await using var host = Host();
        using var scope = host.CreateScope();
        var recorder = scope.ServiceProvider.GetRequiredService<IShadowRecorder>();

        await scope.ServiceProvider.GetRequiredService<ICognitiveCapture>()
            .CaptureUserMessageAsync("I still need to finish the shed roof.");

        Assert.Equal(
            "I still need to finish the shed roof.",
            Assert.Single(await recorder.GetCapturesAsync("memory.unfinished", 10)).Input);
    }

    /// <summary>
    /// Captures carry no model answer, so they cannot have agreed with one. Counting them would
    /// report a climbing agreement rate for a model that was never asked — the exact shape of a
    /// metric that looks like evidence and is not.
    /// </summary>
    [Fact]
    public async Task CapturesDoNotCountTowardsAgreementOrTheDisagreementQueue()
    {
        await using var host = new TestHost(Now, settings: new Dictionary<string, string?>
        {
            ["CognitiveModels:Capture"] = "true",
            ["CognitiveModels:ShadowMode"] = "true",
        });
        using var scope = host.CreateScope();
        var recorder = scope.ServiceProvider.GetRequiredService<IShadowRecorder>();

        await scope.ServiceProvider.GetRequiredService<ICognitiveCapture>()
            .CaptureUserMessageAsync("I still need to finish the shed roof.");
        await Shadow.CompareAsync(
            recorder, "memory.unfinished", legacy: true,
            model: _ => Task.FromResult<(bool, double)?>((false, 0.9)));

        var agreement = Assert.Single(await recorder.GetAgreementAsync(Now.AddDays(-1)));
        Assert.Equal(1, agreement.Comparisons);
        Assert.Equal(1, agreement.Disagreements);
        Assert.Single(await recorder.GetDisagreementsAsync("memory.unfinished", 50));
    }

    [Fact]
    public async Task WhenOff_NothingIsWrittenAtAll()
    {
        await using var host = Host(capture: false);
        using var scope = host.CreateScope();

        await scope.ServiceProvider.GetRequiredService<ICognitiveCapture>()
            .CaptureUserMessageAsync("I still need to finish the shed roof.");

        Assert.Empty(await scope.ServiceProvider.GetRequiredService<IShadowRecorder>()
            .GetCapturesAsync(null, 100));
    }

    /// <summary>
    /// A turn the user put off the record produces no training data either. The capture runs under
    /// the same gate as memory extraction rather than beside it, because "we won't remember this"
    /// meaning "except in the telemetry table" is not a promise anyone would accept if it were
    /// written down that way.
    /// </summary>
    [Fact]
    public async Task ADoNotRememberConversation_CapturesNothing()
    {
        await using var host = Host();

        Guid conversationId;
        using (var seed = host.CreateScope())
        {
            var sp = seed.ServiceProvider;
            await sp.GetRequiredService<CompanionSeeder>().SeedAsync(Now);
            var conversations = sp.GetRequiredService<IConversationStore>();
            var conversation = await conversations.StartConversationAsync(
                CompanionSeeder.DemoUserId, "private", "mock", "test");
            await conversations.SetDoNotRememberAsync(conversation.Id, CompanionSeeder.DemoUserId, true);
            conversationId = conversation.Id;
        }

        using (var scope = host.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ICompanion>().RespondAsync(
                CompanionSeeder.DemoUserId, conversationId, "I still need to finish the shed roof.");
        }

        using (var scope = host.CreateScope())
        {
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<IShadowRecorder>()
                .GetCapturesAsync(null, 100));
        }
    }

    /// <summary>An ordinary turn does capture, which is the other half of the same guarantee.</summary>
    [Fact]
    public async Task AnOrdinaryTurn_Captures()
    {
        await using var host = Host();

        Guid conversationId;
        using (var seed = host.CreateScope())
        {
            var sp = seed.ServiceProvider;
            await sp.GetRequiredService<CompanionSeeder>().SeedAsync(Now);
            conversationId = (await sp.GetRequiredService<IConversationStore>()
                .StartConversationAsync(CompanionSeeder.DemoUserId, "t", "mock", "test")).Id;
        }

        using (var scope = host.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ICompanion>().RespondAsync(
                CompanionSeeder.DemoUserId, conversationId, "I still need to finish the shed roof.");
        }

        using (var scope = host.CreateScope())
        {
            var rows = await scope.ServiceProvider.GetRequiredService<IShadowRecorder>()
                .GetCapturesAsync("memory.unfinished", 100);
            var row = Assert.Single(rows);
            Assert.Equal("true", row.Legacy);
            Assert.Equal("I still need to finish the shed roof.", row.Input);
        }
    }
}
