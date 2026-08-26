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
    private const string User = "usr-scott";

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
    /// And the direction that actually bit: switching CAPTURE on must not start RUNNING models.
    ///
    /// The two flags are documented as independent, and were not. Both resolved to one recorder
    /// whose IsRecording was hard-coded true, and Shadow.CompareAsync gated the expensive half on
    /// exactly that — so capture, which is meant to run nothing, would have begun paying an NLI
    /// inference on every turn the moment a model file appeared. It was inert only because no
    /// model is enabled, which is a safety that expires the first time one is.
    /// </summary>
    [Fact]
    public async Task CaptureAlone_DoesNotStartRunningModels()
    {
        await using var host = new TestHost(Now, settings: new Dictionary<string, string?>
        {
            ["CognitiveModels:Capture"] = "true",
        });
        using var scope = host.CreateScope();
        var recorder = scope.ServiceProvider.GetRequiredService<IShadowRecorder>();

        Assert.True(recorder.IsRecording);
        Assert.False(recorder.IsShadowing);
    }

    /// <summary>Shadow mode is the flag that says a model may run, and it still does.</summary>
    [Fact]
    public async Task ShadowMode_IsWhatTurnsModelsOn()
    {
        await using var host = new TestHost(Now, settings: new Dictionary<string, string?>
        {
            ["CognitiveModels:ShadowMode"] = "true",
        });
        using var scope = host.CreateScope();

        Assert.True(scope.ServiceProvider.GetRequiredService<IShadowRecorder>().IsShadowing);
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
    /// Forgetting a memory takes the rows it produced out of the capture table too.
    ///
    /// Capture's gate is evaluated at TURN time — private, in-character, "don't remember" —
    /// which covers every way of saying no except the one people actually use, which is
    /// changing their mind afterwards.
    ///
    /// A3 changed HOW those rows are found. It used to be a substring search over the stored
    /// text, which deleted by resemblance and matched across every user of the instance. It
    /// is now the message id the row was captured from, and both the user and the id must
    /// match.
    /// </summary>
    [Fact]
    public async Task ForgettingAMemory_RemovesTheRowsItProduced()
    {
        await using var host = Host();
        using var scope = host.CreateScope();
        var recorder = scope.ServiceProvider.GetRequiredService<IShadowRecorder>();
        var capture = scope.ServiceProvider.GetRequiredService<ICognitiveCapture>();

        const string forgotten = "I still need to finish the shed roof this weekend.";
        const string kept = "We have decided to use SQLite in the end.";
        var forgottenMessage = Guid.NewGuid();
        var keptMessage = Guid.NewGuid();

        await capture.CaptureUserMessageAsync(forgotten, default, User, forgottenMessage);
        await capture.CaptureUserMessageAsync(kept, default, User, keptMessage);
        Assert.Equal(6, (await recorder.GetCapturesAsync(null, 100)).Count);

        var removed = await recorder.ForgetByEvidenceAsync(
            User, [forgottenMessage], DateTimeOffset.UnixEpoch);

        Assert.Equal(3, removed);
        var left = await recorder.GetCapturesAsync(null, 100);
        Assert.Equal(3, left.Count);
        Assert.All(left, row => Assert.Equal(kept, row.Input));
    }

    /// <summary>
    /// Identical wording captured from two different turns is two different rows, and
    /// forgetting one leaves the other. Under the old substring rule both went, because the
    /// text was the same — which is precisely the over-deletion A3 removes.
    /// </summary>
    [Fact]
    public async Task IdenticalWordingFromADifferentTurn_Survives()
    {
        await using var host = Host();
        using var scope = host.CreateScope();
        var recorder = scope.ServiceProvider.GetRequiredService<IShadowRecorder>();
        var capture = scope.ServiceProvider.GetRequiredService<ICognitiveCapture>();

        const string sameWords = "I still need to finish the shed roof this weekend.";
        var doomed = Guid.NewGuid();
        var kept = Guid.NewGuid();
        await capture.CaptureUserMessageAsync(sameWords, default, User, doomed);
        await capture.CaptureUserMessageAsync(sameWords, default, User, kept);

        Assert.Equal(3, await recorder.ForgetByEvidenceAsync(
            User, [doomed], DateTimeOffset.UnixEpoch));
        Assert.Equal(3, (await recorder.GetCapturesAsync(null, 100)).Count);
    }

    /// <summary>
    /// And one user's forgetting cannot reach another's rows, even when the message id
    /// collides. Ownership is in the query, so the other user's rows are never loaded.
    /// </summary>
    [Fact]
    public async Task AnotherUsersRows_AreUnreachable()
    {
        await using var host = Host();
        using var scope = host.CreateScope();
        var recorder = scope.ServiceProvider.GetRequiredService<IShadowRecorder>();
        var capture = scope.ServiceProvider.GetRequiredService<ICognitiveCapture>();

        var shared = Guid.NewGuid();
        await capture.CaptureUserMessageAsync("something", default, User, shared);
        await capture.CaptureUserMessageAsync("something", default, "usr-other", shared);

        Assert.Equal(3, await recorder.ForgetByEvidenceAsync(
            User, [shared], DateTimeOffset.UnixEpoch));
        Assert.Equal(3, (await recorder.GetCapturesAsync(null, 100)).Count);
    }

    /// <summary>
    /// A row with no ownership -- written before A3 -- is never attributed to a turn. Those
    /// rows were purged by the migration; inventing an owner for them would delete somebody
    /// else's diagnostics on the next /forget.
    /// </summary>
    [Fact]
    public async Task RowsWithoutOwnership_AreNeverMatched()
    {
        await using var host = Host();
        using var scope = host.CreateScope();
        var recorder = scope.ServiceProvider.GetRequiredService<IShadowRecorder>();
        var capture = scope.ServiceProvider.GetRequiredService<ICognitiveCapture>();

        await capture.CaptureUserMessageAsync("legacy row, no owner");   // no ids passed

        Assert.Equal(0, await recorder.ForgetByEvidenceAsync(
            User, [Guid.NewGuid()], DateTimeOffset.UnixEpoch));
        Assert.Equal(3, (await recorder.GetCapturesAsync(null, 100)).Count);
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
    ///
    /// Called directly, because on the user's side this is defence in depth rather than the thing
    /// standing in the way — see <see cref="ACredentialInAUserMessage_NeverReachesCaptureAtAll"/>.
    /// It is the reply path where nothing else looks.
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

    /// <summary>
    /// Her reply is where the credential check is load-bearing, and it took running the capture
    /// against a live instance to see that. Nothing else inspects a reply for secrets: the privacy
    /// classifier reads the user's message, and a key that arrives in a tool result and is quoted
    /// back is checked here or nowhere.
    /// </summary>
    [Fact]
    public async Task ACredentialInHerReply_KeepsTheVerdictAndDropsTheText()
    {
        await using var host = Host();
        using var scope = host.CreateScope();
        var recorder = scope.ServiceProvider.GetRequiredService<IShadowRecorder>();

        await scope.ServiceProvider.GetRequiredService<ICognitiveCapture>()
            .CaptureReplyAsync("I'll make a note that the key is ghp_abcdefghijklmnopqrstuvwxyz0123456789.");

        var row = Assert.Single(await recorder.GetCapturesAsync("companion.commitment", 10));
        Assert.Equal("true", row.Legacy);
        Assert.Null(row.Input);
    }

    /// <summary>
    /// On the user's side the redaction never gets a turn, because the privacy classifier calls the
    /// same <c>SecretDetector</c> and a message containing a key makes the whole turn
    /// non-rememberable. Asserted rather than assumed: the capture was documented as "keeps the
    /// rate, drops the text" until a live run showed the row is not written at all, and the
    /// difference matters to anyone computing a base rate from this table.
    /// </summary>
    [Fact]
    public async Task ACredentialInAUserMessage_NeverReachesCaptureAtAll()
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
                CompanionSeeder.DemoUserId, conversationId,
                "I still need to rotate sk-abcdefghijklmnopqrstuvwxyz012345 on the server.");
        }

        using (var scope = host.CreateScope())
        {
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<IShadowRecorder>()
                .GetCapturesAsync(null, 100));
        }
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
