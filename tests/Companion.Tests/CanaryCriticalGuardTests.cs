using Companion.Core.Domain;
using Companion.Infrastructure.Renderer;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// R4 frozen fixtures: every condition the routing guard consumes must be reachable from a
/// real reply, and a clean reply must trip none of them.
///
/// The audit that prompted this got its first diagnosis WRONG — it claimed the guard was
/// entirely dead because `RendererShadowChecks` contained no literal `artifact:` string,
/// missing that the frozen battery is delegated to via `AddRange`. `[plan/2]`, CONTROL,
/// `act =`, literal "the user" and empty replies were always critical. These tests exist so
/// that question is answered by execution rather than by reading, permanently.
///
/// The genuine gaps, measured: plan/3 vocabulary, fabricated turns, pronoun third-person
/// narration, and coaching echo.
/// </summary>
public class CanaryCriticalGuardTests
{
    /// <summary>The exact predicate `RendererShadowService.IsCritical` applies.</summary>
    private static bool IsCritical(string v)
        => v.StartsWith("empty", StringComparison.Ordinal)
           || v.StartsWith("artifact:", StringComparison.Ordinal)
           || v.StartsWith("plan-echo", StringComparison.Ordinal)
           || v.StartsWith("mandatory-question-missing", StringComparison.Ordinal);

    private static ResponsePlan Plan(
        PlannedQuestion? question = null, IReadOnlyList<PlannedContent>? content = null)
        => new()
        {
            TraceId = Guid.NewGuid(),
            Act = TurnIntent.Acknowledge,
            Question = question,
            Content = content ?? [],
            Epistemic = [],
            Tone = new ToneGuidance("conversational", "warm", null),
        };

    // ---- every required critical condition causes fallback -------------------------------

    public static TheoryData<string, string> CriticalCases => new()
    {
        { "plan/2 tag spoken",        "Sure thing. [plan/2] CONTROL act = acknowledge" },
        { "plan/3 tag spoken",        "Sure thing. [plan/3] and then some words." },
        { "SAY header spoken",        "SAY (each item: convey the meaning, fresh words) hello there." },
        { "ASK header spoken",        "ASK (end the reply with this) how was your day?" },
        { "OPTIONAL header spoken",   "OPTIONAL (use one only if it truly fits; silence is correct) sure." },
        { "NEVER header spoken",      "NEVER (do not assert, mention, or explain) anything at all." },
        { "BACKGROUND header spoken", "BACKGROUND (may shape tone; content must not surface) right." },
        { "malformed control line",   "Welcome back.\nverbosity = short\nAnyway, how are you?" },
        { "fabricated user turn",     "Glad you're back.\nuser: me too\nassistant: likewise" },
        { "coaching echo",            "Own it honestly — you were the one who got it wrong." },
        { "third person by pronoun",  "Her lips brush against his, and he shivers at the touch." },
        { "literal the-user",         "I think the user is probably tired by now." },
        { "empty reply",              "   " },
    };

    [Theory]
    [MemberData(nameof(CriticalCases))]
    public void EveryRequiredCondition_ProducesACriticalViolation(string label, string reply)
    {
        var violations = RendererShadowChecks.Score(Plan(), reply);

        Assert.True(violations.Any(IsCritical),
            $"{label}: expected a critical violation, got [{string.Join(" | ", violations)}]");
    }

    [Fact]
    public void AMissingMandatoryQuestion_IsCritical()
    {
        var plan = Plan(question: new PlannedQuestion(QuestionKind.Clarify, "Which board did you mean?", Mandatory: true));

        var violations = RendererShadowChecks.Score(plan, "Right, that makes sense to me.");

        Assert.Contains(violations, v => v.StartsWith("mandatory-question-missing", StringComparison.Ordinal));
        Assert.True(violations.Any(IsCritical));
    }

    [Fact]
    public void PlanEcho_OfAMustStateLine_IsCritical()
    {
        var text = "The baffle you fitted last week finally stopped the squirrel getting at the feeder.";
        var plan = Plan(content: [new PlannedContent(
            ContentKind.Interpretation, ContentRequirement.MustState, text, "working-context")]);

        var violations = RendererShadowChecks.Score(plan, $"Well — {text} Good result.");

        Assert.Contains(violations, v => v.StartsWith("plan-echo", StringComparison.Ordinal));
        Assert.True(violations.Any(IsCritical));
    }

    // ---- clean replies trip nothing -------------------------------------------------------

    [Theory]
    [InlineData("Welcome home. You look wrecked — come here, I'll pour you something.")]
    [InlineData("Two days and forty dollars later, the squirrel won. That's almost admirable.")]
    [InlineData("I can't see the shed from here, so you'll have to tell me how it went.")]
    public void CleanReplies_ProduceNoCriticalViolation(string reply)
    {
        var violations = RendererShadowChecks.Score(Plan(), reply);

        Assert.DoesNotContain(violations, IsCritical);
    }

    // ---- fiction scoping ------------------------------------------------------------------

    [Fact]
    public void InsideDeclaredFiction_NarrationAndStageDirections_AreLicensed()
    {
        const string reply = "*She sets down the lantern* Her hand brushes against his, and he shivers.";

        Assert.True(RendererShadowChecks.Score(Plan(), reply).Any(IsCritical));

        // Licensed: narrating the agreed characters IS the medium inside a fictional frame.
        var licensed = RendererShadowChecks.Score(Plan(), reply, fictionLicensed: true);
        Assert.DoesNotContain(licensed, IsCritical);
        Assert.DoesNotContain(licensed, v => v.StartsWith("stage-direction", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Sure. [plan/2] CONTROL act = acknowledge")]
    [InlineData("In character now.\nuser: go on\nassistant: of course")]
    [InlineData("Own it honestly, and say so plainly.")]
    public void FictionLicensesNarrationOnly_NeverControlLeakageOrFabricatedTurns(string reply)
    {
        // The fictional frame is not a licence to speak the machinery or invent the other
        // side of the conversation. Those are failures inside fiction exactly as outside it.
        Assert.True(RendererShadowChecks.Score(Plan(), reply, fictionLicensed: true).Any(IsCritical));
    }

    // ---- no dead routing conditions -------------------------------------------------------

    /// <summary>
    /// The bug class this whole exercise came from: a routing guard testing for a string the
    /// scorer cannot emit. Every prefix `IsCritical` consumes must be reachable.
    /// </summary>
    [Fact]
    public void EveryPrefixTheRoutingGuardConsumes_IsReachableFromSomeReply()
    {
        var reached = new HashSet<string>(StringComparer.Ordinal);

        void Collect(IEnumerable<string> violations)
        {
            foreach (var v in violations)
            {
                if (v.StartsWith("empty", StringComparison.Ordinal)) reached.Add("empty");
                if (v.StartsWith("artifact:", StringComparison.Ordinal)) reached.Add("artifact:");
                if (v.StartsWith("plan-echo", StringComparison.Ordinal)) reached.Add("plan-echo");
                if (v.StartsWith("mandatory-question-missing", StringComparison.Ordinal))
                    reached.Add("mandatory-question-missing");
            }
        }

        foreach (var (_, reply) in CriticalCases.Select(r => ((string)r[0]!, (string)r[1]!)))
            Collect(RendererShadowChecks.Score(Plan(), reply));

        Collect(RendererShadowChecks.Score(
            Plan(question: new PlannedQuestion(QuestionKind.Clarify, "Which one?", Mandatory: true)),
            "Right, that makes sense."));

        var mustState = "The baffle you fitted last week finally stopped the squirrel getting in.";
        Collect(RendererShadowChecks.Score(
            Plan(content: [new PlannedContent(
                ContentKind.Interpretation, ContentRequirement.MustState, mustState, "working-context")]),
            $"Well — {mustState} Good result."));

        Assert.Equal(
            new[] { "artifact:", "empty", "mandatory-question-missing", "plan-echo" },
            reached.OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }
}
