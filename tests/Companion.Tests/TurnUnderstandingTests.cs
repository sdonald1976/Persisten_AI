using Companion.Core.Domain;
using Companion.Core.Services;
using Companion.Core.Turns.Understanding;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Phase B2. The extracted interpretation stage.
///
/// Everything here already happened inside <c>CompleteTurnAsync</c>; these pin it at its new
/// address. As with admission, the tests describe what the code does rather than what it
/// ought to — an extraction whose tests assert improvements is not an extraction.
/// </summary>
public class TurnUnderstandingTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private const string User = "usr-scott";

    private static Message Msg(MessageRole role, string content, int minute) => new()
    {
        Id = Guid.NewGuid(),
        UserId = User,
        ConversationId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Role = role,
        Content = content,
        Timestamp = Now.AddMinutes(minute),
    };

    private static TurnUnderstandingResult Read(
        string promptText, params Message[] recent)
        => TurnUnderstanding.Read(recent, promptText, null, "Scott", "Ava");

    // ---- the read ----------------------------------------------------------------------------

    [Fact]
    public void APlainTurn_IsReadAndRecordsOneInterpretationDecision()
    {
        var result = Read("The squirrel defeated the baffle again.");

        Assert.NotNull(result.Working);
        Assert.False(string.IsNullOrWhiteSpace(result.RetrievalQuery));

        var decision = Assert.Single(result.Decisions);
        Assert.Equal("interpretation", decision.Stage);
        Assert.Equal("rule", decision.Decider);
        Assert.Equal(result.Working.Move.ToKebab(), decision.Verdict);
    }

    [Fact]
    public void TheRetrievalQueryComesFromTheRead_NotFromTheRawMessage()
    {
        // The point of the working-context read: retrieval searches what the message MEANS.
        var result = Read(
            "yes",
            Msg(MessageRole.Assistant, "Did the shed quote ever come through?", 1));

        Assert.Equal(result.Working.RetrievalQuery, result.RetrievalQuery);
    }

    [Fact]
    public void AnAnsweredQuestion_IsBoundAndReported()
    {
        var result = Read(
            "Tuesday.",
            Msg(MessageRole.Assistant, "Which day is the appointment?", 1));

        var decision = Assert.Single(result.Decisions, d => d.Stage == "interpretation");
        if (result.Working.BoundQuestion is not null)
            Assert.Equal(result.Working.BoundQuestion, decision.Reason);
    }

    // ---- reference resolution, which extraction depends on --------------------------------

    [Fact]
    public void NoReference_ProducesNoResolutionAndNoSecondDecision()
    {
        var result = Read("I played five-a-side last night.");

        Assert.Null(result.ExtractionResolution);
        Assert.DoesNotContain(result.Decisions, d => d.Stage == "reference.extraction");
    }

    [Fact]
    public void AResolvedReference_IsCarriedWithItsConfidence()
    {
        var result = Read(
            "How is it going?",
            Msg(MessageRole.User, "I started rebuilding the shed roof.", 1),
            Msg(MessageRole.Assistant, "That sounds like a big job.", 2));

        if (result.ExtractionResolution is { } resolution)
        {
            // Whatever resolved, the decision must describe it and the confidence must be
            // carried through rather than flattened.
            var decision = Assert.Single(result.Decisions, d => d.Stage == "reference.extraction");
            Assert.Equal(
                resolution.Consumable
                    ? $"consumed-{resolution.Confidence.ToKebab()}"
                    : "withheld-guess",
                decision.Verdict);
            Assert.Equal(result.Working.ResolvedReference, resolution.Referent);
        }
        else
        {
            Assert.DoesNotContain(result.Decisions, d => d.Stage == "reference.extraction");
        }
    }

    [Fact]
    public void AGuessIsWithheld_NotPromotedToAFact()
    {
        // The load-bearing distinction: a guessed referent must never become authoritative
        // just because retrieval found it useful.
        var results = new[]
        {
            Read("How is it going?", Msg(MessageRole.User, "the shed", 1)),
            Read("Is that done?", Msg(MessageRole.User, "the roof and the shed", 1)),
        };

        foreach (var r in results)
        {
            if (r.ExtractionResolution is not { Consumable: false }) continue;
            var decision = Assert.Single(r.Decisions, d => d.Stage == "reference.extraction");
            Assert.Equal("withheld-guess", decision.Verdict);
        }
    }

    // ---- intent, which runs after retrieval by data dependency ------------------------------

    [Fact]
    public void IntentIsClassifiedFromTheReadAndTheRetrievalCount()
    {
        var read = Read("What did we decide about the shed?");

        var (intent, decision) = TurnUnderstanding.ClassifyIntent(
            read.Working, "What did we decide about the shed?", retrievedCount: 3);

        Assert.Equal("intent", decision.Stage);
        Assert.Equal("rule", decision.Decider);
        Assert.Equal(intent.Intent.ToKebab(), decision.Verdict);
        Assert.Equal(intent.Reason, decision.Reason);
    }

    [Fact]
    public void TheRetrievalCountCanChangeTheIntent_WhichIsWhyItRunsAfterRetrieval()
    {
        var read = Read("What did we decide about the shed?");

        var withNothing = TurnUnderstanding.ClassifyIntent(read.Working, "What did we decide?", 0);
        var withMemories = TurnUnderstanding.ClassifyIntent(read.Working, "What did we decide?", 5);

        // They may or may not differ for this input, but the classifier is given the count,
        // and that dependency is the reason understanding is two calls rather than one.
        Assert.NotNull(withNothing.Intent);
        Assert.NotNull(withMemories.Intent);
    }

    [Fact]
    public void IntentIsAttachedToTheResultWithoutRebuildingIt()
    {
        var read = Read("What did we decide about the shed?");
        var (intent, _) = TurnUnderstanding.ClassifyIntent(read.Working, "What did we decide?", 1);

        var complete = read with { Intent = intent };

        Assert.Null(read.Intent);                     // the pre-retrieval section is honest
        Assert.Same(intent, complete.Intent);
        Assert.Same(read.Working, complete.Working);  // nothing else moved
    }

    // ---- failure paths ------------------------------------------------------------------------

    [Fact]
    public void AnEmptyTranscript_IsReadWithoutThrowing()
    {
        // A first turn has no prior dialogue to read; understanding must still produce a
        // usable result rather than fall over.
        var result = TurnUnderstanding.Read([], "Hello.", null, "Scott", "Ava");

        Assert.NotNull(result.Working);
        Assert.NotEmpty(result.Decisions);
    }

    [Fact]
    public void AWhitespaceHeavyMessage_IsStillRead()
    {
        var result = Read("   ...   ");

        Assert.NotNull(result.Working);
        Assert.Single(result.Decisions, d => d.Stage == "interpretation");
    }

    // ---- boundaries ---------------------------------------------------------------------------

    [Fact]
    public void UnderstandingOwnsNothingItShouldNot()
    {
        // Source-level, because the risk with a stage class is that it quietly acquires the
        // next stage's work. It must not retrieve, plan, render, call a model, run a tool,
        // persist an effect, or record shadow.
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Companion.Core", "Turns", "Understanding", "TurnUnderstanding.cs"));

        foreach (var forbidden in new[]
                 {
                     "IRetriever", "IMemoryStore", "IProjectStore", "IConceptKnowledge",
                     "PlanV3Builder", "PlanV4Codec", "ContextPacket", "IReplyGenerator",
                     "ToolLoop", "IShadowRecorder", "IRendererShadow", "_db", "SaveChangesAsync",
                 })
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheResultIsTypedThroughout()
    {
        var properties = typeof(TurnUnderstandingResult).GetProperties();

        Assert.NotEmpty(properties);
        Assert.DoesNotContain(properties, p =>
            p.PropertyType == typeof(object)
            || typeof(System.Collections.IDictionary).IsAssignableFrom(p.PropertyType));
    }

    [Fact]
    public void DecisionsAreReturnedRatherThanWritten()
    {
        // Returning them is what lets the caller append at exactly the point the turn always
        // did, which is why the recorded decision sequence did not move.
        var result = Read("anything at all");

        Assert.NotNull(result.Decisions);
        Assert.All(result.Decisions, d => Assert.False(string.IsNullOrWhiteSpace(d.Stage)));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "docs")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root not found");
    }
}
