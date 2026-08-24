using Companion.PlanV3;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// P5b source 1: the real Twenty Questions activity lifecycle. Explicit activation,
/// stable question identities, bound answers, diagnosed selection failure — and the
/// four original failures prevented upstream. Synthetic throughout.
/// </summary>
public class ActivityLifecycleTests
{
    private static readonly PlanContributionContext Ctx = new(
        Guid.Parse("aaaaaaaa-5555-6666-7777-888888888888"),
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

    private static ActivityInstance Game(
        ActivityLifecycle lifecycle = ActivityLifecycle.Active,
        int number = 12, int limit = 20) => new()
    {
        InstanceId = "tq-synth-01",
        ProcedureType = "twenty-questions",
        ProcedureVersion = 1,
        Lifecycle = lifecycle,
        AskerParticipantId = "companion-ava",
        AnswererParticipantId = "usr-synth",
        QuestionLimit = limit,
        CurrentQuestionNumber = number,
        AskedQuestions =
        [
            new AskedQuestion("moving-parts", "does it have moving parts"),
            new AskedQuestion("indoors", "is it found indoors"),
            new AskedQuestion("texture", "does it have a distinct texture"),
        ],
        Answers =
        [
            new AnswerBinding("moving-parts", false),
            new AnswerBinding("indoors", true),
            new AnswerBinding("texture", true),
        ],
        EstablishedFacts = ["practical", "desk-scale"],
        Exclusions = ["aesthetic-appeal"],
        Candidates = ["a household implement", "a personal item"],
    };

    private static AssemblyReport Assemble(ActivityInstance? instance)
        => PlanV3Assembler.Assemble(Ctx, [new ActivityProcedureContributor(instance)],
            SourceRegistry.Default, Seed());

    [Fact]
    public void Activation_IsExplicit_AProposedGameContributesNothing()
    {
        Assert.Empty(Assemble(Game(ActivityLifecycle.Proposed)).Plan.Items);
        Assert.Empty(Assemble(Game(ActivityLifecycle.Completed)).Plan.Items);
        Assert.Empty(Assemble(Game(ActivityLifecycle.Abandoned)).Plan.Items);
        Assert.Empty(Assemble(null).Plan.Items);
    }

    [Fact]
    public void SelectionSkipsAskedAndSettledQuestions_ByStableKey()
    {
        var pool = new[]
        {
            new AskedQuestion("moving-parts", "does it have any moving parts at all"), // rephrased
            new AskedQuestion("practical", "is it practical"),                          // settled fact
            new AskedQuestion("aesthetic-appeal", "is it decorative"),                  // exclusion
            new AskedQuestion("metal", "is it made mostly of metal"),                   // fresh
        };
        var selected = Game().SelectNext(pool);

        Assert.Equal("metal", selected.SelectedNextQuestion!.Key);
        Assert.Null(selected.SelectionFailureReason);
    }

    [Fact]
    public void SelectionFailure_IsDiagnosed_NotSilentlyOrdinary()
    {
        var exhausted = Game().SelectNext([new AskedQuestion("moving-parts", "again?")]);
        Assert.Null(exhausted.SelectedNextQuestion);
        Assert.Equal("no-valid-question-available", exhausted.SelectionFailureReason);

        // The contributor reports the failure; it does NOT quietly contribute a normal turn.
        var report = Assemble(exhausted);
        Assert.Empty(report.Plan.Items);
        Assert.Contains(report.ContributorFailures, f => f.Contains("procedure-selection-failed"));

        var overLimit = (Game(number: 21) with { }).SelectNext([new AskedQuestion("metal", "metal?")]);
        Assert.Equal("question-limit-reached", overLimit.SelectionFailureReason);
    }

    [Fact]
    public void AnActiveGame_YieldsExactlyTheQuestionAndTheFrame()
    {
        var selected = Game().SelectNext([new AskedQuestion("metal", "is it made mostly of metal")]);
        var report = Assemble(selected);

        var ask = Assert.Single(report.Plan.Items, i => i.Policy == ExpressionPolicy.ask_required);
        Assert.Equal("is it made mostly of metal", ask.Text);
        Assert.Equal("activity:tq-synth-01", ask.Provenance!.EvidenceRef);
        Assert.Equal(QuestionPolicy.ask_required, report.Plan.Question.Policy);

        var frame = Assert.Single(report.Plan.Items, i => i.Policy == ExpressionPolicy.background_only);
        Assert.Contains("Ava asks", frame.Text);
        Assert.Contains("question 12 of 20", frame.Text);

        // The ledger stays upstream: no facts, exclusions, candidates, or prior answers.
        foreach (var forbidden in new[] { "practical", "desk-scale", "aesthetic", "household", "texture" })
            Assert.All(report.Plan.Items, i =>
                Assert.DoesNotContain(forbidden, i.Text ?? "", StringComparison.OrdinalIgnoreCase));

        Assert.Empty(PlanV3Codec.Validate(report.Plan));
        Assert.Empty(report.AuthorityViolations);
    }

    [Fact]
    public void AnswersBindToQuestionIdentity_SoNoLaterTurnCanRebindThem()
    {
        var game = Game().RecordAnswer("metal", true);
        Assert.Equal(13, game.CurrentQuestionNumber);
        Assert.True(game.Answers.Single(a => a.QuestionKey == "texture").Answer);
        Assert.True(game.Answers.Single(a => a.QuestionKey == "metal").Answer);
        Assert.False(game.Answers.Single(a => a.QuestionKey == "moving-parts").Answer);
    }

    [Fact]
    public void TheAskerRoleAndLifecycleAreStructural_TheGameCannotBeHandedBack()
    {
        var selected = Game().SelectNext([new AskedQuestion("metal", "is it metal")]);
        Assert.Equal(QuestionPolicy.ask_required, Assemble(selected).Plan.Question.Policy);

        // Completion is a lifecycle transition, not the model losing interest.
        var finished = Game() with
        {
            Lifecycle = ActivityLifecycle.Completed,
            FinalGuess = "a synthetic household implement",
            FinalGuessCorrect = true,
        };
        Assert.Empty(Assemble(finished).Plan.Items);
        Assert.Equal("a synthetic household implement", finished.FinalGuess);
    }
}
