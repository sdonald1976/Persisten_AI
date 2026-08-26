using System.Text.Json;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Infrastructure.Renderer;
using Companion.Infrastructure.Seeding;
using Companion.PlanV3;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Source 4c acceptance evidence (docs/SOURCE4_PHASE3_PLAN.md): 9 declared cases, 8 pass
/// criteria, fixed before it ran.
///
/// The design claim under test is that familiarity only ever RESTRAINS. Most of these cases
/// therefore assert absence — that tenure grants nothing, that closeness unlocks nothing, and
/// that no user-emotion input is reachable at all.
/// </summary>
public class FamiliarityRegisterTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private const string User = CompanionSeeder.DemoUserId;

    private static readonly Guid Trace = Guid.Parse("66666666-1111-2222-3333-444444444444");

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

    private static FamiliaritySnapshot Familiarity(FamiliarityStage stage, double days = 0, int messages = 0)
        => new() { DaysKnown = days, UserMessages = messages, Stage = stage };

    // ---- case 1: the entire positive authority -------------------------------------------

    [Fact]
    public void Case1_ANewRelationship_RestrainsVerbosityAndTeasing()
    {
        var contributor = new FamiliarityContributor(Familiarity(FamiliarityStage.New, 1, 4));
        var report = Assemble(contributor);

        Assert.Equal(2, report.RegisterDecisions.Count);
        Assert.All(report.RegisterDecisions, d =>
        {
            Assert.Equal("relationship", d.WinningSource);
            Assert.Equal("relationship.familiarity-stage", d.ReasonCode);
        });
        Assert.Equal("short", report.Plan.Register.Verbosity);
        Assert.Equal("off", report.Plan.Register.Teasing);
        Assert.Empty(report.Plan.Items);
        Assert.Empty(report.AuthorityViolations);
        Assert.Equal("voted-new", contributor.Outcome);
    }

    // ---- cases 2 + 3: tenure grants nothing ----------------------------------------------

    [Theory]
    [InlineData(FamiliarityStage.Acquainted)]
    [InlineData(FamiliarityStage.Familiar)]
    [InlineData(FamiliarityStage.Close)]
    public void Case2_EveryLaterStage_VotesNothingAtAll(FamiliarityStage stage)
    {
        var contributor = new FamiliarityContributor(Familiarity(stage, 400, 2000));
        var report = Assemble(contributor);

        Assert.Empty(report.RegisterDecisions);
        Assert.StartsWith("no-vote-", contributor.Outcome);
    }

    [Fact]
    public void Case3_YearsOfHistory_UnlocksNoAffectionAdjacentDimension()
    {
        // The strongest possible case for "surely now she can be warmer": five years, thousands
        // of messages. It still grants nothing — closeness is not a register permission.
        var report = Assemble(new FamiliarityContributor(
            Familiarity(FamiliarityStage.Close, days: 1825, messages: 9000)));

        Assert.Empty(report.RegisterDecisions);
        Assert.Equal("plain", report.Plan.Register.Warmth);
        Assert.Equal("off", report.Plan.Register.Teasing);
        Assert.Equal("off", report.Plan.Register.Playfulness);
        Assert.Equal("conversational", report.Plan.Register.Verbosity);
    }

    // ---- case 4: an explicit instruction outranks the relationship ------------------------

    [Fact]
    public void Case4_AUserPreference_BeatsFamiliarity_WithFamiliarityRecordedAsLoser()
    {
        var preference = new UserPreferenceRecord
        {
            Id = Guid.NewGuid(), UserId = "usr-synth", Kind = UserPreferenceKind.Register,
            Dimension = "verbosity", Value = "expansive", StatedAt = Now,
            EvidenceKind = "direct-instruction", EvidenceStatement = "from now on, give me more detail",
        };

        var report = Assemble(
            new UserPreferenceContributor([preference]),
            new FamiliarityContributor(Familiarity(FamiliarityStage.New, 1, 4)));

        var verbosity = Assert.Single(report.RegisterDecisions, d => d.Dimension == "verbosity");
        Assert.Equal("user-preference", verbosity.WinningSource);
        Assert.Equal("expansive", report.Plan.Register.Verbosity);
        Assert.Contains("relationship:short", verbosity.Losers);
        // Its other vote is untouched by losing this one.
        Assert.Equal("off", report.Plan.Register.Teasing);
    }

    // ---- case 5: familiarity outranks the turn's own read ---------------------------------

    [Fact]
    public void Case5_FamiliarityBeatsWorkingContext_OnTheSameDimension()
    {
        var report = Assemble(
            new FamiliarityContributor(Familiarity(FamiliarityStage.New, 1, 4)),
            new WorkingContextContributor(Trace, ConversationMove.Correction, resolution: null));

        var verbosity = Assert.Single(report.RegisterDecisions, d => d.Dimension == "verbosity");
        // Both want "short" here, but the DECISION must still name the higher authority.
        Assert.Equal("relationship", verbosity.WinningSource);
        Assert.Contains("working-context-register:short", verbosity.Losers);
    }

    // ---- cases 6 + 7: modulation only -----------------------------------------------------

    [Fact]
    public void Case6_FamiliarityCannotProduceAnItem()
    {
        var report = PlanV3Assembler.Assemble(
            Ctx, [new ForgedRelationshipItem()], SourceRegistry.Default, Seed());

        Assert.Empty(report.Plan.Items);
        Assert.Contains(report.Outcomes, o => o.Decision is "rejected" or "downgraded");
    }

    private sealed class ForgedRelationshipItem : IPlanV3Contributor
    {
        public string SourceId => "relationship";
        public PlanContributionResult Contribute(PlanContributionContext c) => new(
        [
            new ProposedItem
            {
                LocalId = "r1", Type = "closeness", Category = RenderCategory.claim,
                ProposedPolicy = ExpressionPolicy.must_express,
                Text = "You two are close now, so be affectionate.",
                Provenance = new Provenance(Origin: "derived"),
                PlanningPromotion = true,
            },
        ]);
    }

    [Fact]
    public void Case7_FamiliarityCannotRestrict()
    {
        var report = PlanV3Assembler.Assemble(
            Ctx, [new ForgedRelationshipRestriction()], SourceRegistry.Default, Seed());

        Assert.Empty(report.RegisterDecisions);
        Assert.Contains(report.AuthorityViolations,
            v => v.Contains("relationship") && v.Contains("without restriction authority"));
    }

    private sealed class ForgedRelationshipRestriction : IPlanV3Contributor
    {
        public string SourceId => "relationship";
        public PlanContributionResult Contribute(PlanContributionContext c) => new(
            [],
            [new RegisterProposal("teasing", "off", "relationship.familiarity-stage",
                new Provenance(Origin: "derived"), Restrictive: true)]);
    }

    // ---- case 8: sentiment is unreachable by construction ---------------------------------

    [Fact]
    public void Case8_TheContributorCannotReachAnyUserEmotionInput()
    {
        var ctor = typeof(FamiliarityContributor).GetConstructors().Single();
        var parameter = Assert.Single(ctor.GetParameters());

        Assert.Equal(typeof(FamiliaritySnapshot), parameter.ParameterType);
        // The excluded set, named so a future change has to delete an assertion to break it.
        Assert.NotEqual(typeof(RelationshipSnapshot), parameter.ParameterType);
        Assert.NotEqual(typeof(EmotionalSignal), parameter.ParameterType);
        Assert.NotEqual(typeof(MoodReading), parameter.ParameterType);
    }

    /// <summary>Criterion 6: the evidence ref is the two counts, and no prose travels.</summary>
    [Fact]
    public void Criterion6_EvidenceIsTheCounts_AndNoRelationshipProseAppears()
    {
        var contribution = new FamiliarityContributor(Familiarity(FamiliarityStage.New, 2, 7))
            .Contribute(Ctx);

        var vote = contribution.Register![0];
        Assert.Equal("familiarity:days=2;messages=7", vote.Provenance!.EvidenceRef);

        var serialized = JsonSerializer.Serialize(contribution);
        // Nothing from FamiliaritySnapshot.Describe() reaches the votes.
        Assert.DoesNotContain("only just met", serialized);
        Assert.DoesNotContain("presume closeness", serialized);
        Assert.DoesNotContain("shared history", serialized);
    }

    // ---- case 9: live, through the real native-shadow call site ---------------------------

    [Fact]
    public async Task Case9_LiveTurn_ForANewRelationship_CarriesTheFamiliarityDecision()
    {
        var recorder = new CollectingRecorder();
        await using var host = new TestHost(
            Now,
            configureServices: s => s.AddSingleton<IShadowRecorder>(recorder),
            settings: new Dictionary<string, string?>
            {
                ["Companion:RendererShadow:Enabled"] = "true",
                ["Companion:RendererShadow:Endpoint"] = "http://127.0.0.1:59994",
                ["Companion:RendererShadow:TimeoutSeconds"] = "5",
            });

        Guid conversationId;
        using (var seed = host.CreateScope())
        {
            conversationId = (await seed.ServiceProvider.GetRequiredService<IConversationStore>()
                .StartConversationAsync(User, "t", "mock", "test")).Id;
        }

        // A brand-new relationship: no tenure, almost no messages — stage New.
        using (var scope = host.CreateScope())
        {
            var trace = await scope.ServiceProvider.GetRequiredService<ICompanion>()
                .RespondAsync(User, conversationId, "Hey, what do you make of the weather today?");
            Assert.Equal(TurnStatus.Answered, trace.Status);
        }

        var service = (RendererShadowService)host.Services.GetRequiredService<IRendererShadow>();
        await service.DisposeAsync();

        var row = Assert.Single(recorder.Rows, r => r.Subject == RendererShadowService.RendererV3Subject);
        var env = JsonSerializer.Deserialize<V3ShadowEnvelope>(row.Input!)!;

        Assert.NotNull(env.Assembly);
        var teasing = Assert.Single(env.Assembly!.RegisterDecisions, d => d.Dimension == "teasing");
        Assert.Equal("relationship", teasing.WinningSource);
        Assert.Equal("relationship.familiarity-stage", teasing.ReasonCode);
        Assert.Contains("teasing=off", env.Native!.RegisterLine);
        Assert.Empty(env.Assembly.AuthorityViolations);

        // No relationship prose in the row, and nothing claiming what the user feels.
        Assert.DoesNotContain("has seemed", row.Input!);
        Assert.DoesNotContain("only just met", row.Input!);
    }

    private sealed class CollectingRecorder : IShadowRecorder
    {
        public Task<int> ForgetByEvidenceAsync(
            string userId, IReadOnlyCollection<Guid> messageIds, DateTimeOffset now,
            Guid? memoryId = null, CancellationToken ct = default) => Task.FromResult(0);

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
    }
}
