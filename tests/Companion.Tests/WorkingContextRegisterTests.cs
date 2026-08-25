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
/// Source 4a acceptance evidence (docs/SOURCE4_PHASE1_PLAN.md): the 10 declared cases and
/// 8 pass criteria for the working-context register contribution, fixed before it ran.
///
/// The authority under test is deliberately tiny — verbosity, from two typed moves — so most
/// of these cases are about what it must NOT do.
/// </summary>
public class WorkingContextRegisterTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private const string User = CompanionSeeder.DemoUserId;

    private static readonly Guid Trace = Guid.Parse("44444444-1111-2222-3333-444444444444");

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

    // ---- cases 1 + 2: the entire positive authority --------------------------------------

    [Theory]
    [InlineData(ConversationMove.ConfirmsClaim)]
    [InlineData(ConversationMove.Correction)]
    public void Cases1And2_TheTwoVotingMoves_ProduceOneShortVerbosityVote(ConversationMove move)
    {
        var contributor = new WorkingContextContributor(Trace, move, resolution: null);
        var report = Assemble(contributor);

        var decision = Assert.Single(report.RegisterDecisions);
        Assert.Equal("verbosity", decision.Dimension);
        Assert.Equal("short", decision.Value);
        Assert.Equal("working-context-register", decision.WinningSource);
        Assert.Equal("working-context.move", decision.ReasonCode);
        Assert.Equal("short", report.Plan.Register.Verbosity);
        Assert.Empty(report.AuthorityViolations);
        // Criterion 1: never an item, only ever a vote.
        Assert.Empty(report.Plan.Items);
        Assert.StartsWith("voted-", contributor.Outcome);
    }

    // ---- case 3: everything else votes nothing -------------------------------------------

    [Theory]
    [InlineData(ConversationMove.NewTopic)]
    [InlineData(ConversationMove.ContinuesThread)]
    [InlineData(ConversationMove.AnswersOpenQuestion)]
    [InlineData(ConversationMove.ResolvesReference)]
    public void Case3_MovesWithNoHonestVerbosityImplication_VoteNothing(ConversationMove move)
    {
        var contributor = new WorkingContextContributor(Trace, move, resolution: null);
        var report = Assemble(contributor);

        Assert.Empty(report.RegisterDecisions);
        Assert.Equal("conversational", report.Plan.Register.Verbosity);   // canonical default
        Assert.StartsWith("no-signal-", contributor.Outcome);
    }

    // ---- case 4: a guess suppresses the whole contribution -------------------------------

    [Fact]
    public void Case4_AGuessedResolution_SuppressesEvenAVotingMove()
    {
        var contributor = new WorkingContextContributor(
            Trace, ConversationMove.Correction, ResolutionConfidence.Guess);
        var report = Assemble(contributor);

        Assert.Empty(report.RegisterDecisions);
        Assert.Equal("conversational", report.Plan.Register.Verbosity);
        Assert.Equal("suppressed-guess", contributor.Outcome);
    }

    // ---- case 5: real resolutions do not suppress ----------------------------------------

    [Theory]
    [InlineData(ResolutionConfidence.Exact)]
    [InlineData(ResolutionConfidence.Unambiguous)]
    [InlineData(null)]
    public void Case5_ExactUnambiguousOrNothingResolved_StillVotes(ResolutionConfidence? resolution)
    {
        var report = Assemble(new WorkingContextContributor(
            Trace, ConversationMove.ConfirmsClaim, resolution));

        Assert.Equal("short", report.Plan.Register.Verbosity);
    }

    // ---- case 6: turn-local validity expires automatically -------------------------------

    [Fact]
    public void Case6_AReadingFromAnotherTurn_ExpiresAndContributesNothing()
    {
        // Same state, different turn: exactly the leak turn-local validity has to stop.
        var stale = new WorkingContextContributor(
            Guid.Parse("99999999-9999-9999-9999-999999999999"),
            ConversationMove.Correction, resolution: null);

        var report = Assemble(stale);

        Assert.Empty(report.RegisterDecisions);
        Assert.Equal("expired-different-turn", stale.Outcome);
    }

    // ---- case 7: mixed-dimension conflict on the SAME dimension --------------------------

    [Fact]
    public void Case7_VerbosityConflict_UserPreferenceWins_WorkingContextRecordedAsLoser()
    {
        var preference = new UserPreferenceRecord
        {
            Id = Guid.NewGuid(),
            UserId = "usr-synth",
            Kind = UserPreferenceKind.Register,
            Dimension = "verbosity",
            Value = "expansive",
            StatedAt = Now,
            EvidenceKind = "direct-instruction",
            EvidenceStatement = "from now on, give me more detail",
        };

        var report = Assemble(
            new UserPreferenceContributor([preference]),
            new WorkingContextContributor(Trace, ConversationMove.Correction, resolution: null));

        var decision = Assert.Single(report.RegisterDecisions);
        // §5.4: user-preference outranks working-context. The turn's read of the moment
        // cannot overrule a standing instruction.
        Assert.Equal("user-preference", decision.WinningSource);
        Assert.Equal("expansive", decision.Value);
        Assert.Contains("working-context-register:short", decision.Losers);
        Assert.Equal("expansive", report.Plan.Register.Verbosity);
    }

    // ---- case 8: different dimensions survive independently ------------------------------

    [Fact]
    public void Case8_MixedDimensions_ResolveIndependently_WithNoCrossTalk()
    {
        var warm = new UserPreferenceRecord
        {
            Id = Guid.NewGuid(), UserId = "usr-synth", Kind = UserPreferenceKind.Register,
            Dimension = "warmth", Value = "warm", StatedAt = Now,
            EvidenceKind = "direct-instruction", EvidenceStatement = "be warmer",
        };
        var report = Assemble(
            new UserPreferenceContributor([warm]),
            new HostingConfigContributor(new Dictionary<string, string> { ["bluntness"] = "blunt" }),
            new WorkingContextContributor(Trace, ConversationMove.ConfirmsClaim, resolution: null));

        // warm + blunt + short, all at once, each from a different authority.
        Assert.Equal("warm", report.Plan.Register.Warmth);
        Assert.Equal("blunt", report.Plan.Register.Bluntness);
        Assert.Equal("short", report.Plan.Register.Verbosity);
        Assert.Equal(3, report.RegisterDecisions.Count);
        Assert.All(report.RegisterDecisions, d => Assert.Empty(d.Losers));
        Assert.Empty(report.AuthorityViolations);
    }

    // ---- case 9: it cannot restrict ------------------------------------------------------

    [Fact]
    public void Case9_ARestrictiveVoteFromThisSource_IsARecordedViolation()
    {
        var report = PlanV3Assembler.Assemble(
            Ctx, [new RestrictiveWorkingContext()], SourceRegistry.Default, Seed());

        Assert.Empty(report.RegisterDecisions);
        Assert.Contains(report.AuthorityViolations,
            v => v.Contains("working-context-register")
                 && v.Contains("restrictive register value without restriction authority"));
    }

    private sealed class RestrictiveWorkingContext : IPlanV3Contributor
    {
        public string SourceId => "working-context-register";
        public PlanContributionResult Contribute(PlanContributionContext c) => new(
            [],
            [new RegisterProposal("verbosity", "terse", "working-context.move",
                new Provenance(Origin: "derived"), Restrictive: true)]);
    }

    // ---- criterion 2: prose is not constructor-reachable ---------------------------------

    [Fact]
    public void Criterion2_TheContributorCannotReceiveProse()
    {
        var ctor = typeof(WorkingContextContributor).GetConstructors().Single();
        var types = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        // Guid, ConversationMove, ResolutionConfidence?, Guid? — and not one string.
        Assert.DoesNotContain(typeof(string), types);
        Assert.DoesNotContain(typeof(WorkingContextState), types);
        Assert.All(types, t =>
            Assert.True(t == typeof(Guid) || t == typeof(Guid?)
                        || t == typeof(ConversationMove) || t == typeof(ResolutionConfidence?),
                $"unexpected constructor input {t.Name}"));
    }

    // ---- criterion 6: diagnostics are content-safe ---------------------------------------

    [Fact]
    public void Criterion6_TheRegisterDecision_CarriesNoText()
    {
        var report = Assemble(new WorkingContextContributor(
            Trace, ConversationMove.Correction, ResolutionConfidence.Exact,
            referentSourceMessageId: Guid.NewGuid()));

        var serialized = JsonSerializer.Serialize(report.RegisterDecisions);
        // Source, dimension, winner, reason — all tokens; nothing conversational.
        Assert.Contains("working-context-register", serialized);
        Assert.Contains("working-context.move", serialized);
        Assert.DoesNotContain(" ", JsonSerializer.Serialize(
            report.RegisterDecisions.Select(d => new { d.Dimension, d.Value, d.ReasonCode })));
    }

    // ---- case 10: live, through the real native-shadow call site -------------------------

    [Fact]
    public async Task Case10_LiveTurn_CarriesTheWorkingContextDecisionIntoTheNativeRow()
    {
        var recorder = new CollectingRecorder();
        await using var host = new TestHost(
            Now,
            configureServices: s => s.AddSingleton<IShadowRecorder>(recorder),
            settings: new Dictionary<string, string?>
            {
                ["Companion:RendererShadow:Enabled"] = "true",
                ["Companion:RendererShadow:Endpoint"] = "http://127.0.0.1:59996",
                ["Companion:RendererShadow:TimeoutSeconds"] = "5",
            });

        Guid conversationId;
        using (var seed = host.CreateScope())
        {
            conversationId = (await seed.ServiceProvider.GetRequiredService<IConversationStore>()
                .StartConversationAsync(User, "t", "mock", "test")).Id;
        }

        using (var scope = host.CreateScope())
        {
            var companion = scope.ServiceProvider.GetRequiredService<ICompanion>();
            // A turn that reads as a correction — the working-context move that votes.
            await companion.RespondAsync(User, conversationId, "I have a Jetson Orin Nano at home.");
            var trace = await companion.RespondAsync(
                User, conversationId, "no, actually it's a Jetson Nano, not an Orin.");
            Assert.Equal(TurnStatus.Answered, trace.Status);
        }

        var service = (RendererShadowService)host.Services.GetRequiredService<IRendererShadow>();
        await service.DisposeAsync();

        var rows = recorder.Rows
            .Where(r => r.Subject == RendererShadowService.RendererV3Subject)
            .ToList();
        Assert.NotEmpty(rows);

        // At least one turn's native row shows this source's vote reaching adjudication, and
        // no row anywhere carries the interpretation note or any other prose from it.
        //
        // ADJUDICATED, not necessarily won: these are brand-new conversations, so Source 4c
        // votes verbosity=short from FamiliarityStage.New and outranks working-context (§5.4
        // relationship > working-context). The turn's read of the moment losing to the state
        // of the relationship is the contract working, so the assertion is that the vote was
        // weighed — as winner or as recorded loser — rather than that it prevailed.
        var envelopes = rows.Select(r => JsonSerializer.Deserialize<V3ShadowEnvelope>(r.Input!)!).ToList();
        Assert.Contains(envelopes, e => e.Assembly is not null
            && e.Assembly.RegisterDecisions.Any(d =>
                d.WinningSource == "working-context-register"
                || d.Losers.Any(l => l.StartsWith("working-context-register:", StringComparison.Ordinal))));
        Assert.All(envelopes, e => Assert.Empty(e.Assembly?.AuthorityViolations ?? []));
        Assert.All(rows, r => Assert.DoesNotContain("ask to clarify rather than guessing", r.Input!));
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
