using Companion.Core.Abstractions;
using Companion.Infrastructure.Models;
using Companion.PlanV3;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The executive planner is a model in the PLANNING seat: it may select and order optional
/// items and decline an optional question, and nothing else. Every test that hands it a
/// hostile or broken proposal must end with the deterministic plan standing untouched.
/// </summary>
public class ExecutivePlannerTests
{
    private static PlanV3.PlanV3 Plan(
        QuestionPolicy policy = QuestionPolicy.question_forbidden, string? questionItem = null)
        => new()
        {
            TraceId = Guid.NewGuid(),
            Participants =
            [
                new Participant("demo-user", ParticipantRole.user, "Scott"),
                new Participant("companion-ava", ParticipantRole.companion, "Ava"),
            ],
            Act = "acknowledge",
            Question = new QuestionPolicyBlock(policy, questionItem),
            Items =
            [
                new PlanItem
                {
                    Id = "f1", Type = "note", Policy = ExpressionPolicy.must_express,
                    Text = "the meeting moved to Tuesday", Source = "retrieval",
                },
                new PlanItem
                {
                    Id = "m1", Type = "memory", Policy = ExpressionPolicy.may_express,
                    Text = "the last reschedule was also a Tuesday", Source = "retrieval",
                },
                new PlanItem
                {
                    Id = "m2", Type = "memory", Policy = ExpressionPolicy.may_express,
                    Text = "the room downstairs is quieter", Source = "retrieval",
                },
            ],
        };

    private static PlanningSignals Signals() => new() { UserMessage = "did the meeting move?" };

    private static LlmExecutivePlanner Planner(params string[] replies)
        => new(new QueuedChatModel(replies), NullLogger<LlmExecutivePlanner>.Instance);

    [Fact]
    public async Task AValidProposal_KeepsOnlyTheChosenOptionalItems()
    {
        var planner = Planner("""{"include":["m2"],"order":["m2"],"ask":false}""");

        var outcome = await planner.RefineAsync(Plan(), Signals());

        Assert.Equal("refined", outcome.Decision.Verdict);
        Assert.Contains(outcome.Plan.Items, i => i.Id == "m2");
        Assert.DoesNotContain(outcome.Plan.Items, i => i.Id == "m1");
        // The obligation is not the model's to touch, and it is still there.
        Assert.Contains(outcome.Plan.Items, i => i.Id == "f1"
            && i.Policy == ExpressionPolicy.must_express);
    }

    [Fact]
    public async Task AProposalNamingAnUnofferedId_IsRejectedWhole()
    {
        // "f1" is a must_express obligation. A planner reaching for it is a planner reaching
        // for authority it does not have, and the entire proposal dies rather than the bad
        // part being trimmed - partial acceptance of a hostile proposal is still acceptance.
        var planner = Planner("""{"include":["f1"],"order":["f1"],"ask":false}""");

        var outcome = await planner.RefineAsync(Plan(), Signals());

        Assert.Equal("deterministic", outcome.Decision.Verdict);
        Assert.Contains("not an offered", outcome.Decision.Reason);
        Assert.Equal(3, outcome.Plan.Items.Count);
    }

    [Fact]
    public async Task NonJsonOutput_FallsBackToTheDeterministicPlan()
    {
        var planner = Planner("Sure! I think you should include m1 because...");

        var outcome = await planner.RefineAsync(Plan(), Signals());

        Assert.Equal("deterministic", outcome.Decision.Verdict);
        Assert.Equal(3, outcome.Plan.Items.Count);
    }

    [Fact]
    public async Task DecliningTheOptionalQuestion_HardensThePlan_AndDropsTheSuggestion()
    {
        var plan = Plan(QuestionPolicy.may_ask, "q1") with
        {
            Items =
            [
                .. Plan().Items,
                new PlanItem
                {
                    Id = "q1", Type = "question", Policy = ExpressionPolicy.may_express,
                    Category = RenderCategory.curiosity,
                    Text = "want me to move the room booking too", Source = "curiosity",
                },
            ],
        };
        var planner = Planner("""{"include":[],"order":[],"ask":false}""");

        var outcome = await planner.RefineAsync(plan, Signals());

        Assert.Equal("refined", outcome.Decision.Verdict);
        Assert.Equal(QuestionPolicy.question_forbidden, outcome.Plan.Question.Policy);
        Assert.DoesNotContain(outcome.Plan.Items, i => i.Id == "q1");
    }

    [Fact]
    public async Task KeepingTheOptionalQuestion_PreservesTheSuggestionItem()
    {
        var plan = Plan(QuestionPolicy.may_ask, "q1") with
        {
            Items =
            [
                .. Plan().Items,
                new PlanItem
                {
                    Id = "q1", Type = "question", Policy = ExpressionPolicy.may_express,
                    Category = RenderCategory.curiosity,
                    Text = "want me to move the room booking too", Source = "curiosity",
                },
            ],
        };
        // The model excluded q1 from include but asked for the question: the contradiction
        // resolves in favour of the question, so the suggestion survives and the plan stays
        // structurally valid.
        var planner = Planner("""{"include":[],"order":[],"ask":true}""");

        var outcome = await planner.RefineAsync(plan, Signals());

        Assert.Equal("refined", outcome.Decision.Verdict);
        Assert.Equal(QuestionPolicy.may_ask, outcome.Plan.Question.Policy);
        Assert.Contains(outcome.Plan.Items, i => i.Id == "q1");
    }

    [Fact]
    public async Task AModelTransportFailure_FallsBackToTheDeterministicPlan()
    {
        var planner = new LlmExecutivePlanner(
            new ThrowingChatModel(), NullLogger<LlmExecutivePlanner>.Instance);

        var outcome = await planner.RefineAsync(Plan(), Signals());

        Assert.Equal("deterministic", outcome.Decision.Verdict);
        Assert.Contains("planner error", outcome.Decision.Reason);
        Assert.Equal(3, outcome.Plan.Items.Count);
    }

    private sealed class ThrowingChatModel : IChatModel
    {
        public Task<ChatCompletion> CompleteAsync(
            string systemPrompt, string userMessage, ResponseFormat? format = null,
            string? assistantPrefix = null, CancellationToken ct = default)
            => throw new HttpRequestException("planner endpoint down");

        public Task<ChatCompletion> StreamAsync(
            string systemPrompt, string userMessage, IProgress<string> sink,
            string? assistantPrefix = null, CancellationToken ct = default)
            => throw new HttpRequestException("planner endpoint down");
    }
}
