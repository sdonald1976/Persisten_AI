using Companion.Core;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Infrastructure.Renderer;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Renderer shadow mode (docs/RENDERER_SHADOW.md): the tuned renderer runs beside production
/// on eligible real plans, records the pair, and changes nothing. These tests pin the three
/// promises the document makes: off is a complete rollback, a broken shadow path can never
/// cost a turn anything, and the deterministic check classes flag what they claim to flag.
/// </summary>
public class RendererShadowTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static ResponsePlan Plan(
        PlannedQuestion? question = null,
        IReadOnlyList<PlannedContent>? content = null,
        IReadOnlyList<EpistemicNote>? epistemic = null)
        => new()
        {
            Act = TurnIntent.Acknowledge,
            Question = question,
            Content = content ?? Array.Empty<PlannedContent>(),
            Epistemic = epistemic ?? Array.Empty<EpistemicNote>(),
            Tone = new ToneGuidance("short and casual", null, null),
        };

    // ---- rollback: off is the null object, end to end ------------------------------------

    [Fact]
    public async Task OffByDefault_TheNullObjectIsRegistered()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();

        var shadow = scope.ServiceProvider.GetRequiredService<IRendererShadow>();
        Assert.IsType<NullRendererShadow>(shadow);
        Assert.False(shadow.IsObserving);
    }

    [Fact]
    public async Task WhenEnabled_TheServiceAndARecordingRecorderAreRegistered()
    {
        await using var host = new TestHost(Now, settings: new Dictionary<string, string?>
        {
            ["Companion:RendererShadow:Enabled"] = "true",
        });
        using var scope = host.CreateScope();

        Assert.IsType<RendererShadowService>(scope.ServiceProvider.GetRequiredService<IRendererShadow>());

        // The renderer flag alone must bring up a persisting recorder — its rows are the point.
        Assert.True(scope.ServiceProvider.GetRequiredService<IShadowRecorder>().IsRecording);
    }

    // ---- isolation: a dead endpoint costs nothing ----------------------------------------

    [Fact]
    public async Task ObserveWithADeadEndpoint_NeverThrows_AndRecordsNothing()
    {
        var recorder = new CollectingRecorder();
        var service = new RendererShadowService(
            recorder,
            Options.Create(new CompanionOptions
            {
                RendererShadow = new RendererShadowOptions
                {
                    Enabled = true,
                    // A port nothing listens on: the render must fail, quietly.
                    Endpoint = "http://127.0.0.1:59999",
                    TimeoutSeconds = 5,
                },
            }),
            NullLogger<RendererShadowService>.Instance);

        service.Observe(new RendererShadowObservation
        {
            TraceId = Guid.NewGuid(),
            Plan = Plan(),
            Transcript = [("user", "hello")],
            UserMessage = "hello again",
            ProductionResponse = "hi.",
        });

        // Fire-and-forget: give the detached task time to fail, then confirm silence.
        await Task.Delay(500);
        Assert.Empty(recorder.Rows);
    }

    [Fact]
    public void ObserveWhenDisabled_ReturnsImmediately()
    {
        var recorder = new CollectingRecorder();
        var service = new RendererShadowService(
            recorder,
            Options.Create(new CompanionOptions()),
            NullLogger<RendererShadowService>.Instance);

        Assert.False(service.IsObserving);
        service.Observe(new RendererShadowObservation
        {
            TraceId = Guid.NewGuid(),
            Plan = Plan(),
            Transcript = [],
            UserMessage = "x",
            ProductionResponse = "y",
        });
        Assert.Empty(recorder.Rows);
    }

    // ---- the deterministic check classes ---------------------------------------------------

    [Fact]
    public void ClosedPlanQuestion_IsFlagged()
    {
        var v = RendererShadowChecks.Score(Plan(), "Sounds like a great evening. What movie?");
        Assert.Contains(v, x => x.StartsWith("closed-plan-question"));
    }

    [Fact]
    public void MandatoryQuestion_MissingAndBuried_AreDistinguished()
    {
        var plan = Plan(question: new PlannedQuestion(QuestionKind.Clarify, "Which one?", Mandatory: true));

        var missing = RendererShadowChecks.Score(plan, "I'll take care of it.");
        Assert.Contains(missing, x => x.StartsWith("mandatory-question-missing"));

        var buried = RendererShadowChecks.Score(plan, "Which one is it? I'll assume the first.");
        Assert.Contains(buried, x => x.StartsWith("mandatory-question-not-final"));

        var fired = RendererShadowChecks.Score(plan, "Which one — the first or the second?");
        Assert.DoesNotContain(fired, x => x.StartsWith("mandatory-question"));
    }

    [Fact]
    public void PaletteLeak_IsFlagged_AndSilenceIsNot()
    {
        var plan = Plan(content:
        [
            new PlannedContent(ContentKind.Memory, ContentRequirement.MayUse,
                "Scott is repainting the office a color he actually likes."),
        ]);

        var leak = RendererShadowChecks.Score(plan,
            "Nice. Speaking of which, how's repainting the office going?");
        Assert.Contains(leak, x => x.StartsWith("palette-leak"));

        var silent = RendererShadowChecks.Score(plan, "Nice. Enjoy the evening.");
        Assert.DoesNotContain(silent, x => x.StartsWith("palette-leak"));
    }

    [Fact]
    public void MustStateOmission_UsesDistinctiveTokens()
    {
        var plan = Plan(content:
        [
            new PlannedContent(ContentKind.LearnedKnowledge, ContentRequirement.MustState,
                "The refill is ready; the pickup window closes at seven."),
        ]);

        var omitted = RendererShadowChecks.Score(plan, "Nothing new came through today.");
        Assert.Contains(omitted, x => x.StartsWith("muststate-omission-proxy"));

        var stated = RendererShadowChecks.Score(plan, "The refill is ready — pickup closes at seven.");
        Assert.DoesNotContain(stated, x => x.StartsWith("muststate-omission-proxy"));
    }

    [Fact]
    public void InventedExperience_IsFlagged_ButHonestNegationPasses()
    {
        var invented = RendererShadowChecks.Score(Plan(), "I've tried that place — my go-to order is the ramen.");
        Assert.Contains(invented, x => x.StartsWith("invented-experience"));

        var honest = RendererShadowChecks.Score(Plan(), "I've never been to one, but it sounds great.");
        Assert.DoesNotContain(honest, x => x.StartsWith("invented-experience"));
    }

    [Fact]
    public void EpistemicAdmission_AbsenceIsFlagged_PresencePasses()
    {
        var plan = Plan(epistemic: [new EpistemicNote(EpistemicKind.NotLearned, "zydeco")]);

        var silent = RendererShadowChecks.Score(plan, "Zydeco is a lively genre from Louisiana.");
        Assert.Contains(silent, x => x.StartsWith("epistemic-admission-absent"));

        var honest = RendererShadowChecks.Score(plan, "I've never heard of zydeco — tell me about it.");
        Assert.DoesNotContain(honest, x => x.StartsWith("epistemic-admission-absent"));
    }

    // ---- privacy: the forget promise reaches renderer rows ---------------------------------

    [Fact]
    public async Task ForgetSweepsRendererRows_MatchingAnyOfTheThreeTexts()
    {
        await using var host = new TestHost(Now, settings: new Dictionary<string, string?>
        {
            ["Companion:RendererShadow:Enabled"] = "true",
        });
        using var scope = host.CreateScope();
        var recorder = scope.ServiceProvider.GetRequiredService<IShadowRecorder>();

        await recorder.RecordAsync(new ShadowComparison
        {
            Subject = RendererShadowService.RendererShadowSubject,
            Legacy = "The appointment with Dr. Feldspar is Tuesday.",
            Model = "Tuesday it is — Dr. Feldspar awaits.",
            Applied = "legacy",
            Input = "{\"UserMessage\":\"remind me about dr feldspar\"}",
        });
        await recorder.RecordAsync(new ShadowComparison
        {
            Subject = RendererShadowService.RendererShadowSubject,
            Legacy = "Unrelated reply about the garden.",
            Model = "Also unrelated.",
            Applied = "legacy",
            Input = "{\"UserMessage\":\"gardening\"}",
        });

        var removed = await recorder.ForgetCapturesAsync(["the appointment with Dr. Feldspar"]);

        Assert.Equal(1, removed);
        var remaining = await recorder.GetDisagreementsAsync(
            RendererShadowService.RendererShadowSubject, 10);
        Assert.DoesNotContain(remaining, r => r.Legacy!.Contains("Feldspar"));
    }

    private sealed class CollectingRecorder : IShadowRecorder
    {
        public List<ShadowComparison> Rows { get; } = [];

        public bool IsRecording => true;

        public bool IsShadowing => true;

        public Task RecordAsync(ShadowComparison comparison, CancellationToken ct = default)
        {
            Rows.Add(comparison);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ShadowAgreement>> GetAgreementAsync(
            DateTimeOffset since, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ShadowAgreement>>([]);

        public Task<IReadOnlyList<ShadowComparison>> GetDisagreementsAsync(
            string? subject, int count, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ShadowComparison>>(Rows);

        public Task<IReadOnlyList<ShadowComparison>> GetCapturesAsync(
            string? subject, int count, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ShadowComparison>>([]);

        public Task<int> ForgetCapturesAsync(
            IReadOnlyCollection<string> excerpts, CancellationToken ct = default)
            => Task.FromResult(0);
    }
}
