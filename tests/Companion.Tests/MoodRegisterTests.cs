using System.Text.Json;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Companion.Infrastructure.Renderer;
using Companion.Infrastructure.Seeding;
using Companion.PlanV3;
using Companion.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Source 4b acceptance evidence (docs/SOURCE4_PHASE2_PLAN.md): 12 declared cases, 10 pass
/// criteria, fixed before implementation. The amended design's central claim is that a
/// StateRef points at a durable transition EVENT rather than a hash of a mutable value, so
/// several of these cases exist purely to prove the id resolves and the log composes.
/// </summary>
public class MoodRegisterTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private const string User = CompanionSeeder.DemoUserId;

    private static readonly Guid Trace = Guid.Parse("55555555-1111-2222-3333-444444444444");

    private static readonly PlanContributionContext Ctx = new(
        Trace, "acknowledge", "a synthetic message", "usr-synth", "companion-ava",
        SensitiveTurn: false);

    private static PlanV3.PlanV3 Seed() => new()
    {
        TraceId = Trace,
        Participants =
        [
            new Participant("usr-synth", ParticipantRole.user, "SynthUser"),
            new Participant("companion-ava", ParticipantRole.companion, "Ava"),
        ],
        Act = "acknowledge",
        Question = new QuestionPolicyBlock(QuestionPolicy.question_forbidden),
        Items = [],
        Register = PlanV3Codec.Canonicalize(new RegisterVector()),
    };

    private static AssemblyReport Assemble(params IPlanV3Contributor[] contributors)
        => PlanV3Assembler.Assemble(Ctx, contributors, SourceRegistry.Default, Seed());

    private static CompanionStateSnapshot State(double spirits, Guid? stateRef, int version = 1)
        => new() { Spirits = spirits, Energy = 0.6, StateRef = stateRef, Version = version };

    // ---- cases 1 + 2: the entire positive authority --------------------------------------

    [Theory]
    [InlineData(-0.9, "flat")]
    [InlineData(-0.3, "flat")]
    [InlineData(0.3, "raised")]
    [InlineData(0.85, "raised")]
    public void Cases1And2_SpiritsPastTheFloor_VoteIntensity_CitingTheTransition(
        double spirits, string expected)
    {
        var stateRef = Guid.NewGuid();
        var contributor = new MoodContributor(State(spirits, stateRef));
        var report = Assemble(contributor);

        var decision = Assert.Single(report.RegisterDecisions);
        Assert.Equal("intensity", decision.Dimension);
        Assert.Equal(expected, decision.Value);
        Assert.Equal("mood", decision.WinningSource);
        Assert.Equal("mood.spirits", decision.ReasonCode);
        Assert.Equal(expected, report.Plan.Register.Intensity);
        // Criterion 3: intensity and nothing else, no items ever.
        Assert.Empty(report.Plan.Items);
        Assert.Empty(report.AuthorityViolations);
        Assert.Equal($"voted-{expected}", contributor.Outcome);
    }

    // ---- case 3: the floor is silence, not a neutral vote --------------------------------

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.29)]
    [InlineData(-0.29)]
    public void Case3_SpiritsInsideTheFloor_ContributeSilence(double spirits)
    {
        var contributor = new MoodContributor(State(spirits, Guid.NewGuid()));
        var report = Assemble(contributor);

        // Not "intensity=even" — no decision at all, so nothing is displaced.
        Assert.Empty(report.RegisterDecisions);
        Assert.Equal("even", report.Plan.Register.Intensity);   // canonical default, unclaimed
        Assert.Equal("below-floor", contributor.Outcome);
    }

    // ---- case 4: no provenance, no standing ----------------------------------------------

    [Fact]
    public void Case4_AMoodWithNoTransitionToCite_DoesNotVote()
    {
        var contributor = new MoodContributor(State(-0.9, stateRef: null, version: 0));
        var report = Assemble(contributor);

        Assert.Empty(report.RegisterDecisions);
        Assert.Equal("no-state-ref", contributor.Outcome);
    }

    // ---- case 5: mood loses to an explicit preference ------------------------------------

    [Fact]
    public void Case5_MoodVersusAUserPreference_PreferenceWins_MoodRecordedAsLoser()
    {
        var preference = new UserPreferenceRecord
        {
            Id = Guid.NewGuid(), UserId = "usr-synth", Kind = UserPreferenceKind.Register,
            Dimension = "intensity", Value = "even", StatedAt = Now,
            EvidenceKind = "direct-instruction", EvidenceStatement = "keep an even keel",
        };

        var report = Assemble(
            new UserPreferenceContributor([preference]),
            new MoodContributor(State(-0.9, Guid.NewGuid())));

        var decision = Assert.Single(report.RegisterDecisions);
        // A transient mood cannot overwrite a standing instruction.
        Assert.Equal("user-preference", decision.WinningSource);
        Assert.Equal("even", report.Plan.Register.Intensity);
        Assert.Contains("mood:flat", decision.Losers);
    }

    // ---- cases 6 + 7: it can only ever modulate ------------------------------------------

    [Fact]
    public void Case6_MoodCannotProduceAnItem()
    {
        var report = PlanV3Assembler.Assemble(
            Ctx, [new ForgedMoodItem()], SourceRegistry.Default, Seed());

        Assert.Empty(report.Plan.Items);
        Assert.Contains(report.Outcomes, o => o.Decision == "rejected" || o.Decision == "downgraded");
    }

    private sealed class ForgedMoodItem : IPlanV3Contributor
    {
        public string SourceId => "mood";
        public PlanContributionResult Contribute(PlanContributionContext c) => new(
        [
            new ProposedItem
            {
                LocalId = "m1", Type = "mood-claim", Category = RenderCategory.claim,
                ProposedPolicy = ExpressionPolicy.must_express,
                Text = "Tell them you are feeling low today.",
                Provenance = new Provenance(Origin: "derived"),
                PlanningPromotion = true,
            },
        ]);
    }

    [Fact]
    public void Case7_MoodCannotRestrict()
    {
        var report = PlanV3Assembler.Assemble(
            Ctx, [new ForgedMoodRestriction()], SourceRegistry.Default, Seed());

        Assert.Empty(report.RegisterDecisions);
        Assert.Contains(report.AuthorityViolations,
            v => v.Contains("mood") && v.Contains("without restriction authority"));
    }

    private sealed class ForgedMoodRestriction : IPlanV3Contributor
    {
        public string SourceId => "mood";
        public PlanContributionResult Contribute(PlanContributionContext c) => new(
            [],
            [new RegisterProposal("intensity", "flat", "mood.spirits",
                new Provenance(Origin: "derived"), Restrictive: true)]);
    }

    // ---- case 8: the StateRef actually resolves ------------------------------------------

    [Fact]
    public async Task Case8_TheCitedStateRef_ResolvesInTheTransitionLog()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var tracker = sp.GetRequiredService<ICompanionStateTracker>();

        await tracker.NudgeAsync(User, -0.9);
        var snapshot = await tracker.BuildAsync(User);

        Assert.NotNull(snapshot.StateRef);
        Assert.Equal(1, snapshot.Version);

        // The vote cites that id as its evidence reference...
        var contribution = new MoodContributor(snapshot with { Spirits = -0.5 })
            .Contribute(Ctx);
        var vote = Assert.Single(contribution.Register!);
        Assert.Equal(snapshot.StateRef.ToString(), vote.Provenance!.EvidenceRef);

        // ...and it RESOLVES, which a hash of mutable state never would.
        var row = await sp.GetRequiredService<ICompanionMoodLog>().GetLatestAsync(User);
        Assert.NotNull(row);
        Assert.Equal(snapshot.StateRef, row!.Id);
        // Her spirits start at the profile default of 0.2, so one -0.9 nudge lands here.
        Assert.Equal(0.2 * 0.85 + -0.9 * 0.15, row.NewSpirits, 6);
        Assert.Equal(0.2, row.PreviousSpirits!.Value, 6);
        Assert.Equal(-0.9, row.AppliedValence!.Value, 6);
    }

    // ---- case 9: concurrent nudges compose ----------------------------------------------

    [Fact]
    public async Task Case9_ConcurrentNudges_AllLand_WithUniqueContiguousVersions()
    {
        await using var host = new TestHost(Now);
        var log = host.Services.GetRequiredService<ICompanionMoodLog>();

        // Twelve simultaneous appends against one user — the case a read-blend-write with no
        // guard would silently lose most of.
        await Task.WhenAll(Enumerable.Range(0, 12).Select(i =>
            log.AppendAsync(User, 0.0, 0.05, 0.5, Now.AddSeconds(i))));

        var history = await log.GetHistoryAsync(User);
        Assert.Equal(12, history.Count);
        // Unique and contiguous: nothing was lost, nothing collided.
        Assert.Equal(Enumerable.Range(1, 12), history.Select(t => t.Version));
        Assert.Equal(12, history.Select(t => t.Id).Distinct().Count());
        // They COMPOSED: each landed on the previous result rather than clobbering it.
        for (var i = 1; i < history.Count; i++)
            Assert.Equal(history[i - 1].NewSpirits, history[i].PreviousSpirits!.Value, 6);
        Assert.True(history[^1].NewSpirits > history[0].NewSpirits);
    }

    // ---- case 10: deterministic replay ---------------------------------------------------

    [Fact]
    public async Task Case10_ReplayingTheLog_ReproducesTheFinalSpiritsExactly()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var tracker = sp.GetRequiredService<ICompanionStateTracker>();

        foreach (var valence in new[] { -0.8, 0.4, -0.2, 0.9 })
            await tracker.NudgeAsync(User, valence);

        var history = await sp.GetRequiredService<ICompanionMoodLog>().GetHistoryAsync(User);
        Assert.Equal(4, history.Count);

        // Replay: same start, same valences, same arithmetic — every step must match the row.
        var replayed = history[0].PreviousSpirits!.Value;
        foreach (var t in history)
        {
            replayed = replayed * (1 - 0.15) + t.AppliedValence!.Value * 0.15;
            Assert.Equal(t.NewSpirits, replayed, 6);
        }
        Assert.Equal(history[^1].NewSpirits, replayed, 6);
    }

    // ---- case 11: decay carries a mood back under the floor by itself --------------------

    [Fact]
    public void Case11_DecayAlone_StopsAMoodFromVoting()
    {
        // A strong low that would vote today...
        var stateRef = Guid.NewGuid();
        Assert.Equal("flat", MoodContributor.Intensity(-0.8));

        // ...decayed across four half-lives is under the floor, and votes nothing. Nothing had
        // to expire it; time did.
        var decayed = CompanionStateTracker.DecayedSpirits(-0.8, Now, Now.AddDays(16));
        Assert.True(Math.Abs(decayed) < MoodContributor.Floor);

        var contributor = new MoodContributor(State(decayed, stateRef));
        Assert.Empty(Assemble(contributor).RegisterDecisions);
        Assert.Equal("below-floor", contributor.Outcome);
    }

    // ---- criteria ------------------------------------------------------------------------

    /// <summary>Criterion 1: this contributor cannot reach user-emotion types.</summary>
    [Fact]
    public void Criterion1_TheContributorTakesAvasOwnStateOnly()
    {
        var ctor = typeof(MoodContributor).GetConstructors().Single();
        var parameter = Assert.Single(ctor.GetParameters());
        Assert.Equal(typeof(CompanionStateSnapshot), parameter.ParameterType);
        // Named for the avoidance of doubt: not these.
        Assert.NotEqual(typeof(MoodReading), parameter.ParameterType);
        Assert.NotEqual(typeof(EmotionalSignal), parameter.ParameterType);
        Assert.NotEqual(typeof(RelationshipSnapshot), parameter.ParameterType);
    }

    /// <summary>Criterion 8: diagnostics carry tokens and ids, never prose.</summary>
    [Fact]
    public void Criterion8_TheMoodDecision_CarriesNoProse()
    {
        var report = Assemble(new MoodContributor(State(-0.9, Guid.NewGuid())));
        var serialized = JsonSerializer.Serialize(report.RegisterDecisions);

        Assert.Contains("mood.spirits", serialized);
        // Nothing from CompanionStateSnapshot.Describe() reaches a decision.
        Assert.DoesNotContain("heavy", serialized);
        Assert.DoesNotContain("subdued", serialized);
        Assert.DoesNotContain("spirits are", serialized);
    }

    // ---- case 12: live, through the real native-shadow call site -------------------------

    [Fact]
    public async Task Case12_LiveTurn_CarriesTheMoodDecisionIntoTheNativeRow()
    {
        var recorder = new CollectingRecorder();
        await using var host = new TestHost(
            Now,
            configureServices: s => s.AddSingleton<IShadowRecorder>(recorder),
            settings: new Dictionary<string, string?>
            {
                ["Companion:RendererShadow:Enabled"] = "true",
                ["Companion:RendererShadow:Endpoint"] = "http://127.0.0.1:59995",
                ["Companion:RendererShadow:TimeoutSeconds"] = "5",
            });

        Guid conversationId;
        using (var seed = host.CreateScope())
        {
            var sp = seed.ServiceProvider;
            conversationId = (await sp.GetRequiredService<IConversationStore>()
                .StartConversationAsync(User, "t", "mock", "test")).Id;
            // Drive her spirits well past the floor through the REAL tracker.
            var tracker = sp.GetRequiredService<ICompanionStateTracker>();
            for (var i = 0; i < 12; i++)
                await tracker.NudgeAsync(User, -1.0);
            Assert.True((await tracker.BuildAsync(User)).Spirits <= -MoodContributor.Floor);
        }

        using (var scope = host.CreateScope())
        {
            var trace = await scope.ServiceProvider.GetRequiredService<ICompanion>()
                .RespondAsync(User, conversationId, "How has your week been going?");
            Assert.Equal(TurnStatus.Answered, trace.Status);
        }

        var service = (RendererShadowService)host.Services.GetRequiredService<IRendererShadow>();
        await service.DisposeAsync();

        var row = Assert.Single(recorder.Rows, r => r.Subject == RendererShadowService.RendererV3Subject);
        var env = JsonSerializer.Deserialize<V3ShadowEnvelope>(row.Input!)!;

        Assert.NotNull(env.Assembly);
        var decision = Assert.Single(env.Assembly!.RegisterDecisions, d => d.Dimension == "intensity");
        Assert.Equal("mood", decision.WinningSource);
        Assert.Equal("flat", decision.Value);
        Assert.Contains("intensity=flat", env.Native!.RegisterLine);
        Assert.Empty(env.Assembly.AuthorityViolations);
        // No prose from her mood description anywhere in the row.
        Assert.DoesNotContain("subdued", row.Input!);
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
            => Task.FromResult<IReadOnlyList<ShadowComparison>>(Rows.Where(r => r.Subject == s).ToList());
        public Task<int> PruneAsync(DateTimeOffset o, CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> ForgetCapturesAsync(IReadOnlyCollection<string> excerpts, CancellationToken ct = default)
            => Task.FromResult(0);
    }
}
