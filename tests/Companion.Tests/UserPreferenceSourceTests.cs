using System.Text.Json;
using Companion.Core;
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
/// Source 3 acceptance evidence (docs/SOURCE3_PREFERENCE_PLAN.md): the 14 declared
/// scenarios and the 12 pass criteria, fixed before implementation ran.
///
/// Live vs constructed, as declared: scenarios 1, 2, 5, 6, 7, 8, 14 run the REAL capture
/// path (Agent.AdjustStyleAsync / MemoryCurator.ForgetAsync) and/or the real shadow call
/// site. 3, 4, 9, 10, 11, 12, 13 construct records or contributors directly (hosting
/// config is configuration, not conversation; expression restrictions have no live
/// capture path yet — recorded as a blocker).
/// </summary>
public class UserPreferenceSourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private const string User = CompanionSeeder.DemoUserId;

    private static readonly PlanContributionContext Ctx = new(
        Guid.Parse("33333333-1111-2222-3333-444444444444"),
        "acknowledge", "a synthetic message", "usr-synth", "companion-ava", SensitiveTurn: false);

    private static PlanV3.PlanV3 Seed() => new()
    {
        TraceId = Ctx.TraceId,
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

    private static UserPreferenceRecord Pref(
        string dimension, string value, bool restrictive = false,
        DateTimeOffset? statedAt = null, string? statement = null,
        UserPreferenceKind kind = UserPreferenceKind.Register, string? subject = null)
        => new()
        {
            Id = Guid.NewGuid(),
            UserId = "usr-synth",
            Kind = kind,
            Dimension = dimension,
            Value = value,
            Subject = subject,
            Restrictive = restrictive,
            StatedAt = statedAt ?? Now,
            EvidenceKind = "direct-instruction",
            EvidenceStatement = statement ?? "a synthetic explicit instruction",
        };

    private const string INSTRUCTION = "from now on, don't swear when we talk";
    private const string INSTRUCTION_CASED = "  From Now On, Don't Swear When We Talk  ";
    private const string INSTRUCTION_SUB = "don't swear";
    private const string INSTRUCTION_SUPER = "from now on, don't swear at my mother about the plumbing";
    private const string INSTRUCTION_PREFIX = "from now on, don't swear when we talk about work";
    private const string INSTRUCTION_FRAGMENT = "don't swear when we talk";
    private const string INSTRUCTION_OPENING = "from now on, don't swear";
    private const string INSTRUCTION_LIVE = "from now on, don't swear";
    private const string SHARED_STATEMENT = "from now on, keep it short and don't swear";

    private static string Instruction => INSTRUCTION;
    private static string SharedStatement => SHARED_STATEMENT;

    private static async Task<Guid> StartConversationAsync(IServiceProvider sp)
        => (await sp.GetRequiredService<IConversationStore>()
            .StartConversationAsync(User, "t", "mock", "test")).Id;

    // ---- scenario 1 (LIVE): don't swear → you can swear again --------------------------

    [Fact]
    public async Task Scenario1_DontSwear_ThenSwearAgain_RevokesRatherThanCompetes()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var conversationId = await StartConversationAsync(sp);
        var agent = sp.GetRequiredService<IAgent>();
        var store = sp.GetRequiredService<IUserPreferenceStore>();

        // "from now on, …" already routes to the style path — no routing was changed.
        var first = await agent.HandleAsync(User, conversationId, "from now on, don't swear");
        Assert.Equal(IntentKind.AdjustStyle, first.Intent);

        var active = await store.GetActiveAsync(User);
        var forbid = Assert.Single(active);
        Assert.Equal("profanity", forbid.Dimension);
        Assert.Equal("forbidden", forbid.Value);
        Assert.True(forbid.Restrictive);
        Assert.Equal("direct-instruction", forbid.EvidenceKind);
        Assert.Contains("swear", forbid.EvidenceStatement);

        host.Clock.Advance(TimeSpan.FromMinutes(5));
        var second = await agent.HandleAsync(User, conversationId, "from now on, you can swear again");
        Assert.Equal(IntentKind.AdjustStyle, second.Intent);

        // Revocation DEACTIVATES; it never creates a competing preference.
        Assert.Empty(await store.GetActiveAsync(User));
        var all = await store.GetAllAsync(User);
        var revoked = Assert.Single(all);
        Assert.Equal(UserPreferenceStatus.Revoked, revoked.Status);
        Assert.NotNull(revoked.RevokedAt);
        Assert.Contains("swear again", revoked.RevocationStatement);

        // And the vote is gone: an empty active set contributes nothing.
        var report = PlanV3Assembler.Assemble(
            Ctx, [new UserPreferenceContributor(await store.GetActiveAsync(User))],
            SourceRegistry.Default, Seed());
        Assert.Empty(report.RegisterDecisions);
        Assert.Equal("neutral", report.Plan.Register.Profanity);
    }

    // ---- scenario 2 (LIVE): be concise → give me more detail ---------------------------

    [Fact]
    public async Task Scenario2_BeConcise_ThenMoreDetail_SupersedesToOneActive()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var conversationId = await StartConversationAsync(sp);
        var agent = sp.GetRequiredService<IAgent>();
        var store = sp.GetRequiredService<IUserPreferenceStore>();

        await agent.HandleAsync(User, conversationId, "be concise");
        host.Clock.Advance(TimeSpan.FromMinutes(5));
        await agent.HandleAsync(User, conversationId, "from now on, give me more detail");

        var active = await store.GetActiveAsync(User);
        var winner = Assert.Single(active);
        Assert.Equal("verbosity", winner.Dimension);
        Assert.Equal("expansive", winner.Value);

        var all = await store.GetAllAsync(User);
        Assert.Equal(2, all.Count);
        var superseded = Assert.Single(all, r => r.Status == UserPreferenceStatus.Superseded);
        Assert.Equal("short", superseded.Value);
        Assert.Equal(winner.Id, superseded.SupersededById);

        // The resolver reports the single winner; nothing is left to fight over.
        var resolution = UserPreferenceResolution.Resolve(active);
        var slot = Assert.Single(resolution.Decisions);
        Assert.Equal("single-active", slot.Reason);
        Assert.Equal(winner.Id, slot.WinnerId);
    }

    // ---- scenario 3 (constructed): hosting vs user on the same dimension ---------------

    [Fact]
    public void Scenario3_HostingVsUser_UserWinsPerFrozenContract_BothRecorded()
    {
        var user = Pref("profanity", "mirror-only");
        var report = PlanV3Assembler.Assemble(
            Ctx,
            [
                new UserPreferenceContributor([user]),
                new HostingConfigContributor(new Dictionary<string, string> { ["profanity"] = "avoid" }),
            ],
            SourceRegistry.Default, Seed());

        // Spec §5.4 ranks user-preference above hosting-config, so the hosting value is a
        // DEFAULT the user can override (§5.5) — not an enforceable deployment restriction.
        // The decision names the winning AUTHORITY and its reason code, and the loser is
        // recorded rather than dropped.
        var decision = Assert.Single(report.RegisterDecisions);
        Assert.Equal("profanity", decision.Dimension);
        Assert.Equal("mirror-only", decision.Value);
        Assert.Equal("user-preference", decision.WinningSource);
        Assert.Equal("user-preference.profanity", decision.ReasonCode);
        Assert.Contains("hosting-config:avoid", decision.Losers);
        Assert.Equal("mirror-only", report.Plan.Register.Profanity);
        Assert.Empty(report.AuthorityViolations);
    }

    // ---- scenario 4 (constructed): hosting alone ---------------------------------------

    [Fact]
    public void Scenario4_HostingDefaultAlone_WinsItsDimension_WithConfigEvidence()
    {
        var report = PlanV3Assembler.Assemble(
            Ctx,
            [new HostingConfigContributor(new Dictionary<string, string> { ["profanity"] = "avoid" })],
            SourceRegistry.Default, Seed());

        var decision = Assert.Single(report.RegisterDecisions);
        Assert.Equal("hosting-config", decision.WinningSource);
        Assert.Equal("avoid", report.Plan.Register.Profanity);
        var restriction = Assert.Single(report.Plan.RegisterRestrictions!);
        Assert.Equal("hosting-config", restriction.Owner);
        Assert.StartsWith("config:HostingPolicy:Register:", restriction.Provenance!.EvidenceRef);
        Assert.Empty(report.AuthorityViolations);
    }

    // ---- scenarios 5+6 (LIVE): no inference, ever --------------------------------------

    [Theory]
    [InlineData("I had the filthiest, most explicit dream about you last night.")]
    [InlineData("ugh. that answer was useless and you're really starting to annoy me.")]
    public async Task Scenarios5And6_SexualContentAndAnnoyance_CreateNothing(string message)
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var conversationId = await StartConversationAsync(sp);

        await sp.GetRequiredService<IAgent>().HandleAsync(User, conversationId, message);

        // The store is byte-empty: no preference, no restriction, nothing inferred from
        // subject matter or sentiment. Only an explicit instruction writes here.
        Assert.Empty(await sp.GetRequiredService<IUserPreferenceStore>().GetAllAsync(User));
    }

    // ---- scenario 7 (LIVE): Ava's tastes never become the user's -----------------------

    [Fact]
    public async Task Scenario7_AvasOwnDislike_NeverBecomesAUserPreference()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;

        // Ava forms a strong dislike through the real taste store.
        await sp.GetRequiredService<IPreferenceStore>().ApplySignalAsync(
            User, "horror movies", targetAffinity: -0.8, "synthetic: they unsettle her",
            embedding: null, Now);

        // Her taste exists; the user's store is untouched, and no contribution claims the
        // user requested anything.
        Assert.NotEmpty(await sp.GetRequiredService<IPreferenceStore>().GetAllAsync(User));
        Assert.Empty(await sp.GetRequiredService<IUserPreferenceStore>().GetAllAsync(User));
    }

    // ---- scenario 8 (LIVE): forgotten evidence invalidates its preference --------------

    [Fact]
    public async Task Scenario8_ForgottenEvidence_InvalidatesThePreference_AndPurgesItsStatement()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var store = sp.GetRequiredService<IUserPreferenceStore>();
        var memories = sp.GetRequiredService<IMemoryStore>();

        // A memory whose evidence excerpt IS the preference's statement text — the case
        // where forgetting the sentence must also take the preference's authority with it.
        const string statement = "from now on, don't swear when we talk";
        var memoryId = Guid.NewGuid();
        await memories.AddSemanticAsync(new SemanticMemory
        {
            Id = memoryId,
            UserId = User,
            Subject = "user",
            Predicate = "asked",
            Value = "Ava not to swear",
            NormalizedFact = "The user asked Ava not to swear.",
            FirstObserved = Now,
            LastConfirmed = Now,
            CreatedAt = Now,
        });
        await memories.AddEvidenceAsync(User,
        [
            new MemoryEvidence
            {
                Id = Guid.NewGuid(),
                UserId = User,
                MemoryId = memoryId,
                MemoryKind = MemoryKind.Semantic,
                MessageId = Guid.NewGuid(),
                Excerpt = statement,
            },
        ]);

        await store.StateAsync(new UserPreferenceRecord
        {
            UserId = User,
            Kind = UserPreferenceKind.Register,
            Dimension = "profanity",
            Value = "forbidden",
            Restrictive = true,
            StatedAt = Now,
            EvidenceKind = "direct-instruction",
            EvidenceStatement = statement,
        });

        // The REAL /forget path.
        Assert.True(await sp.GetRequiredService<IMemoryCurator>()
            .ForgetAsync(User, memoryId, "user asked to forget"));

        Assert.Empty(await store.GetActiveAsync(User));
        var invalidated = Assert.Single(await store.GetAllAsync(User));
        Assert.Equal(UserPreferenceStatus.EvidenceForgotten, invalidated.Status);
        // The statement is PURGED — the forgotten text does not linger here.
        Assert.Null(invalidated.EvidenceStatement);
    // ...and the slot was vacated, so the unique index no longer holds it.
        Assert.Null(invalidated.ActiveSlot);
    }

    // ================= the evidence-linkage repair: adversarial =================

    private static UserPreferenceRecord Stated(string statement, string dimension = "profanity",
        string value = "forbidden", Guid? evidenceEvent = null, Guid? messageId = null)
        => new()
        {
            Id = Guid.NewGuid(),
            UserId = User,
            Kind = UserPreferenceKind.Register,
            Dimension = dimension,
            Value = value,
            Restrictive = true,
            StatedAt = Now,
            EvidenceKind = messageId is null ? "direct-instruction" : "stored-message",
            EvidenceEventId = evidenceEvent ?? Guid.NewGuid(),
            EvidenceMessageId = messageId,
            EvidenceStatement = statement,
        };

    /// <summary>
    /// Requirement 1: overlapping text must never revoke. Each forgotten excerpt here
    /// shares words with the standing instruction - or contains it, or is contained by it -
    /// and the old mutual-substring rule would have killed the preference on every one.
    /// </summary>
    [Theory]
    // The three that the OLD mutual-substring rule actually revoked on (verified): a
    // fragment of the instruction, its opening clause, and a superstring of it. These are
    // the realistic shape - a memory excerpt that is part of the sentence the instruction
    // was stated in - and each one silently destroyed a standing rule.
    [InlineData(INSTRUCTION_FRAGMENT)]
    [InlineData(INSTRUCTION_OPENING)]
    [InlineData(INSTRUCTION_PREFIX)]
    // ...and the short/partial overlaps, which the old length bar happened to spare but
    // which nothing structural was protecting.
    [InlineData(INSTRUCTION_SUB)]
    [InlineData(INSTRUCTION_SUPER)]
    [InlineData("swear")]
    [InlineData("From now on")]
    [InlineData("talk")]
    public async Task Adversarial_OverlappingForgottenText_NeverRevokesAPreference(string forgotten)
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IUserPreferenceStore>();

        await store.StateAsync(Stated(Instruction));

        var result = await store.InvalidateByForgottenEvidenceAsync(User, [forgotten], [], Now);

        Assert.Equal(0, result.Invalidated);
        Assert.Equal(0, result.Ambiguous);
        var survivor = Assert.Single(await store.GetActiveAsync(User));
        Assert.Equal(Instruction, survivor.EvidenceStatement);
    }

    /// <summary>Requirement 2: the exact instruction still invalidates, whitespace and
    /// casing normalized - the repair must not have broken forgetting.</summary>
    [Theory]
    [InlineData(INSTRUCTION)]
    [InlineData(INSTRUCTION_CASED)]
    public async Task Adversarial_TheExactInstruction_StillInvalidatesAndPurges(string forgotten)
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IUserPreferenceStore>();
        await store.StateAsync(Stated(Instruction));

        var result = await store.InvalidateByForgottenEvidenceAsync(User, [forgotten], [], Now);

        Assert.Equal(1, result.Invalidated);
        Assert.Empty(await store.GetActiveAsync(User));
        var dead = Assert.Single(await store.GetAllAsync(User));
        Assert.Equal(UserPreferenceStatus.EvidenceForgotten, dead.Status);
        Assert.Null(dead.EvidenceStatement);
    }

    /// <summary>Requirement 2: the durable evidence EVENT invalidates exactly, with no
    /// text involved at all - the path that exists because the intent turn stores no
    /// Message row.</summary>
    [Fact]
    public async Task Adversarial_TheEvidenceEvent_InvalidatesWithoutAnyTextMatching()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IUserPreferenceStore>();

        var mine = Guid.NewGuid();
        await store.StateAsync(Stated(Instruction, evidenceEvent: mine));
        await store.StateAsync(Stated("be concise", "verbosity", "short"));

        Assert.Equal(1, await store.InvalidateByEvidenceEventAsync(User, mine, Now));

        var survivor = Assert.Single(await store.GetActiveAsync(User));
        Assert.Equal("verbosity", survivor.Dimension);
        // An unrelated event id takes nothing.
        Assert.Equal(0, await store.InvalidateByEvidenceEventAsync(User, Guid.NewGuid(), Now));
        Assert.Single(await store.GetActiveAsync(User));
    }

    /// <summary>
    /// Requirements 3 and 4: two ACTIVE preferences carrying identical statements. A
    /// text-only forget cannot tell them apart, so it must revoke NEITHER and report the
    /// ambiguity - silently picking one would drop a standing rule on a coin flip.
    /// </summary>
    [Fact]
    public async Task Adversarial_IdenticalStatementsFromDifferentEvents_RevokeNothing_AndReportAmbiguity()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IUserPreferenceStore>();

        var first = await store.StateAsync(Stated(SharedStatement));
        var second = await store.StateAsync(Stated(SharedStatement, "verbosity", "short"));
        Assert.NotEqual(first.EvidenceEventId, second.EvidenceEventId);

        var result = await store.InvalidateByForgottenEvidenceAsync(User, [SharedStatement], [], Now);

        Assert.Equal(0, result.Invalidated);
        Assert.Equal(1, result.Ambiguous);
        Assert.Equal(2, (await store.GetActiveAsync(User)).Count);

        // The unambiguous handle still works: forgetting ONE of the two identical
        // instructions by its own event id takes exactly that one.
        Assert.Equal(1, await store.InvalidateByEvidenceEventAsync(User, first.EvidenceEventId, Now));
        var survivor = Assert.Single(await store.GetActiveAsync(User));
        Assert.Equal(second.Id, survivor.Id);
        Assert.Equal(SharedStatement, survivor.EvidenceStatement);
    }

    /// <summary>Requirement 3: exact id linkage is unaffected by ambiguous text nearby.</summary>
    [Fact]
    public async Task Adversarial_MessageIdLinkage_IsExact_EvenWhenStatementsCollide()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IUserPreferenceStore>();

        var target = Guid.NewGuid();
        var kept = await store.StateAsync(Stated(SharedStatement, "verbosity", "short"));
        await store.StateAsync(Stated(SharedStatement, messageId: target));

        var result = await store.InvalidateByForgottenEvidenceAsync(User, [SharedStatement], [target], Now);

        // The id-linked one dies; the text-identical one survives, because the text route
        // was ambiguous and ambiguity revokes nothing.
        Assert.Equal(1, result.Invalidated);
        Assert.Equal(1, result.Ambiguous);
        var survivor = Assert.Single(await store.GetActiveAsync(User));
        Assert.Equal(kept.Id, survivor.Id);
    }

    /// <summary>
    /// Requirement 5: the one-active invariant is a DATABASE constraint, not an intention.
    /// Two writers racing the same slot cannot both end up active - the loser gets a
    /// constraint violation rather than a silently duplicated preference.
    /// </summary>
    [Fact]
    public async Task Requirement5_ConcurrentWritesToOneSlot_CannotBothBeActive()
    {
        await using var host = new TestHost(Now);

        // Two independent scopes = two DbContexts = a real race, not a same-context write.
        using var a = host.CreateScope();
        using var b = host.CreateScope();
        var storeA = a.ServiceProvider.GetRequiredService<IUserPreferenceStore>();

        await storeA.StateAsync(Stated(Instruction));

        // B never observed A's row and tries to claim the same slot directly.
        var collision = new UserPreferenceRecord
        {
            Id = Guid.NewGuid(),
            UserId = User,
            Kind = UserPreferenceKind.Register,
            Dimension = "profanity",
            Value = "mirror-only",
            StatedAt = Now,
            EvidenceEventId = Guid.NewGuid(),
            EvidenceStatement = "mirror my profanity",
            ActiveSlot = UserPreferenceRecord.SlotKey(
                UserPreferenceKind.Register, "global", "profanity", null),
        };
        var db = b.ServiceProvider.GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();
        db.UserPreferences.Add(collision);

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());

        // Exactly one active record survives the race.
        using var verify = host.CreateScope();
        var active = Assert.Single(
            await verify.ServiceProvider.GetRequiredService<IUserPreferenceStore>().GetActiveAsync(User));
        Assert.Equal("forbidden", active.Value);
    }

    /// <summary>Requirement 5, the ordinary path: supersession vacates the slot, so the
    /// replacement can claim it under the same unique index.</summary>
    [Fact]
    public async Task Requirement5_Supersession_VacatesTheSlot_SoTheReplacementCanClaimIt()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IUserPreferenceStore>();

        await store.StateAsync(Stated("be concise", "verbosity", "short"));
        var replacement = await store.StateAsync(Stated("give me more detail", "verbosity", "expansive"));

        var all = await store.GetAllAsync(User);
        Assert.Equal(2, all.Count);
        var superseded = Assert.Single(all, r => r.Status == UserPreferenceStatus.Superseded);
        Assert.Null(superseded.ActiveSlot);
        var active = Assert.Single(all, r => r.Status == UserPreferenceStatus.Active);
        Assert.Equal(replacement.Id, active.Id);
        Assert.Equal(
            UserPreferenceRecord.SlotKey(UserPreferenceKind.Register, "global", "verbosity", null),
            active.ActiveSlot);
    }

    /// <summary>Live capture mints a durable evidence event, even with no Message row.</summary>
    [Fact]
    public async Task LiveCapture_MintsADurableEvidenceEvent_WithNoMessageRow()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var conversationId = await StartConversationAsync(sp);

        await sp.GetRequiredService<IAgent>().HandleAsync(User, conversationId, INSTRUCTION_LIVE);

        var record = Assert.Single(await sp.GetRequiredService<IUserPreferenceStore>().GetActiveAsync(User));
        Assert.NotEqual(Guid.Empty, record.EvidenceEventId);
        Assert.Null(record.EvidenceMessageId);          // the intent path stores no Message
        Assert.Equal("direct-instruction", record.EvidenceKind);
        Assert.NotNull(record.ActiveSlot);
    }

    // ---- scenario 9 (constructed): no evidence, no restrictive authority ---------------

    [Fact]
    public void Scenario9_RestrictiveVoteWithoutEvidence_IsARecordedViolation()
    {
        var report = PlanV3Assembler.Assemble(
            Ctx, [new EvidencelessContributor()], SourceRegistry.Default, Seed());

        Assert.Empty(report.RegisterDecisions);
        Assert.Equal("neutral", report.Plan.Register.Profanity);
        Assert.Contains(report.AuthorityViolations,
            v => v.Contains("restrictive register value without evidence reference"));
    }

    private sealed class EvidencelessContributor : IPlanV3Contributor
    {
        public string SourceId => "user-preference";
        public PlanContributionResult Contribute(PlanContributionContext c) => new(
            [],
            [new RegisterProposal("profanity", "forbidden", "user-preference.profanity",
                new Provenance(Origin: "told-by-user"), Restrictive: true)]);
    }

    // ---- scenario 10 (constructed): persona cannot restrict ----------------------------

    [Fact]
    public void Scenario10_PersonaSource_CannotProposeARestriction()
    {
        var report = PlanV3Assembler.Assemble(
            Ctx, [new PersonaRestrictionAttempt()], SourceRegistry.Default, Seed());

        Assert.Empty(report.RegisterDecisions);
        Assert.Contains(report.AuthorityViolations,
            v => v.Contains("persona: restrictive register value without restriction authority"));
    }

    private sealed class PersonaRestrictionAttempt : IPlanV3Contributor
    {
        public string SourceId => "persona";
        public PlanContributionResult Contribute(PlanContributionContext c) => new(
            [],
            [new RegisterProposal("profanity", "forbidden", "persona.style",
                new Provenance(Origin: "derived"), Restrictive: true)]);
    }

    // ---- scenarios 11+12 (constructed): expression restrictions travel as notes --------

    [Fact]
    public void Scenario11_ExpressionRestriction_BecomesAMustNotExpressNote_WithEvidence()
    {
        var restriction = Pref("expression", "withhold", restrictive: true,
            kind: UserPreferenceKind.ExpressionRestriction,
            subject: "the synthetic surprise party");
        var report = PlanV3Assembler.Assemble(
            Ctx, [new UserPreferenceContributor([restriction])], SourceRegistry.Default, Seed());

        var note = Assert.Single(report.Plan.Items);
        Assert.Equal(ExpressionPolicy.must_not_express, note.Policy);
        Assert.Equal("user-preference.expression-restriction.stated", note.ReasonCode);
        Assert.Equal("user-preference", note.Source);
        Assert.Contains("the synthetic surprise party", note.Text);
        Assert.Equal(restriction.Id.ToString(), note.Provenance!.EvidenceRef);
        // A restriction names its subject; it never quotes the user's statement.
        Assert.DoesNotContain("synthetic explicit instruction", note.Text);
        Assert.Empty(report.AuthorityViolations);
    }

    [Fact]
    public void Scenario12_ExpressionRestrictionWithoutEvidence_IsRejected()
    {
        var report = PlanV3Assembler.Assemble(
            Ctx, [new EvidencelessRestrictionAttempt()], SourceRegistry.Default, Seed());

        Assert.Empty(report.Plan.Items);
        var outcome = Assert.Single(report.Outcomes);
        Assert.Equal("rejected", outcome.Decision);
        Assert.Equal("grant-requires-evidence", outcome.Reason);
    }

    private sealed class EvidencelessRestrictionAttempt : IPlanV3Contributor
    {
        public string SourceId => "user-preference";
        public PlanContributionResult Contribute(PlanContributionContext c) => new(
        [
            new ProposedItem
            {
                LocalId = "r1", Type = "expression-restriction", Category = RenderCategory.note,
                ProposedPolicy = ExpressionPolicy.must_not_express,
                ReasonCode = "user-preference.expression-restriction.stated",
                Text = "Do not raise the synthetic subject.",
                Provenance = new Provenance(Origin: "told-by-user"),
            },
        ]);
    }

    // ---- scenario 13 (constructed): store failure costs only itself --------------------

    [Fact]
    public void Scenario13_ContributorFailure_IsContentSafe_AndOthersSurvive()
    {
        var report = PlanV3Assembler.Assemble(
            Ctx,
            [
                new ThrowingPreferenceContributor(),
                new HostingConfigContributor(new Dictionary<string, string> { ["verbosity"] = "short" }),
            ],
            SourceRegistry.Default, Seed());

        var failure = Assert.Single(report.ContributorFailures);
        Assert.Equal("user-preference: InvalidOperationException", failure);
        Assert.DoesNotContain("secret-statement-text", JsonSerializer.Serialize(report));
        // The other authority still voted.
        Assert.Single(report.RegisterDecisions);
    }

    private sealed class ThrowingPreferenceContributor : IPlanV3Contributor
    {
        public string SourceId => "user-preference";
        public PlanContributionResult Contribute(PlanContributionContext c)
            => throw new InvalidOperationException("store failed: secret-statement-text");
    }

    // ---- scenario 14 (LIVE): end to end through the real call site ---------------------

    [Fact]
    public async Task Scenario14_LiveTurn_CarriesThePreferenceDecisionIntoTheNativeRow()
    {
        var recorder = new CollectingRecorder();
        await using var host = new TestHost(
            Now,
            configureServices: s => s.AddSingleton<IShadowRecorder>(recorder),
            settings: new Dictionary<string, string?>
            {
                ["Companion:RendererShadow:Enabled"] = "true",
                ["Companion:RendererShadow:Endpoint"] = "http://127.0.0.1:59997",
                ["Companion:RendererShadow:TimeoutSeconds"] = "5",
            });

        Guid conversationId;
        using (var seed = host.CreateScope())
        {
            var sp = seed.ServiceProvider;
            conversationId = await StartConversationAsync(sp);
            // The preference arrives through the REAL capture path.
            await sp.GetRequiredService<IAgent>().HandleAsync(User, conversationId, "from now on, don't swear");
        }

        TurnTrace trace;
        using (var scope = host.CreateScope())
        {
            trace = await scope.ServiceProvider.GetRequiredService<ICompanion>()
                .RespondAsync(User, conversationId, "How was your day?");
        }
        Assert.Equal(TurnStatus.Answered, trace.Status);
        Assert.False(string.IsNullOrWhiteSpace(trace.Response));

        var service = (RendererShadowService)host.Services.GetRequiredService<IRendererShadow>();
        await service.DisposeAsync();

        var row = Assert.Single(recorder.Rows, r => r.Subject == RendererShadowService.RendererV3Subject);
        var env = JsonSerializer.Deserialize<V3ShadowEnvelope>(row.Input!)!;

        // The native register carries the preference; the decision names the authority.
        Assert.NotNull(env.Native);
        Assert.Contains("profanity=forbidden", env.Native!.RegisterLine);
        Assert.NotNull(env.Assembly);
        var decision = Assert.Single(env.Assembly!.RegisterDecisions, d => d.Dimension == "profanity");
        Assert.Equal("user-preference", decision.WinningSource);
        Assert.Equal("user-preference.profanity", decision.ReasonCode);
        Assert.Empty(env.Assembly.AuthorityViolations);

        // And the user's statement text is NOWHERE in the persisted row.
        Assert.DoesNotContain("don't swear", row.Input!, StringComparison.OrdinalIgnoreCase);

        // Production stayed production: messages stored, reply delivered.
        using var verify = host.CreateScope();
        var messages = await verify.ServiceProvider.GetRequiredService<IConversationStore>()
            .GetRecentMessagesAsync(conversationId, User, 10);
        Assert.Contains(messages, m => m.Role == MessageRole.Assistant);
    }

    // ---- criteria that span the scenarios ----------------------------------------------

    /// <summary>Criterion 3: the resolver is pure and its report carries no text.</summary>
    [Fact]
    public void Criterion3_Resolution_IsDeterministic_AndItsReportContainsNoPreferenceText()
    {
        const string statement = "a private synthetic statement about swearing";
        var older = Pref("profanity", "forbidden", restrictive: true,
            statedAt: Now, statement: statement);
        var newer = Pref("profanity", "mirror-only",
            statedAt: Now.AddMinutes(10), statement: statement);
        var restriction = Pref("expression", "withhold", restrictive: true,
            kind: UserPreferenceKind.ExpressionRestriction,
            subject: "a private synthetic subject", statement: statement);

        var records = new List<UserPreferenceRecord> { older, newer, restriction };
        var a = UserPreferenceResolution.Resolve(records);
        var b = UserPreferenceResolution.Resolve([restriction, older, newer]);

        // Deterministic regardless of input order.
        Assert.Equal(JsonSerializer.Serialize(a), JsonSerializer.Serialize(b));

        // The anomalous two-active slot resolves to the newest and says so.
        var profanity = Assert.Single(a.Register);
        Assert.Equal(newer.Id, profanity.WinnerId);
        Assert.Equal("newest-statement", profanity.Reason);
        Assert.Equal([older.Id], profanity.LoserIds);

        // Winner, losers, supersession, scope, authority — and no preference text.
        var serialized = JsonSerializer.Serialize(a);
        Assert.DoesNotContain("private synthetic", serialized);
        Assert.DoesNotContain(statement, serialized);
    }

    /// <summary>Criterion 5: two mechanisms — votes never carry a subject, notes never a dimension value.</summary>
    [Fact]
    public void Criterion5_RegisterAndRestriction_TravelDifferentMechanisms()
    {
        var register = Pref("verbosity", "short");
        var restriction = Pref("expression", "withhold", restrictive: true,
            kind: UserPreferenceKind.ExpressionRestriction, subject: "the synthetic topic");
        var report = PlanV3Assembler.Assemble(
            Ctx, [new UserPreferenceContributor([register, restriction])],
            SourceRegistry.Default, Seed());

        // The register preference is a VOTE (no item); the restriction is an ITEM (no vote).
        Assert.Single(report.RegisterDecisions);
        Assert.Equal("short", report.Plan.Register.Verbosity);
        var note = Assert.Single(report.Plan.Items);
        Assert.Equal("expression-restriction", note.Type);
        Assert.DoesNotContain(report.RegisterDecisions, d => d.Dimension == "expression");
    }

    /// <summary>Criterion 7: register decisions and shadow rows carry ids, never statements.</summary>
    [Fact]
    public void Criterion7_NoStatementText_InDecisionsOrEnvelope()
    {
        const string statement = "an extremely private synthetic instruction";
        var pref = Pref("warmth", "warm", statement: statement);
        var report = PlanV3Assembler.Assemble(
            Ctx, [new UserPreferenceContributor([pref])], SourceRegistry.Default, Seed());

        Assert.DoesNotContain(statement, JsonSerializer.Serialize(report.RegisterDecisions));

        var v2 = new ResponsePlan
        {
            TraceId = Ctx.TraceId,
            Act = TurnIntent.Acknowledge,
            Content = [],
            Epistemic = [],
            Tone = new ToneGuidance("short and casual", null, null),
        };
        var trust = new RendererTrustContext(RendererTransport.local_loopback);
        var env = V3ShadowEnvelopeBuilder.Build(v2, V2Translation.FromV2(v2), null, 1, ["usr-synth"], trust);
        env = V3ShadowEnvelopeBuilder.WithNative(env, V2Translation.FromV2(v2), report.Plan,
            null, report.LintRejections, null, 1, ["usr-synth"], trust);
        env = V3ShadowEnvelopeBuilder.WithAssembly(env, report);
        Assert.DoesNotContain(statement, JsonSerializer.Serialize(env));
    }

    /// <summary>Criterion 1 corollary: interpreting "you can swear again" is a revocation,
    /// never a new profanity preference — asserted at the interpreter, where the risk is.</summary>
    [Theory]
    [InlineData("you can swear again", PreferenceCommands.CommandAction.Revoke)]
    [InlineData("it's okay to swear now", PreferenceCommands.CommandAction.Revoke)]
    [InlineData("don't swear", PreferenceCommands.CommandAction.Set)]
    [InlineData("no more cursing", PreferenceCommands.CommandAction.Set)]
    public void TheInterpreter_ReadsRevocationsAsRevocations(
        string text, PreferenceCommands.CommandAction expected)
    {
        var command = PreferenceCommands.Interpret(text);
        Assert.NotNull(command);
        Assert.Equal(expected, command!.Action);
        Assert.Equal("profanity", command.Dimension);
    }

    /// <summary>Amendment 5: ambiguous language produces no durable preference.</summary>
    [Theory]
    [InlineData("be nicer about my cooking")]
    [InlineData("talk like a pirate")]
    [InlineData("from now on, remember my sister's birthday")]
    [InlineData("that swearing earlier was funny")]
    public void TheInterpreter_DeclinesAmbiguity(string text)
        => Assert.Null(PreferenceCommands.Interpret(text));

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
            => Task.FromResult(Rows.RemoveAll(r => excerpts.Any(e =>
                (r.Input ?? "").Contains(e, StringComparison.OrdinalIgnoreCase))));
    }
}
