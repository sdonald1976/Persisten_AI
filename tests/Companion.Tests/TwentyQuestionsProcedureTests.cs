using Companion.PlanV3;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The Twenty Questions regression family (P5), derived from the anonymized fixture
/// `Fixtures/twenty-questions-regression.json` — the four real failures reproduced as
/// upstream-ledger tests: repeated questions, answer misassociation, state reversal, and
/// abandonment. Each is now prevented UPSTREAM by the procedure ledger; the mouth never
/// reasons over game state. Object identity is not part of the regression and the family
/// is anonymized to a neutral subject.
/// </summary>
public class TwentyQuestionsProcedureTests
{
    private static readonly PlanContributionContext Ctx = new(
        Guid.Parse("cccccccc-1111-2222-3333-444444444444"),
        "answer-question", "No", "usr-synth", "companion-ava", SensitiveTurn: false);

    private static PlanV3.PlanV3 Seed() => new()
    {
        TraceId = Ctx.TraceId,
        Participants =
        [
            new Participant("usr-synth", ParticipantRole.user, "SynthUser"),
            new Participant("companion-ava", ParticipantRole.companion, "Ava"),
        ],
        Act = "answer-question",
        Question = new QuestionPolicyBlock(QuestionPolicy.question_forbidden),
        Items = [],
        Register = PlanV3Codec.Canonicalize(new RegisterVector()),
    };

    /// <summary>The real game's shape at question 12, anonymized: the asked list and the
    /// answer bindings that the original run had already lost by this point.</summary>
    private static ProcedureContributor.ActivityLedger MidGame(
        string? selected = "is it usually kept out of sight",
        IReadOnlyList<string>? asked = null,
        IReadOnlyList<(string, bool)>? answers = null)
        => new(
            "Twenty Questions", QuestionNumber: 12, QuestionBudget: 20,
            AskedQuestions: asked ??
            [
                "does it exist physically", "can it be interacted with directly",
                "is it man-made", "does it serve a practical purpose",
                "is it found indoors", "does it have moving parts",
                "is it small enough for a desk", "is it tied to productivity",
                "does it have notable aesthetic appeal", "is it used in leisure contexts",
                "does it have a distinct texture",
            ],
            Answers: answers ??
            [
                ("does it exist physically", true), ("can it be interacted with directly", true),
                ("is it man-made", true), ("does it serve a practical purpose", true),
                ("is it found indoors", true), ("does it have moving parts", false),
                ("is it small enough for a desk", true), ("is it tied to productivity", false),
                ("does it have notable aesthetic appeal", false), ("is it used in leisure contexts", true),
                ("does it have a distinct texture", true),
            ],
            EstablishedFacts: ["physical", "man-made", "practical", "indoors", "no moving parts",
                               "desk-scale", "distinct texture"],
            Exclusions: ["not productivity-related", "no notable aesthetic appeal"],
            Candidates: ["a household implement", "a personal item"],
            SelectedNextQuestion: selected);

    private static AssemblyReport Assemble(ProcedureContributor.ActivityLedger ledger)
        => PlanV3Assembler.Assemble(Ctx, [new ProcedureContributor(ledger)],
            SourceRegistry.Default, Seed());

    /// <summary>Failure 1: "no moving parts" was asked three times across the real game.</summary>
    [Fact]
    public void RepeatedQuestion_IsRefusedByTheLedger_HoweverItIsPhrased()
    {
        var ledger = MidGame();
        Assert.True(ledger.WouldRepeat("does it have moving parts"));
        Assert.True(ledger.WouldRepeat("DOES IT HAVE MOVING PARTS"));

        // Given a pool containing the thrice-asked question, selection skips it.
        var next = ledger.SelectNext(
            ["does it have moving parts", "is it usually kept out of sight"]);
        Assert.Equal("is it usually kept out of sight", next);
    }

    /// <summary>Failure 2: a "Yes" to texture was later restated as "No". The ledger binds
    /// each answer to its own question, so no later turn can rebind it.</summary>
    [Fact]
    public void AnswerBinding_IsExplicit_SoMisassociationCannotHappen()
    {
        var ledger = MidGame();
        var texture = ledger.Answers.Single(a => a.Question == "does it have a distinct texture");
        Assert.True(texture.Answer);

        // The plan carries neither the answers nor the facts — nothing for the mouth to
        // re-derive incorrectly.
        var report = Assemble(ledger);
        Assert.All(report.Plan.Items, i =>
        {
            Assert.DoesNotContain("texture", i.Text ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("distinct", i.Text ?? "", StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>Failure 3: established facts reversed (practical → "perhaps decorative").
    /// Facts and exclusions live upstream and never enter the plan to be contradicted.</summary>
    [Fact]
    public void EstablishedFacts_StayUpstream_AndCannotDriftInTheReply()
    {
        var ledger = MidGame();
        Assert.Contains("practical", ledger.EstablishedFacts);
        Assert.Contains("no notable aesthetic appeal", ledger.Exclusions);

        var report = Assemble(ledger);
        Assert.All(report.Plan.Items, i =>
        {
            Assert.DoesNotContain("practical", i.Text ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("decorative", i.Text ?? "", StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>Failure 4: the game was abandoned ("what would you like to explore?").
    /// With a selected question the plan is ask_required — the asker role is structural.</summary>
    [Fact]
    public void TheAskerRoleIsStructural_TheGameCannotBeHandedBack()
    {
        var report = Assemble(MidGame());

        Assert.Equal(QuestionPolicy.ask_required, report.Plan.Question.Policy);
        var ask = Assert.Single(report.Plan.Items, i => i.Policy == ExpressionPolicy.ask_required);
        Assert.Equal("is it usually kept out of sight", ask.Text);
        Assert.Contains(report.Plan.Items,
            i => i.Policy == ExpressionPolicy.background_only && i.Text!.Contains("question 12 of 20"));
        Assert.Empty(PlanV3Codec.Validate(report.Plan));
    }

    /// <summary>When the ledger has no next question (budget spent, or the procedure
    /// decided to guess), the plan simply carries no ask — still structural, never a
    /// drift into assistant-mode chatter.</summary>
    [Fact]
    public void NoSelectedQuestion_YieldsNoAsk_WithoutAbandonmentProse()
    {
        var report = Assemble(MidGame(selected: null));

        Assert.Equal(QuestionPolicy.question_forbidden, report.Plan.Question.Policy);
        Assert.DoesNotContain(report.Plan.Items, i => i.Policy == ExpressionPolicy.ask_required);
        Assert.Single(report.Plan.Items);   // the frame only
        Assert.Empty(PlanV3Codec.Validate(report.Plan));
    }
}
