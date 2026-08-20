using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Phase 5 shadow slice (docs/RESPONSE_PLAN.md): the typed boundary between what Ava
/// decided and how the model says it. The live specimens are the permanent regressions —
/// Mad Hatter (error ownership), the rabbit hole (invented shared history), the quokka
/// (epistemic fidelity). Stage 1 is shadow-only: the plan is computed and recorded beside
/// every turn while the generation packet stays byte-identical.
/// </summary>
public class ResponsePlanTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private const string UserId = "plan-user";

    private static Message A(string content) => new() { Role = MessageRole.Assistant, Content = content };
    private static Message U(string content) => new() { Role = MessageRole.User, Content = content };

    private static ResponsePlan Plan(
        Message[] recent, string message,
        IReadOnlyList<RetrievalResult>? retrieved = null,
        ConceptLookupResult? knowledge = null)
    {
        var working = WorkingContext.Read(recent, message);
        var intent = TurnIntentClassifier.Classify(working, message, retrieved?.Count ?? 0);
        return ResponsePlanner.Build(Guid.NewGuid(), intent, working, message,
            retrieved ?? Array.Empty<RetrievalResult>(), knowledge,
            curiosityQuestion: null, registerNote: null, moodNote: null, personaStyle: null);
    }

    private static RetrievalResult Hit(IMemory memory) => new()
    {
        Memory = memory, Score = 1.0, Signals = new Dictionary<string, double>(), Reason = "test",
    };

    // ---- SPECIMEN: Mad Hatter — error ownership ----

    [Fact]
    public void CANONICAL_ACorrectionOfHerClaim_AssignsTheErrorToCompanion()
    {
        // She misattributed the quote; the user corrects her. The plan must preserve that
        // AVA was wrong and the USER corrected her — not "we".
        var recent = new[]
        {
            U("Who said 'We're all mad here'?"),
            A("That's the Mad Hatter's famous line from Alice in Wonderland!"),
        };

        var plan = Plan(recent, "No, it was actually the Cheshire Cat.");

        var ack = Assert.Single(plan.Acknowledgments, a => a.Kind == AckKind.CorrectionAccepted);
        Assert.Equal(ErrorOwner.Companion, ack.ErrorOwner);
        Assert.Equal(TurnIntent.AcceptCorrection, plan.Act);
    }

    [Fact]
    public void CANONICAL_TheMadHatterInversion_IsAgreement_NotCorrection()
    {
        // She was RIGHT; the correction-shaped reply agrees with her. No error exists,
        // ErrorOwner never becomes Companion, and contrition would be invented — the live
        // specimen where the model apologized for a mix-up that never happened.
        var recent = new[]
        {
            U("Who said 'We're all mad here'?"),
            A("That's the Cheshire Cat's famous line from Alice in Wonderland!"),
        };

        var plan = Plan(recent, "No, it was actually the Cheshire Cat.");

        Assert.DoesNotContain(plan.Acknowledgments, a => a.Kind == AckKind.CorrectionAccepted);
        var ack = Assert.Single(plan.Acknowledgments, a => a.Kind == AckKind.AgreementConfirmed);
        Assert.Equal(ErrorOwner.Nobody, ack.ErrorOwner);
        Assert.Equal(TurnIntent.Acknowledge, plan.Act);

        // The mirror tripwire: apology after agreement is invented contrition.
        Assert.NotNull(PlanFidelity.CheckInventedContrition(plan,
            "Ah, you're absolutely right — I owe an apology for that mix-up!"));
        Assert.Null(PlanFidelity.CheckInventedContrition(plan,
            "Exactly — glad we're on the same page about that one!"));
    }

    [Fact]
    public void AGenuineConflict_WithGeneralContent_IsStillACorrection()
    {
        // Generalized, not phrase-specific: the asserted value ("acrylic") is absent from
        // her claim ("glass"), so the conflict is real and the error is hers.
        var recent = new[]
        {
            U("The baubles keep falling."),
            A("Glass baubles can be so fragile — maybe wrap them?"),
        };

        var plan = Plan(recent, "No, actually they're acrylic, not glass.");

        var ack = Assert.Single(plan.Acknowledgments, a => a.Kind == AckKind.CorrectionAccepted);
        Assert.Equal(ErrorOwner.Companion, ack.ErrorOwner);
    }

    [Fact]
    public void ASelfCorrection_AssignsTheErrorToTheUser()
    {
        var recent = new[] { U("Plant the oak by the gate."), A("Oak by the gate — noted.") };

        var plan = Plan(recent, "Actually, I meant the maple, not the oak.");

        var ack = Assert.Single(plan.Acknowledgments, a => a.Kind == AckKind.CorrectionAccepted);
        Assert.Equal(ErrorOwner.User, ack.ErrorOwner);
    }

    [Fact]
    public void WeBothSlippedUp_IsAFidelityViolation_AfterACompanionOwnedCorrection()
    {
        var recent = new[]
        {
            U("Who said 'We're all mad here'?"),
            A("That's the Mad Hatter's famous line!"),
        };
        var plan = Plan(recent, "No, it was actually the Cheshire Cat.");

        Assert.NotNull(PlanFidelity.CheckCorrectionOwnership(plan,
            "Ha, you're right — we both slipped up on that one! It was the Cheshire Cat."));
        Assert.Null(PlanFidelity.CheckCorrectionOwnership(plan,
            "You're right, I mixed that up — it was the Cheshire Cat. Thanks for the correction!"));
    }

    [Fact]
    public void ErrorSharing_IsFine_WhenTheUserOwnedTheError()
    {
        var recent = new[] { U("Plant the oak by the gate."), A("Noted.") };
        var plan = Plan(recent, "Actually, I meant the maple.");

        Assert.Null(PlanFidelity.CheckCorrectionOwnership(plan, "No worries — we both mix these up!"));
    }

    // ---- SPECIMEN: the rabbit hole — invented shared history ----

    [Fact]
    public void ASharedHistoryClaim_WithoutSharedMemory_IsAViolation()
    {
        var plan = Plan(new[] { U("I love the rabbit hole scene in Alice in Wonderland.") },
            "It's such a great story.");

        Assert.NotNull(PlanFidelity.CheckSharedHistoryClaim(plan,
            "Remember when we went down the rabbit hole together? That was such an adventure!"));
        Assert.Null(PlanFidelity.CheckSharedHistoryClaim(plan,
            "We could go down that rabbit hole together sometime — metaphorically speaking!"));
    }

    [Fact]
    public void ASharedHistoryClaim_BackedByASharedMemory_IsSupported()
    {
        var shared = new EpisodicMemory
        {
            Id = Guid.NewGuid(), UserId = UserId, Description = "Watched Alice in Wonderland together",
            Owner = MemoryOwner.Shared, Status = MemoryStatus.Active,
            EventTime = Now, MentionedAt = Now, CreatedAt = Now,
        };
        var plan = Plan(new[] { U("I love that movie.") }, "It really is lovely.",
            retrieved: new[] { Hit(shared) });

        Assert.Contains(plan.Content, c => c.Kind == ContentKind.SharedMemory);
        Assert.Null(PlanFidelity.CheckSharedHistoryClaim(plan,
            "Remember when we watched it together? I loved that."));
    }

    // ---- SPECIMEN: the quokka — epistemic fidelity ----

    [Fact]
    public void AnUnknownConcept_CarriesANotLearnedNote_AndExplainingItIsAViolation()
    {
        var plan = Plan(new[] { A("Morning!") }, "Do you know what a quokka is?",
            knowledge: new ConceptLookupResult(ConceptFamiliarity.Unknown, "quokka"));

        Assert.Contains(plan.Epistemic, e => e is { Kind: EpistemicKind.NotLearned, Subject: "quokka" });
        Assert.NotNull(PlanFidelity.CheckEpistemic(plan,
            "A quokka is a small wallaby from Western Australia — adorable little things!"));
        Assert.Null(PlanFidelity.CheckEpistemic(plan,
            "I haven't learned what a quokka is yet — want to tell me?"));
    }

    [Fact]
    public void AKnownConcept_BecomesMustStateContent()
    {
        var plan = Plan(new[] { A("Morning!") }, "Do you know what an axe is?",
            knowledge: new ConceptLookupResult(ConceptFamiliarity.Known, "axe",
                "An axe is a tool used for chopping wood.", Now, KnowledgeOrigin.Taught));

        Assert.Contains(plan.Content, c =>
            c is { Kind: ContentKind.LearnedKnowledge, Requirement: ContentRequirement.MustState });
    }

    // ---- authority levels and the act ----

    [Fact]
    public void DisputedAndSuperseded_AreMustNotContradict()
    {
        var disputed = new SemanticMemory
        {
            Id = Guid.NewGuid(), UserId = UserId, Subject = "user", Predicate = "fact",
            Value = "x", NormalizedFact = "The user lives in Cambridge.",
            Status = MemoryStatus.Disputed, CreatedAt = Now, FirstObserved = Now, LastConfirmed = Now,
        };
        var plan = Plan(new[] { A("Morning!") }, "Where do I live?", retrieved: new[] { Hit(disputed) });

        Assert.Contains(plan.Content, c => c.Requirement == ContentRequirement.MustNotContradict);
    }

    [Fact]
    public void AClarifyAct_CarriesAMandatoryQuestion()
    {
        var recent = new[] { U("My sisters Beth and Clara are both visiting this weekend.") };
        var plan = Plan(recent, "What should I cook for her?");

        Assert.Equal(TurnIntent.Clarify, plan.Act);
        Assert.NotNull(plan.Question);
        Assert.True(plan.Question!.Mandatory);
        Assert.Equal(QuestionKind.Clarify, plan.Question.Kind);
    }

    [Fact]
    public void ATeachingTurn_OwesAFactTaughtAcknowledgment()
    {
        var plan = Plan(new[] { A("Morning!") },
            "An axe is a tool used for chopping or splitting wood.");

        Assert.Contains(plan.Acknowledgments, a => a.Kind == AckKind.FactTaught);
    }

    // ---- the narrow promotion: correction acknowledgments only, conflict-verified ----

    private static async Task<(TestHost host, Guid conv)> SeededCorrectionSessionAsync(
        bool promote, string assistantClaim)
    {
        var host = new TestHost(Now, configureOptions: o => o.PromoteResponsePlan = promote);
        using var scope = host.CreateScope();
        var conversations = scope.ServiceProvider.GetRequiredService<IConversationStore>();
        var conv = (await conversations.StartConversationAsync(UserId, "t", "mock", "test")).Id;
        await conversations.AddMessageAsync(new Message
        {
            Id = Guid.NewGuid(), ConversationId = conv, UserId = UserId,
            Role = MessageRole.User, Content = "Who said 'We're all mad here'?", Timestamp = Now.AddMinutes(-2),
        });
        await conversations.AddMessageAsync(new Message
        {
            Id = Guid.NewGuid(), ConversationId = conv, UserId = UserId,
            Role = MessageRole.Assistant, Content = assistantClaim, Timestamp = Now.AddMinutes(-1),
        });
        return (host, conv);
    }

    [Fact]
    public async Task PromotedPlan_InjectsTheOwnedCorrectionLine_ForAGenuineCorrection()
    {
        var (host, conv) = await SeededCorrectionSessionAsync(promote: true,
            "That's the Mad Hatter's famous line!");
        await using var _ = host;
        using var scope = host.CreateScope();

        var trace = await scope.ServiceProvider.GetRequiredService<ICompanion>()
            .RespondAsync(UserId, conv, "No, it was actually the Cheshire Cat.");

        Assert.Contains("You made the error here", trace.Packet.Render());
        var turn = host.Services.GetRequiredService<ITurnTraceLog>().Recent(UserId, 1).Single();
        Assert.Equal("correction-owned-injected",
            turn.Decisions.Single(d => d.Stage == "plan.promotion").Verdict);
    }

    [Fact]
    public async Task PromotedPlan_InjectsNothing_ForTheAgreementInversion()
    {
        var (host, conv) = await SeededCorrectionSessionAsync(promote: true,
            "That's the Cheshire Cat's famous line!");
        await using var _ = host;
        using var scope = host.CreateScope();

        var trace = await scope.ServiceProvider.GetRequiredService<ICompanion>()
            .RespondAsync(UserId, conv, "No, it was actually the Cheshire Cat.");

        Assert.DoesNotContain("You made the error here", trace.Packet.Render());
        // The agreement reading reaches the packet instead — no contrition instruction.
        Assert.Contains("confirmation, not a correction", trace.Packet.Render());
        var turn = host.Services.GetRequiredService<ITurnTraceLog>().Recent(UserId, 1).Single();
        Assert.DoesNotContain(turn.Decisions, d => d.Stage == "plan.promotion");
    }

    [Fact]
    public async Task WithTheFlagOff_AGenuineCorrection_GetsNoOwnedLine()
    {
        var (host, conv) = await SeededCorrectionSessionAsync(promote: false,
            "That's the Mad Hatter's famous line!");
        await using var _ = host;
        using var scope = host.CreateScope();

        var trace = await scope.ServiceProvider.GetRequiredService<ICompanion>()
            .RespondAsync(UserId, conv, "No, it was actually the Cheshire Cat.");

        Assert.DoesNotContain("You made the error here", trace.Packet.Render());
    }

    // ---- shadow discipline: recorded everywhere, packet byte-identical ----

    [Fact]
    public async Task ThePlan_IsRecorded_AndThePacketCarriesNoTraceOfIt()
    {
        await using var host = new TestHost(Now);
        Guid conv;
        using (var scope = host.CreateScope())
            conv = (await scope.ServiceProvider.GetRequiredService<IConversationStore>()
                .StartConversationAsync(UserId, "t", "mock", "test")).Id;

        TurnTrace trace;
        using (var scope = host.CreateScope())
        {
            trace = await scope.ServiceProvider.GetRequiredService<ICompanion>()
                .RespondAsync(UserId, conv, "My dog is called Precious.");
        }

        var turn = host.Services.GetRequiredService<ITurnTraceLog>().Recent(UserId, 1).Single();
        Assert.NotNull(turn.Plan);
        Assert.Equal(turn.Intent!.Intent, turn.Plan!.Act);
        Assert.Contains(turn.Decisions, d => d.Stage == "plan");

        // Shadow means shadow: no plan vocabulary reaches the model.
        var rendered = trace.Packet.Render();
        Assert.DoesNotContain("must-state", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ResponsePlan", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("acknowledgment", rendered, StringComparison.OrdinalIgnoreCase);

        // And the durable record carries the serialized contract.
        var record = (await host.Services.GetRequiredService<IDiagnosticsStore>()
            .GetRecentTurnsAsync(UserId, 1)).Single();
        Assert.NotNull(record.Plan);
        Assert.Contains("\"act\":", record.Plan);
    }
}
