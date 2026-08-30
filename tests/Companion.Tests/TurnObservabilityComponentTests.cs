using System.Reflection;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Companion.Core;
using Companion.Core.Turns.Observability;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The contract for the turn's record of itself. One invariant runs through all of it:
/// observability may record what happened, but it cannot select, replace, suppress, reorder, or
/// otherwise affect the displayed reply or durable cognitive state.
///
/// Several of these are structural rather than behavioural on purpose — an assertion about what
/// a type CANNOT express outlives an assertion about what one call happened to do.
/// </summary>
public class TurnObservabilityComponentTests
{
    // ---- the invariant, stated structurally -------------------------------------------------

    [Fact]
    public void No_observability_method_can_hand_back_a_reply()
    {
        var returns = typeof(TurnObservability)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Select(m => m.ReturnType)
            .Select(t => t == typeof(Task) ? typeof(void)
                : t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Task<>)
                    ? t.GetGenericArguments()[0] : t)
            .ToList();

        Assert.NotEmpty(returns);
        // A string return would be a reply, or a candidate for one. Nothing here returns text.
        Assert.DoesNotContain(returns, t => t == typeof(string));
        // Everything that IS returned is either nothing, a trace annotation, or a bool about
        // whether recording is on. None of those can carry an utterance.
        Assert.All(returns, t => Assert.True(
            t == typeof(void) || t == typeof(bool)
            || t == typeof(DecisionRecord) || t == typeof(IReadOnlyList<DecisionRecord>),
            $"unexpected observability return type {t.Name}"));
    }

    [Fact]
    public void Snapshots_have_nowhere_to_put_a_reply_that_was_not_displayed()
    {
        // The same structural defence PostTurnRequest uses: a losing production candidate, a
        // guard-rejected canary candidate, and pre-gate text have no field to live in, so they
        // cannot reach a capture or diagnostic row by mistake.
        foreach (var snapshot in new[] { typeof(TurnCaptureSnapshot), typeof(TurnRecordSnapshot) })
        {
            var replyish = snapshot.GetProperties()
                .Where(p => p.PropertyType == typeof(string))
                .Select(p => p.Name)
                .Where(n => n.Contains("Reply", StringComparison.OrdinalIgnoreCase)
                            || n.Contains("Response", StringComparison.OrdinalIgnoreCase)
                            || n.Contains("Candidate", StringComparison.OrdinalIgnoreCase))
                .ToList();

            Assert.Single(replyish);
            Assert.DoesNotContain(replyish, n => n.Contains("Candidate", StringComparison.OrdinalIgnoreCase));
        }
    }

    // ---- recording is genuinely optional ----------------------------------------------------

    [Fact]
    public async Task With_no_recorder_configured_nothing_is_written_and_nothing_throws()
    {
        var obs = Build(out _, out var traceLog, withRecorder: false);

        Assert.False(obs.IsRecording);
        await obs.RecordGateRefusalAsync("u", Guid.NewGuid(), Guid.NewGuid(),
            new Companion.Core.Turns.Execution.GateRefusal { Reason = "r", Enforced = true });
        await obs.ObserveTeachingAsync("u", Guid.NewGuid(), Guid.NewGuid(), "a widget is a thing", true);
        await obs.CaptureExchangeAsync(Capture("hello", "hi"));

        // The trace log is not optional and still gets its entry; the shadow store simply is not there.
        Assert.Empty(traceLog.Entries);
    }

    // ---- the gate row ------------------------------------------------------------------------

    [Fact]
    public async Task Gate_refusal_records_what_execution_already_decided()
    {
        var obs = Build(out var rec, out _);
        var msgId = Guid.NewGuid();
        var convId = Guid.NewGuid();

        await obs.RecordGateRefusalAsync("u1", msgId, convId,
            new Companion.Core.Turns.Execution.GateRefusal { Reason = "invented apology", Enforced = true });

        var row = Assert.Single(rec.Rows);
        Assert.Equal("safety.gate", row.Subject);
        Assert.Equal("u1", row.UserId);
        // Exact identity, so /forget reaches it.
        Assert.Equal(msgId, row.SourceMessageId);
        Assert.Equal(convId, row.ConversationId);
        Assert.Equal("model", row.Applied);
        Assert.False(row.Agreed);
    }

    [Fact]
    public async Task An_unenforced_gate_refusal_is_still_recorded_as_legacy()
    {
        var obs = Build(out var rec, out _);
        await obs.RecordGateRefusalAsync("u1", Guid.NewGuid(), Guid.NewGuid(),
            new Companion.Core.Turns.Execution.GateRefusal { Reason = "shadow only", Enforced = false });

        Assert.Equal("legacy", Assert.Single(rec.Rows).Applied);
    }

    // ---- plan fidelity -----------------------------------------------------------------------

    [Fact]
    public async Task Plan_fidelity_returns_decisions_it_never_enforces()
    {
        var obs = Build(out var rec, out _);
        // A plan that recorded agreement, against a reply that apologises for nothing.
        var plan = Plan() with
        {
            Acknowledgments = [new Acknowledgment(AckKind.AgreementConfirmed, ErrorOwner.Nobody, "yes")],
        };

        var decisions = await obs.RecordPlanFidelityAsync(
            "u1", Guid.NewGuid(), Guid.NewGuid(), plan,
            "I'm so sorry, that was my mistake.");

        Assert.All(decisions, d => Assert.Equal("plan.fidelity", d.Stage));
        // Every violation produces BOTH a decision and a row, and the row is a capture
        // (Model null) — a measurement, never an override.
        Assert.Equal(decisions.Count, rec.Rows.Count(r => r.Subject == "plan.fidelity"));
        Assert.All(rec.Rows.Where(r => r.Subject == "plan.fidelity"), r =>
        {
            Assert.Null(r.Model);
            Assert.Equal("legacy", r.Applied);
        });
    }

    [Fact]
    public async Task A_compliant_reply_produces_no_fidelity_decisions()
    {
        var obs = Build(out var rec, out _);
        var decisions = await obs.RecordPlanFidelityAsync(
            "u1", Guid.NewGuid(), Guid.NewGuid(), Plan(), "Sure, here it is.");

        Assert.Empty(decisions);
        Assert.Empty(rec.Rows);
    }

    // ---- renderer shadow eligibility ---------------------------------------------------------

    [Theory]
    // sensitive, inCharacter, toolCalls, expected verdict, expected observations
    [InlineData(false, false, 0, "observed", 1)]
    [InlineData(false, false, 2, "plan-only", 1)]
    [InlineData(false, true, 0, "plan-only", 1)]
    [InlineData(true, false, 0, "skipped", 0)]
    [InlineData(true, true, 3, "skipped", 0)]
    public void Renderer_shadow_eligibility_is_unchanged(
        bool sensitive, bool inCharacter, int toolCalls, string verdict, int observations)
    {
        var obs = Build(out _, out _);
        var shadow = new CountingRendererShadow();
        var built = 0;

        var decision = obs.ObserveRendererShadow(
            new RendererShadowEligibility
            {
                Shadow = shadow,
                Sensitive = sensitive,
                InCharacter = inCharacter,
                ToolCallCount = toolCalls,
            },
            () => { built++; return Observation(); });

        Assert.Equal(verdict, decision!.Verdict);
        Assert.Equal(observations, shadow.Observed + shadow.PlanOnly);
        // A skipped turn does not even build the snapshot, so a privacy-sensitive turn's text
        // is never assembled into an observation object at all.
        Assert.Equal(observations, built);
    }

    [Fact]
    public void A_privacy_sensitive_turn_never_reaches_the_renderer_shadow()
    {
        var obs = Build(out _, out _);
        var shadow = new CountingRendererShadow();

        obs.ObserveRendererShadow(
            new RendererShadowEligibility
            {
                Shadow = shadow, Sensitive = true, InCharacter = false, ToolCallCount = 0,
            },
            () => throw new InvalidOperationException("the snapshot must not be built"));

        Assert.Equal(0, shadow.Observed);
        Assert.Equal(0, shadow.PlanOnly);
    }

    // ---- the interleaved teaching capture -----------------------------------------------------

    [Fact]
    public async Task Teaching_observation_keeps_the_loose_shape_gate()
    {
        var obs = Build(out var rec, out _);

        // No copular sentence at all, so it is not even part of the capture population.
        await obs.ObserveTeachingAsync("u", Guid.NewGuid(), Guid.NewGuid(), "please close the door", false);
        Assert.Empty(rec.Rows);

        await obs.ObserveTeachingAsync("u", Guid.NewGuid(), Guid.NewGuid(), "a kerf is a saw cut", true);
        var row = Assert.Single(rec.Rows);
        Assert.Equal("knowledge.teaching", row.Subject);
        Assert.Equal("true", row.Legacy);
    }

    [Fact]
    public void Post_turn_effects_depend_on_the_observer_not_on_a_recorder()
    {
        // The class that owns durable cognitive state must not be able to reach a general
        // recorder — narrowing this dependency is the point of ITeachingObserver.
        var ctor = typeof(Companion.Core.Turns.PostTurn.PostTurnEffects)
            .GetConstructors().Single();
        var types = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        Assert.Contains(typeof(ITeachingObserver), types);
        Assert.DoesNotContain(typeof(IShadowRecorder), types);
    }

    [Fact]
    public void The_observer_interface_can_only_observe()
    {
        var methods = typeof(ITeachingObserver).GetMethods();
        var only = Assert.Single(methods);
        Assert.Equal(nameof(ITeachingObserver.ObserveTeachingAsync), only.Name);
        Assert.Equal(typeof(Task), only.ReturnType);
    }

    // ---- privacy ------------------------------------------------------------------------------

    [Fact]
    public async Task Secret_looking_content_is_dropped_from_capture_rows()
    {
        var obs = Build(out var rec, out _);
        const string secret = "the api_key: A7f9Kd2LmQ8xZp1RtY";

        await obs.CaptureExchangeAsync(Capture(secret, "noted"));

        // The row still exists — structure survives, the words do not.
        Assert.NotEmpty(rec.Rows);
        Assert.All(rec.Rows, r => Assert.DoesNotContain("A7f9Kd2LmQ8xZp1RtY", r.Input ?? ""));
        Assert.All(rec.Rows.Where(r => r.Subject == "turn.intent"), r => Assert.Null(r.Input));
    }

    [Fact]
    public async Task Capture_rows_carry_the_displayed_reply_and_exact_identity()
    {
        var obs = Build(out var rec, out _);
        var snap = Capture("who is Ada?", "Ada Lovelace wrote the first algorithm.");

        await obs.CaptureExchangeAsync(snap);

        var intent = Assert.Single(rec.Rows, r => r.Subject == "turn.intent");
        Assert.Equal(snap.ExtractionSource.Id, intent.SourceMessageId);
        Assert.Equal(snap.ConversationId, intent.ConversationId);
        Assert.Equal(snap.UserId, intent.UserId);
        // A capture, not a comparison: nothing was asked, so nothing agreed.
        Assert.Null(intent.Model);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static TurnObservability Build(
        out CollectingRecorder recorder, out CollectingTraceLog traceLog, bool withRecorder = true)
    {
        recorder = new CollectingRecorder();
        traceLog = new CollectingTraceLog();
        return new TurnObservability(
            traceLog,
            Options.Create(new CompanionOptions()),
            NullLogger<TurnObservability>.Instance,
            withRecorder ? recorder : null);
    }

    private static TurnCaptureSnapshot Capture(string userText, string displayedReply)
    {
        var conversationId = Guid.NewGuid();
        var source = new Message
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            UserId = "u1",
            Role = MessageRole.User,
            Content = userText,
            Timestamp = DateTimeOffset.UnixEpoch,
        };
        return new TurnCaptureSnapshot
        {
            UserId = "u1",
            ConversationId = conversationId,
            ExtractionSource = source,
            DisplayedReply = displayedReply,
            Recent = [source],
            Working = Working(userText),
            Intent = TurnIntentClassifier.Classify(Working(userText), userText, 0),
            Selected = [],
            Focal = null,
        };
    }

    private static ResponsePlan Plan() => new()
    {
        Act = TurnIntent.AnswerQuestion,
        Tone = new ToneGuidance(null, null, null),
    };

    private static WorkingContextState Working(string userText) => new()
    {
        Move = ConversationMove.NewTopic,
        RawQuery = userText,
        RetrievalQuery = userText,
    };

    private static RendererShadowObservation Observation() => new()
    {
        TraceId = Guid.NewGuid(),
        UserId = "u1",
        SourceMessageId = Guid.NewGuid(),
        ConversationId = Guid.NewGuid(),
        Plan = Plan(),
        Transcript = [],
        UserMessage = "hello",
        ProductionResponse = "hi",
    };

    private sealed class CountingRendererShadow : IRendererShadow
    {
        public int Observed { get; private set; }
        public int PlanOnly { get; private set; }
        public bool IsObserving => true;
        public bool IsCanaryFor(string userId) => false;
        public void Observe(RendererShadowObservation observation) => Observed++;
        public void ObservePlanOnly(RendererShadowObservation observation) => PlanOnly++;
        public RendererShadowCounters Counters => new(0, 0, 0, 0, 0);
        public Task<RendererCanaryResult?> RenderForDisplayAsync(
            RendererShadowObservation observation, bool record, CancellationToken ct)
            => Task.FromResult<RendererCanaryResult?>(null);

    public bool IsMouthObserving => false;

    public MouthCounters MouthCounters => new(0, 0, 0, 0, null);


    public bool IsMouthCanaryFor(string userId) => false;

    public Task<RendererCanaryResult?> RenderMouthForDisplayAsync(
        RendererShadowObservation observation, bool record, CancellationToken ct)
        => Task.FromResult<RendererCanaryResult?>(null);

    public void ObserveMouth(RendererShadowObservation observation)
    {
    }

    public Task<(bool Ok, string Detail)> VerifyMouthIdentityAsync(CancellationToken ct)
        => Task.FromResult((true, "test double"));
    }

    private sealed class CollectingTraceLog : ITurnTraceLog
    {
        public List<TurnDiagnostics> Entries { get; } = [];
        public void Record(string userId, TurnDiagnostics diagnostics) => Entries.Add(diagnostics);
        public TurnDiagnostics? Last(string userId) => Entries.LastOrDefault();
        public IReadOnlyList<TurnDiagnostics> Recent(string userId, int count)
            => Entries.TakeLast(count).ToList();
    }

    private sealed class CollectingRecorder : IShadowRecorder
    {
        public List<ShadowComparison> Rows { get; } = [];
        public bool IsRecording => true;
        public bool IsShadowing => true;
        public Task RecordAsync(ShadowComparison c, CancellationToken ct = default)
        { Rows.Add(c); return Task.CompletedTask; }
        public Task<IReadOnlyList<ShadowAgreement>> GetAgreementAsync(DateTimeOffset s, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ShadowAgreement>>([]);
        public Task<IReadOnlyList<ShadowComparison>> GetDisagreementsAsync(string? s, int c, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ShadowComparison>>(Rows);
        public Task<IReadOnlyList<ShadowComparison>> GetCapturesAsync(string? s, int c, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ShadowComparison>>([]);
        public Task<int> PruneAsync(DateTimeOffset o, CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> ForgetByEvidenceAsync(
            string userId, IReadOnlyCollection<Guid> messageIds, DateTimeOffset now,
            Guid? memoryId = null, CancellationToken ct = default) => Task.FromResult(0);
    }
}
