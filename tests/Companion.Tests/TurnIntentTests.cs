using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Companion.Infrastructure.Seeding;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Turn intent (language-organ Phase 2): what Ava should DO this turn, classified
/// deterministically from working context + retrieval, in shadow. The vocabulary names acts,
/// never prose — and "unknown" (continue naturally) is the correct answer whenever nothing
/// clears the bar, because a wrong "unknown" costs a corpus row while a wrong intent would
/// one day cost a turn.
/// </summary>
public class TurnIntentTests
{
    private static Message A(string content) => new() { Role = MessageRole.Assistant, Content = content };
    private static Message U(string content) => new() { Role = MessageRole.User, Content = content };

    private static TurnIntentState Classify(Message[] recent, string message, int retrieved = 3)
        => TurnIntentClassifier.Classify(WorkingContext.Read(recent, message), message, retrieved);

    // ---- each intent, from its grounding situation ----

    [Fact]
    public void AQuestion_IsAnswerQuestion()
    {
        var intent = Classify(new[] { A("The greenhouse held overnight.") }, "What temperature did it drop to?");
        Assert.Equal(TurnIntentClassifier.Intents.AnswerQuestion, intent.Intent);
    }

    [Fact]
    public void AFirstPersonShare_IsAcknowledge()
    {
        var intent = Classify(Array.Empty<Message>(), "My sister Beth is visiting on Saturday.");
        Assert.Equal(TurnIntentClassifier.Intents.Acknowledge, intent.Intent);
    }

    [Fact]
    public void AnAnswerToHerOwnQuestion_IsRespondToAnswer()
    {
        var intent = Classify(new[] { A("What's your favorite kind of magic?") }, "Additive.");
        Assert.Equal(TurnIntentClassifier.Intents.RespondToAnswer, intent.Intent);
        Assert.Contains("favorite kind of magic", intent.Reason);
    }

    [Fact]
    public void ACorrection_IsAcceptCorrection()
    {
        var recent = new[] { U("Plant the oak by the gate."), A("Oak by the gate — noted.") };
        var intent = Classify(recent, "Actually, I meant the maple, not the oak.");
        Assert.Equal(TurnIntentClassifier.Intents.AcceptCorrection, intent.Intent);
    }

    [Fact]
    public void CANONICAL_AQuestionHangingOnAnAmbiguousReference_IsClarify()
    {
        // THE CANONICAL PROMOTION CASE. Two possible "her"s in the window: answering means
        // guessing; the act is to ask. In the first live shadow run the system selected
        // clarify (0.75) here and qwen3:8b, uninstructed, answered anyway ("a roasted potato
        // dish") without asking which sister — the first recorded turn where authoritative
        // intent would have produced a better act than the model's default. When intent is
        // ever promoted into generation, THIS is the case the promotion must win.
        var recent = new[] { U("My sisters Beth and Clara are both visiting this weekend.") };
        var intent = Classify(recent, "What should I cook for her?");

        Assert.Equal(TurnIntentClassifier.Intents.Clarify, intent.Intent);
        Assert.Contains(intent.Candidates, c => c.Intent == TurnIntentClassifier.Intents.AnswerQuestion);
    }

    // ---- request/directive: promoted to selectable on the 2026-08-20 evidence (6/6
    // consistent signature; the model performed the requested act 6/6 times) ----

    [Theory]
    [InlineData("Ask me a question.")]
    [InlineData("Tell me about border collies.")]
    [InlineData("Help me figure this out.")]
    [InlineData("Give me two choices.")]
    [InlineData("Don't answer that yet.")]
    [InlineData("Remind me what we were discussing.")]
    public void ADirective_IsSelected(string message)
    {
        var intent = Classify(new[] { A("Morning!") }, message);
        Assert.Equal(TurnIntentClassifier.Intents.RequestDirective, intent.Intent);
    }

    [Fact]
    public void AQuestionFormRequest_IsAnswerQuestion_WithDirectiveCompeting()
    {
        // "Can you…?" is answered by performing it — answer-question already says so, and
        // the directive candidate rides behind as the shape's record.
        var intent = Classify(new[] { A("Morning!") }, "Can you remind me what we discussed?");

        Assert.Equal(TurnIntentClassifier.Intents.AnswerQuestion, intent.Intent);
        Assert.Contains(intent.Candidates,
            c => c.Intent == TurnIntentClassifier.Intents.RequestDirective);
    }

    [Theory]
    [InlineData("My sister Beth is visiting on Saturday.")]
    [InlineData("What breeds make good farm dogs?")]
    [InlineData("The greenhouse held its temperature overnight.")]
    public void OrdinaryTurns_AreNotDirectives(string message)
        => Assert.False(TurnIntentClassifier.LooksDirective(message));

    [Fact]
    public void AProgressQuestion_WithNothingRetrieved_IsAdmitUnknown()
    {
        // The documented failure: "how's that plot coming along?" answered with three
        // paragraphs of invented compost layers. With zero memories retrieved, the honest
        // act is saying she can't see it.
        var intent = Classify(
            new[] { A("Morning!") }, "How's the allotment plot coming along?", retrieved: 0);

        Assert.Equal(TurnIntentClassifier.Intents.AdmitUnknown, intent.Intent);
    }

    [Fact]
    public void TheSameProgressQuestion_WithMemories_IsAnswerQuestion()
    {
        var intent = Classify(
            new[] { A("Morning!") }, "How's the allotment plot coming along?", retrieved: 4);
        Assert.Equal(TurnIntentClassifier.Intents.AnswerQuestion, intent.Intent);
    }

    [Fact]
    public void AThreadContinuation_IsContinueTopic()
    {
        var recent = new[]
        {
            U("The irrigation manifold needs a new gasket before the frost."),
            A("The manifold gasket — that's the brittle one from last spring?"),
        };
        var intent = Classify(recent, "That gasket cracked along the manifold seam again.");
        Assert.Equal(TurnIntentClassifier.Intents.ContinueTopic, intent.Intent);
    }

    [Fact]
    public void ATopicChange_IsFollowTopicChange()
    {
        var recent = new[]
        {
            U("The irrigation manifold needs a new gasket."),
            A("I'll hold onto that."),
        };
        var intent = Classify(recent, "Completely different thing — the council approved the extension.");
        Assert.Equal(TurnIntentClassifier.Intents.FollowTopicChange, intent.Intent);
    }

    // ---- ambiguous and negative cases ----

    [Fact]
    public void ABareInterjection_WithNoQuestionInPlay_IsUnknown()
    {
        var recent = new[] { A("I repotted the ferns this morning.") };
        var intent = Classify(recent, "lol");

        Assert.Equal(TurnIntentClassifier.Intents.Unknown, intent.Intent);
        Assert.Contains("continue naturally", intent.Reason);
    }

    [Fact]
    public void Lol_EvenAfterHerQuestion_IsUnknown_NotAnAnswer()
    {
        // Live shadow catch, pinned: her reply ended with a question, the user typed "lol",
        // and the binding treated it as the answer — classifying the turn as responding to
        // her question. Laughter reacts to a question; it does not answer it.
        var intent = Classify(new[] { A("Do you have any favorite recipes you'd like to include?") }, "lol");
        Assert.Equal(TurnIntentClassifier.Intents.Unknown, intent.Intent);
    }

    [Fact]
    public void Yeah_AfterHerQuestion_IsNotAnInterjection_ButAnAnswer()
    {
        var intent = Classify(new[] { A("Want me to keep track of the seed order?") }, "yeah");
        Assert.Equal(TurnIntentClassifier.Intents.RespondToAnswer, intent.Intent);
    }

    [Fact]
    public void AnAmbiguousReferenceInAStatement_DoesNotSelectClarify_ButListsIt()
    {
        // Understanding is dented, not blocked — a reply doesn't require resolving "her",
        // so clarify is a competing candidate for the shadow data, never the selection.
        var recent = new[] { U("My sisters Beth and Clara are both visiting this weekend.") };
        var intent = Classify(recent, "I'm baking a pie for her.");

        Assert.NotEqual(TurnIntentClassifier.Intents.Clarify, intent.Intent);
        Assert.Contains(intent.Candidates, c => c.Intent == TurnIntentClassifier.Intents.Clarify);
    }

    [Fact]
    public void Candidates_AreOrderedStrongestFirst_AndIncludeTheWinner()
    {
        var recent = new[] { U("My sisters Beth and Clara are both visiting this weekend.") };
        var intent = Classify(recent, "I'm baking a pie for her.");

        Assert.Equal(intent.Intent, intent.Candidates[0].Intent);
        Assert.Equal(intent.Candidates.OrderByDescending(c => c.Confidence).Select(c => c.Intent),
            intent.Candidates.Select(c => c.Intent));
    }

    // ---- shadow discipline: recorded everywhere, injected nowhere ----

    [Fact]
    public async Task Intent_ReachesTheRing_AndNeverThePacket()
    {
        var host = new TestHost(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));
        await using var _ = host;
        using var scope = host.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CompanionSeeder>().SeedAsync(host.Clock.GetUtcNow());
        var conv = await scope.ServiceProvider.GetRequiredService<IConversationStore>()
            .StartConversationAsync(CompanionSeeder.DemoUserId, "t", "mock", "test");

        var trace = await scope.ServiceProvider.GetRequiredService<ICompanion>()
            .RespondAsync(CompanionSeeder.DemoUserId, conv.Id, "My dog is called Precious.");

        var turn = Assert.Single(host.Services.GetRequiredService<ITurnTraceLog>()
            .Recent(CompanionSeeder.DemoUserId, 1));
        Assert.NotNull(turn.Intent);
        Assert.Equal(TurnIntentClassifier.Intents.Acknowledge, turn.Intent!.Intent);
        Assert.Equal(turn.Intent.Intent, turn.Decisions.Single(d => d.Stage == "intent").Verdict);

        // Non-authoritative: the packet the model reads carries no trace of the vocabulary.
        var rendered = trace.Packet.Render();
        Assert.DoesNotContain("acknowledge", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("intent", rendered, StringComparison.OrdinalIgnoreCase);
    }
}
