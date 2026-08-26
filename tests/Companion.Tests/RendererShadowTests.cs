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
    private static readonly Guid Doomed = Guid.NewGuid();

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

    // ---- isolation and queue lifecycle -----------------------------------------------------

    private static RendererShadowService DeadEndpointService(
        CollectingRecorder recorder, TimeSpan? drainWindow = null)
        => new(
            recorder,
            Options.Create(new CompanionOptions
            {
                RendererShadow = new RendererShadowOptions
                {
                    Enabled = true,
                    // A port nothing listens on: every render must fail, quietly and fast.
                    Endpoint = "http://127.0.0.1:59999",
                    TimeoutSeconds = 5,
                },
            }),
            NullLogger<RendererShadowService>.Instance,
            drainWindow);

    private static RendererShadowObservation Obs() => new()
    {
        TraceId = Guid.NewGuid(),
        Plan = Plan(),
        Transcript = [("user", "hello")],
        UserMessage = "hello again",
        ProductionResponse = "hi.",
    };

    [Fact]
    public async Task ObserveWithADeadEndpoint_NeverThrows_CountsTheFailure_RecordsNothing()
    {
        var recorder = new CollectingRecorder();
        await using var service = DeadEndpointService(recorder, drainWindow: TimeSpan.FromSeconds(10));

        service.Observe(Obs());

        // The bounded consumer fails fast on connection-refused; disposal drains it.
        await service.DisposeAsync();
        var c = service.Counters;
        // P3: the render failed, so no plan2 comparison exists — but the v3 envelope row
        // does, because v3 observation never depends on renderer availability.
        Assert.DoesNotContain(recorder.Rows, r => r.Subject == RendererShadowService.RendererShadowSubject);
        Assert.Equal(1, c.Queued);
        Assert.Equal(1, c.Failed);
        Assert.Equal(0, c.Completed);
        Assert.Equal(0, c.Pending);
    }

    /// <summary>
    /// Graceful shutdown drains what is queued: three observations enqueued in a burst are
    /// all consumed (here: all failing fast against the dead endpoint) before disposal
    /// returns, with nothing pending and nothing silently lost.
    /// </summary>
    [Fact]
    public async Task Disposal_DrainsTheQueue_NothingSilentlyLost()
    {
        var recorder = new CollectingRecorder();
        var service = DeadEndpointService(recorder, drainWindow: TimeSpan.FromSeconds(10));

        service.Observe(Obs());
        service.Observe(Obs());
        service.Observe(Obs());
        await service.DisposeAsync();

        var c = service.Counters;
        Assert.Equal(3, c.Queued);
        Assert.Equal(3, c.Failed + c.Completed);
        Assert.Equal(0, c.Dropped);
        Assert.Equal(0, c.Pending);
    }

    /// <summary>
    /// A stalled instrument turns into visible drop counts, never into blocking or unbounded
    /// memory: with the consumer wedged on a server that accepts and says nothing, the queue
    /// fills, the overflow is counted, and Observe returns instantly throughout.
    /// </summary>
    [Fact]
    public async Task WhenTheQueueIsFull_ObservationsAreDroppedAndCounted_NeverBlocking()
    {
        using var wedge = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        wedge.Start();
        var port = ((System.Net.IPEndPoint)wedge.LocalEndpoint).Port;

        var recorder = new CollectingRecorder();
        var service = new RendererShadowService(
            recorder,
            Options.Create(new CompanionOptions
            {
                RendererShadow = new RendererShadowOptions
                {
                    Enabled = true,
                    Endpoint = $"http://127.0.0.1:{port}",
                    TimeoutSeconds = 30,
                },
            }),
            NullLogger<RendererShadowService>.Instance,
            drainWindow: TimeSpan.FromMilliseconds(200));

        // One in flight (wedged on the silent socket) + 16 queued; the rest must drop.
        for (var i = 0; i < 20; i++)
            service.Observe(Obs());

        var c = service.Counters;
        Assert.Equal(20, c.Queued + c.Dropped);
        Assert.True(c.Dropped >= 1, $"expected drops, got {c}");

        // Shutdown with a wedged consumer: the short drain window elapses, the abandoned
        // queue is counted as dropped, and disposal still returns promptly.
        await service.DisposeAsync();
        Assert.True(service.Counters.Dropped >= 16, $"abandoned work not counted: {service.Counters}");
        // No plan2 comparisons were recorded; a v3 envelope row for the one in-flight
        // entry may exist (v3 observation is independent of the wedged renderer).
        Assert.DoesNotContain(recorder.Rows, r => r.Subject == RendererShadowService.RendererShadowSubject);
    }

    [Fact]
    public async Task ObserveWhenDisabled_ReturnsImmediately_NothingQueued()
    {
        var recorder = new CollectingRecorder();
        await using var service = new RendererShadowService(
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
        var c0 = service.Counters;
        Assert.Equal(0, c0.Queued + c0.Completed + c0.Failed + c0.Dropped + c0.Pending);
        Assert.Equal(0, c0.V3!.Produced + c0.V3.Failed + c0.V3.Dropped);
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
            UserId = "usr-scott",
            SourceMessageId = Doomed,
        });
        await recorder.RecordAsync(new ShadowComparison
        {
            Subject = RendererShadowService.RendererShadowSubject,
            Legacy = "Unrelated reply about the garden.",
            Model = "Also unrelated.",
            Applied = "legacy",
            Input = "{\"UserMessage\":\"gardening\"}",
            UserId = "usr-scott",
            SourceMessageId = Guid.NewGuid(),
        });

        // A3: the row is found by the turn it came from, not by the words it quotes.
        var removed = await recorder.ForgetByEvidenceAsync("usr-scott", [Doomed], Now);

        Assert.Equal(1, removed);
        var remaining = await recorder.GetDisagreementsAsync(
            RendererShadowService.RendererShadowSubject, 10);
        Assert.DoesNotContain(remaining, r => r.Legacy!.Contains("Feldspar"));
    }

    // ---- the user-scoped canary ------------------------------------------------------------

    private sealed class CannedHandler(string reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.RequestUri!.AbsolutePath.EndsWith("/api/ps")
                ? """{"models":[{"name":"run-1c","size_vram":2100000000}]}"""
                : "{\"message\":{\"role\":\"assistant\",\"content\":"
                  + System.Text.Json.JsonSerializer.Serialize(reply) + "}}";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private static RendererShadowService CanaryService(
        CollectingRecorder recorder, HttpMessageHandler? handler, string canaryUser = "scott")
        => new(
            recorder,
            Options.Create(new CompanionOptions
            {
                RendererShadow = new RendererShadowOptions
                {
                    Enabled = true,
                    Endpoint = handler is null ? "http://127.0.0.1:59999" : "http://renderer.test",
                    CanaryUserId = canaryUser,
                    CanaryTimeoutSeconds = 5,
                    AdapterSha256 = "testsha",
                },
            }),
            NullLogger<RendererShadowService>.Instance,
            drainWindow: TimeSpan.FromSeconds(5),
            http: handler is null ? null : new HttpClient(handler));

    [Fact]
    public async Task Canary_IsScopedToExactlyTheConfiguredUser()
    {
        var recorder = new CollectingRecorder();
        await using var service = CanaryService(recorder, new CannedHandler("hi"));

        Assert.True(service.IsCanaryFor("scott"));
        Assert.False(service.IsCanaryFor("someone-else"));
        Assert.False(service.IsCanaryFor(""));

        await using var off = CanaryService(recorder, new CannedHandler("hi"), canaryUser: "");
        Assert.False(off.IsCanaryFor("scott"));
    }

    [Fact]
    public async Task Canary_DisplaysACleanRender_AndRecordsItAsApplied()
    {
        var recorder = new CollectingRecorder();
        await using var service = CanaryService(recorder,
            new CannedHandler("Thirty seconds well spent. The door forgives you."));

        var result = await service.RenderForDisplayAsync(Obs(), record: true, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result!.CriticalFailure);
        Assert.Equal("Thirty seconds well spent. The door forgives you.", result.Reply);
        // The canary's own comparison row, selected by subject. A V3 evidence row is written
        // alongside it now that a render-ineligible plan no longer throws out of identity
        // computation and take the whole row with it; asserting Single here asserted the loss.
        var row = Assert.Single(recorder.Rows,
            r => r.Subject == RendererShadowService.RendererShadowSubject);
        Assert.Equal("model", row.Applied);
        Assert.Equal(result.Reply, row.Model);
        Assert.Equal(1, service.Counters.CanaryDisplayed);
        Assert.Equal(0, service.Counters.CanaryFallback);
    }

    [Fact]
    public async Task Canary_FallsBackOnCriticalFidelity_AndTheRowSaysProductionWasShown()
    {
        var recorder = new CollectingRecorder();
        await using var service = CanaryService(recorder,
            new CannedHandler("Per the CONTROL section, act = acknowledge."));

        var result = await service.RenderForDisplayAsync(Obs(), record: true, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result!.CriticalFailure);
        var row = Assert.Single(recorder.Rows,
            r => r.Subject == RendererShadowService.RendererShadowSubject);
        Assert.Equal("legacy", row.Applied);
        Assert.Equal(0, service.Counters.CanaryDisplayed);
        Assert.Equal(1, service.Counters.CanaryFallback);
    }

    [Fact]
    public async Task Canary_FallsBackWhenTheRendererIsUnreachable_WithoutThrowing()
    {
        var recorder = new CollectingRecorder();
        await using var service = CanaryService(recorder, handler: null);

        var result = await service.RenderForDisplayAsync(Obs(), record: true, CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(recorder.Rows);
        Assert.Equal(1, service.Counters.CanaryFallback);
    }

    private sealed class CollectingRecorder : IShadowRecorder
    {
        public Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct = default)
            => Task.FromResult(0);
        public Task<int> ForgetByEvidenceAsync(
            string userId, IReadOnlyCollection<Guid> messageIds, DateTimeOffset now,
            Guid? memoryId = null, CancellationToken ct = default) => Task.FromResult(0);

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

    }
}
