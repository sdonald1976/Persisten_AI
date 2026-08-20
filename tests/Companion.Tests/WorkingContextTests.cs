using Companion.Core.Domain;
using Companion.Core.Services;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The working-context read (language-organ Phase 1): multi-turn scenarios at 1, 3, 5, and
/// 10 turns, phrased the way a person actually refers back — "the second one", "yeah", "her",
/// "what I said before" — plus the negative cases, which matter as much: an old hanging
/// question must not hijack a new topic, and mid-sentence "actually" is not a correction.
/// All pure-function: transcripts in, explicit state out, no host, no models.
/// </summary>
public class WorkingContextTests
{
    private static Message A(string content) => new() { Role = MessageRole.Assistant, Content = content };
    private static Message U(string content) => new() { Role = MessageRole.User, Content = content };

    // ---- 1 turn back ----

    [Fact]
    public void Yeah_AfterAQuestion_AnswersIt()
    {
        var recent = new[] { A("Want me to keep track of the seed order for you?") };

        var state = WorkingContext.Read(recent, "yeah");

        Assert.Equal(WorkingContext.Moves.AnswersOpenQuestion, state.Move);
        Assert.Equal("Want me to keep track of the seed order for you?", state.BoundQuestion);
        Assert.Contains("seed order", state.RetrievalQuery);
        Assert.NotNull(state.InterpretationNote);
        Assert.Empty(state.OpenQuestions); // just answered — no longer open
    }

    [Fact]
    public void TheSecondOne_AgainstInlineOptions_ResolvesTheItem()
    {
        var recent = new[] { A(
            "Which planting bed should we plan first: the herb spiral, the raised beds, or the greenhouse benches?") };

        var state = WorkingContext.Read(recent, "The second one.");

        Assert.Equal(WorkingContext.Moves.AnswersOpenQuestion, state.Move);
        Assert.Equal("the raised beds", state.ResolvedReference);
        Assert.Contains("the raised beds", state.RetrievalQuery);
        Assert.Contains("the raised beds", state.InterpretationNote);
    }

    [Fact]
    public void TheLastOne_AgainstABulletedList_ResolvesTheItem()
    {
        var recent = new[] { A("A few options for the hedge:\n- oak\n- maple\n- birch") };

        var state = WorkingContext.Read(recent, "Let's go with the last one.");

        Assert.Equal(WorkingContext.Moves.ResolvesReference, state.Move);
        Assert.Equal("birch", state.ResolvedReference);
        Assert.Contains("birch", state.RetrievalQuery);
        Assert.NotNull(state.InterpretationNote); // exact resolution — assertive
    }

    [Fact]
    public void TheSecondOne_AgainstProseAlternatives_ResolvesTheWholePhrase()
    {
        // Verbatim shape from the live evidence run: two options offered as flowing prose
        // with descriptive commas inside each. The first cut of the splitter turned the
        // descriptive comma into an option called "comforting meal".
        var recent = new[] { A(
            "How about a cozy pumpkin risotto for a creamy, comforting meal, or a quick " +
            "sheet pan chicken with roasted veggies and garlic bread for a fuss-free option?") };

        var state = WorkingContext.Read(recent, "The second one.");

        Assert.Contains("sheet pan chicken", state.ResolvedReference);
        Assert.DoesNotContain("comforting meal", state.ResolvedReference);
    }

    [Fact]
    public void AnOffering_WithoutAQuestionMark_StillEnumerates()
    {
        var recent = new[] { A("You could try the lemon tart, or maybe the plum galette.") };

        var state = WorkingContext.Read(recent, "the second one");

        Assert.Equal("the plum galette", state.ResolvedReference);
    }

    [Fact]
    public void NarrativeOr_IsNotAnOffering()
    {
        // " or " inside narration offers nothing; the cue requirement is the guard.
        var recent = new[] { A("I read for an hour or so this evening.") };

        var state = WorkingContext.Read(recent, "The second one.");

        Assert.Null(state.ResolvedReference);
        Assert.Null(state.InterpretationNote);
    }

    [Fact]
    public void AnEmojiDecoratedQuestion_IsStillATrailingQuestion()
    {
        // qwen-family models sign off with emoji; the question is no less open for it.
        var recent = new[] { A("Which do you prefer: oak, maple, or birch? \U0001F333") };

        var state = WorkingContext.Read(recent, "the last one");

        Assert.Equal("birch", state.ResolvedReference);
    }

    [Fact]
    public void AnOrdinal_WithNothingEnumerated_ClassifiesWithoutAsserting()
    {
        var recent = new[] { A("The greenhouse held its temperature overnight.") };

        var state = WorkingContext.Read(recent, "The second one.");

        Assert.Equal(WorkingContext.Moves.ContinuesThread, state.Move);
        Assert.Null(state.ResolvedReference);
        Assert.Null(state.InterpretationNote);
        Assert.Equal(state.RawQuery, state.RetrievalQuery);
        Assert.Contains("The second one", state.ReferenceMarkers.Single());
    }

    // ---- 3 turns back: the hijack negative ----

    [Fact]
    public void AnOldHangingQuestion_DoesNotHijack_AShortNewTopicMessage()
    {
        var recent = new[]
        {
            A("What's your favorite kind of magic?"),
            U("Let me tell you about the pond project instead."),
            A("That sounds exciting."),
        };

        var state = WorkingContext.Read(recent, "Additive.");

        // No binding — the question is three messages old, not the turn being answered.
        Assert.Equal(WorkingContext.Moves.NewTopic, state.Move);
        Assert.Null(state.BoundQuestion);
        Assert.Null(state.InterpretationNote);
        Assert.Equal("Additive.", state.RetrievalQuery);

        // But the question is not lost: it is held as explicit open-question state.
        var open = Assert.Single(state.OpenQuestions);
        Assert.Equal("What's your favorite kind of magic?", open.Question);
    }

    [Fact]
    public void AQuestionTheUserAnswered_InProse_IsNotHeldOpen()
    {
        var recent = new[]
        {
            A("What's your favorite kind of magic?"),
            U("Honestly my favorite magic is the additive kind, hands down."),
            A("That tracks."),
        };

        var state = WorkingContext.Read(recent, "Anyway, the pond liner arrived.");

        Assert.Empty(state.OpenQuestions);
    }

    // ---- 5 turns back: pronouns and entities ----

    [Fact]
    public void Her_ResolvesToTheMostRecentPersonEntity_QueryOnly()
    {
        var recent = new[]
        {
            U("My sister Beth is visiting next week."),
            A("That's lovely — how long will she stay?"),
            U("About ten days."),
            A("Plenty of time for the garden tour then."),
        };

        var state = WorkingContext.Read(recent, "I'm planning a dinner for her.");

        Assert.Equal(WorkingContext.Moves.ResolvesReference, state.Move);
        Assert.Equal("Beth", state.ResolvedReference);
        Assert.Contains("Beth", state.RetrievalQuery);
        Assert.Contains("Beth", state.SalientEntities);
        // An entity GUESS rewrites the query but never asserts a note to the model.
        Assert.Null(state.InterpretationNote);
    }

    [Fact]
    public void Her_PrefersThePersonTheUserIntroduced_OverNamesInHerOwnReply()
    {
        // Verbatim from the first live qwen3 run: the companion's reply led with
        // "Will Precious get to meet Beth…", and "her" resolved to "Will Precious" — an
        // auxiliary verb plus the dog's name, lifted from her own message — while the sister
        // the user had just introduced sat one message earlier.
        var recent = new[]
        {
            U("My sister Beth is visiting on Saturday."),
            A("Will Precious get to meet Beth, or should I suggest a cozy spot for the pup during the visit?"),
        };

        var state = WorkingContext.Read(recent, "I'm planning a small dinner for her.");

        Assert.Equal("Beth", state.ResolvedReference);
        Assert.DoesNotContain(state.SalientEntities, e => e.StartsWith("Will", StringComparison.Ordinal));
    }

    [Fact]
    public void WhatISaidBefore_ResolvesToThePreviousSubstantiveUserMessage()
    {
        var recent = new[]
        {
            U("The tomatoes in the north bed are showing blight on the lower leaves."),
            A("Noted — I'll keep an eye on the forecast with you."),
        };

        var state = WorkingContext.Read(recent, "Can you remind me what I said before about the tomatoes?");

        Assert.Equal(WorkingContext.Moves.ResolvesReference, state.Move);
        Assert.Contains("blight", state.ResolvedReference);
        Assert.Contains("blight", state.RetrievalQuery);
        Assert.NotNull(state.InterpretationNote); // their own words — assertive
    }

    // ---- 10 turns back: the window boundary, honestly ----

    [Fact]
    public void AReferent_OlderThanTheVisibleWindow_DoesNotResolve_AndDoesNotHijack()
    {
        // Beth was named ten turns ago; the pipeline hands WorkingContext only the recent
        // window (RecentMessageCount, default 6). The right behavior at the boundary is
        // honest failure: classify the reference, resolve nothing, assert nothing.
        var window = new[]
        {
            U("The compost thermometer read fifty degrees this morning."),
            A("That pile is working hard."),
            U("I turned it twice this week."),
            A("Good rhythm."),
            U("The new bin design is holding up too."),
            A("I'm glad the hinges worked out."),
        };

        var state = WorkingContext.Read(window, "Do you think I should invite her?");

        Assert.Contains("her", state.ReferenceMarkers);
        Assert.Null(state.ResolvedReference);
        Assert.Null(state.InterpretationNote);
        Assert.Equal(state.RawQuery, state.RetrievalQuery);
        Assert.Equal(WorkingContext.Moves.ContinuesThread, state.Move);
    }

    // ---- corrections ----

    [Fact]
    public void ALeadingActually_IsACorrection()
    {
        var recent = new[]
        {
            U("Plant the oak by the gate."),
            A("Oak by the gate — noted."),
        };

        var state = WorkingContext.Read(recent, "Actually, I meant the maple, not the oak.");

        Assert.Equal(WorkingContext.Moves.Correction, state.Move);
        Assert.NotNull(state.InterpretationNote);
    }

    [Fact]
    public void AMidSentenceActually_IsNotACorrection()
    {
        var recent = new[] { A("How is the greenhouse coping with the heat?") };

        var state = WorkingContext.Read(recent, "It's actually holding up well out there.");

        Assert.NotEqual(WorkingContext.Moves.Correction, state.Move);
    }

    // ---- topic and entities ----

    [Fact]
    public void Topic_PrefersTheResolvedProject_ThenContentWords()
    {
        var recent = new[] { U("The irrigation manifold needs a new gasket.") };

        Assert.Equal("Pond Project",
            WorkingContext.Read(recent, "ok", resolvedProject: "Pond Project").Topic);

        var topic = WorkingContext.Read(recent, "ok").Topic;
        Assert.NotNull(topic);
        Assert.Contains("irrigation", topic);
    }

    [Fact]
    public void SpeakerNames_AreNotSalientEntities()
    {
        var recent = new[]
        {
            U("I told Ava about the Jetson board that Scott ordered."),
        };

        var state = WorkingContext.Read(recent, "any update?",
            userName: "Scott", companionName: "Ava");

        Assert.Contains("Jetson", state.SalientEntities);
        Assert.DoesNotContain("Ava", state.SalientEntities);
        Assert.DoesNotContain("Scott", state.SalientEntities);
    }
}
