using System.Text.Json;
using Companion.Core;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Infrastructure.Renderer;
using Companion.PlanV3;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// P3 shadow V3 observation (docs/RESPONSE_PLAN_V3_SPEC.md §14): translated_v2 envelopes
/// recorded beside plan2 rows, complete disclosure/retention rules applied BEFORE
/// recording, no model involvement, no turn-path latency, forget coverage. All test plans
/// are synthetic — no real conversation data.
/// </summary>
public class RendererV3ShadowTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static ResponsePlan PlainPlan() => new()
    {
        TraceId = Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444"),
        Act = TurnIntent.Acknowledge,
        Content =
        [
            new PlannedContent(ContentKind.Interpretation, ContentRequirement.MustState,
                "The synthetic hinge stopped squeaking after one synthetic drop of oil.", "working-context"),
            new PlannedContent(ContentKind.Memory, ContentRequirement.MayUse,
                "The synthetic garden gnome collection reached eleven.", "active"),
        ],
        Tone = new ToneGuidance("short and casual", "good spirits", "warm, dry"),
    };

    private static PlanV3.PlanV3 ProtectedV3()
    {
        var v3 = V2Translation.FromV2(PlainPlan());
        return v3 with
        {
            Items = [.. v3.Items.Select((i, n) => n == 0
                ? i with
                {
                    Text = "A synthetic relative's synthetic scan results arrive on a synthetic Tuesday.",
                    Disclosure = Disclosure.restricted,
                    Owner = "principal:synthetic-relative",
                    Audience = ["usr-local"],
                    Retention = Retention.volatile_turn_only,
                }
                : i)],
        };
    }

    // ---- the envelope builder: privacy applied before anything persists -----------------

    [Fact]
    public void Envelope_ForPlainPlan_CarriesFullIdentityAndUnredactedText()
    {
        var v3 = V2Translation.FromV2(PlainPlan());
        var env = V3ShadowEnvelopeBuilder.Build(PlainPlan(), v3, null, 1,
            ["usr-local"], new RendererTrustContext(RendererTransport.local_loopback));

        Assert.Equal("translated_v2", env.PlanOrigin);
        Assert.Equal("plan/3", env.Protocol);
        Assert.True(env.Valid);
        Assert.True(env.V2Compatible);
        Assert.True(env.AudienceOk);
        Assert.False(env.ContainsProtected);
        Assert.Equal(0, env.RedactedItemCount);
        Assert.NotNull(env.RenderPromptHash);
        Assert.Null(env.CorrelationTag);
        Assert.Contains(env.Items, i => i.Text!.Contains("synthetic drop of oil"));
        Assert.Equal(64, env.V2SourceHash.Length);
        Assert.Equal(64, env.WirePlanHash.Length);
    }

    [Fact]
    public void Envelope_ForProtectedPlan_NeverContainsProtectedText()
    {
        var key = Convert.FromBase64String(Convert.ToBase64String("synthetic-deployment-secret"u8));
        var env = V3ShadowEnvelopeBuilder.Build(PlainPlan(), ProtectedV3(), key, 3,
            ["usr-local"], new RendererTrustContext(RendererTransport.local_loopback));

        Assert.True(env.ContainsProtected);
        Assert.Equal(1, env.RedactedItemCount);
        Assert.Null(env.RenderPromptHash);           // content-derived hash not persistable
        Assert.StartsWith("v3:", env.CorrelationTag); // keyed, versioned
        var serialized = JsonSerializer.Serialize(env);
        Assert.DoesNotContain("scan results", serialized);
        Assert.DoesNotContain("synthetic Tuesday", serialized);
        // Unprotected items still carry text; the redacted one carries metadata only.
        Assert.Contains(env.Items, i => i.Redacted && i.Text is null);
        Assert.Contains(env.Items, i => !i.Redacted && i.Text is not null);
    }

    // ---- the service: rows beside plan2, queue-borne, counted ---------------------------

    private sealed class CollectingRecorder : IShadowRecorder
    {
        public List<ShadowComparison> Rows { get; } = [];
        public bool IsRecording => true;
        public bool IsShadowing => true;
        public Task RecordAsync(ShadowComparison c, CancellationToken ct = default)
        { lock (Rows) Rows.Add(c); return Task.CompletedTask; }
        public Task<IReadOnlyList<ShadowAgreement>> GetAgreementAsync(DateTimeOffset s, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ShadowAgreement>>([]);
        public Task<IReadOnlyList<ShadowComparison>> GetDisagreementsAsync(string? s, int c, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ShadowComparison>>(Rows);
        public Task<IReadOnlyList<ShadowComparison>> GetCapturesAsync(string? s, int c, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ShadowComparison>>([]);
        public Task<int> ForgetCapturesAsync(IReadOnlyCollection<string> e, CancellationToken ct = default)
            => Task.FromResult(0);
    }

    private static RendererShadowService Service(CollectingRecorder recorder)
        => new(recorder,
            Options.Create(new CompanionOptions
            {
                RendererShadow = new RendererShadowOptions
                {
                    Enabled = true,
                    Endpoint = "http://127.0.0.1:59999", // dead: render fails, v3 row must not
                    TimeoutSeconds = 5,
                    CorrelationKeyBase64 = Convert.ToBase64String("synthetic-secret"u8),
                    CorrelationKeyVersion = 2,
                },
            }),
            NullLogger<RendererShadowService>.Instance,
            drainWindow: TimeSpan.FromSeconds(10));

    private static RendererShadowObservation Obs() => new()
    {
        TraceId = Guid.NewGuid(),
        Plan = PlainPlan(),
        Transcript = [("user", "synthetic hello")],
        UserMessage = "synthetic message about the synthetic hinge",
        ProductionResponse = "A synthetic reply.",
    };

    [Fact]
    public async Task QueuedObservation_ProducesAV3RowBesideThePlan2Attempt_EvenWhenTheRenderFails()
    {
        var recorder = new CollectingRecorder();
        var service = Service(recorder);
        service.Observe(Obs());
        await service.DisposeAsync();

        // Dead endpoint: no plan2 row (render failed) — but the v3 envelope row exists,
        // proving v3 observation is independent of renderer availability.
        var v3Rows = recorder.Rows.Where(r => r.Subject == RendererShadowService.RendererV3Subject).ToList();
        Assert.Single(v3Rows);
        var env = JsonSerializer.Deserialize<V3ShadowEnvelope>(v3Rows[0].Input!)!;
        Assert.Equal("translated_v2", env.PlanOrigin);
        Assert.True(env.Valid);
        Assert.True(env.V2Compatible);
        Assert.Equal("none", v3Rows[0].Applied);
        Assert.Null(v3Rows[0].Legacy);
        Assert.Null(v3Rows[0].Model);

        var c = service.Counters.V3!;
        Assert.Equal(1, c.Produced);
        Assert.Equal(1, c.Valid);
        Assert.Equal(1, c.V2Compatible);
        Assert.Equal(0, c.Failed);
        Assert.Equal(0, c.Dropped);
    }

    [Fact]
    public async Task TheV3Row_IsSweptByTheExistingRendererForgetClause()
    {
        await using var host = new TestHost(Now, settings: new Dictionary<string, string?>
        {
            ["Companion:RendererShadow:Enabled"] = "true",
        });
        using var scope = host.CreateScope();
        var recorder = scope.ServiceProvider.GetRequiredService<IShadowRecorder>();

        await recorder.RecordAsync(new ShadowComparison
        {
            Subject = RendererShadowService.RendererV3Subject,
            Legacy = null,
            Model = null,
            Applied = "none",
            Input = "{\"Items\":[{\"Text\":\"the synthetic gnome census reached eleven\"}]}",
        });

        var removed = await recorder.ForgetCapturesAsync(["the synthetic gnome census"]);
        Assert.Equal(1, removed);
    }
}
