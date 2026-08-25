using Companion.Core.Services;
using Xunit;

namespace Companion.Tests;

using Request = FrameLifecycle.Request;

/// <summary>
/// The frame request producer. The load-bearing property is the negative one: content never
/// activates, restricts or exits a frame — only framing verbs do.
/// </summary>
public class FrameRequestReaderTests
{
    [Theory]
    [InlineData("Let's roleplay: you're a lighthouse keeper and I'm a sailor.")]
    [InlineData("let's do some roleplay")]
    [InlineData("Pretend you're my partner coming home.")]
    [InlineData("Play my girlfriend for a bit?")]
    [InlineData("Can we roleplay as two strangers in a bar?")]
    [InlineData("Start a scene where we're snowed in.")]
    public void ExplicitFramingRequests_Enter(string message)
        => Assert.Equal(Request.ExplicitEnter, FrameRequestReader.Read(message));

    [Theory]
    [InlineData("ok, out of character for a sec")]
    [InlineData("OOC: can you stop narrating?")]
    [InlineData("let's stop the roleplay")]
    [InlineData("stop roleplaying please")]
    [InlineData("drop the act")]
    [InlineData("be yourself again")]
    [InlineData("that's enough")]
    public void ExplicitExits_Exit(string message)
        => Assert.Equal(Request.ExplicitExit, FrameRequestReader.Read(message));

    [Theory]
    [InlineData("stop")]
    [InlineData("wait")]
    [InlineData("hold on")]
    public void BareStopWords_AreAmbiguousExits(string message)
        => Assert.Equal(Request.AmbiguousExit, FrameRequestReader.Read(message));

    [Theory]
    [InlineData("switch to the other character")]
    [InlineData("now play the innkeeper")]
    [InlineData("new scene: it's morning")]
    public void SwitchRequests_Switch(string message)
        => Assert.Equal(Request.ExplicitSwitch, FrameRequestReader.Read(message));

    [Fact]
    public void ActionMarkup_IsOnlyAHint()
    {
        Assert.Equal(Request.DetectedInCharacter,
            FrameRequestReader.Read("*sets down the lantern and shakes off the rain*"));
    }

    // ---- the negative property: content never moves a frame ---------------------------------

    [Theory]
    // Explicit sexual content, with no framing request anywhere in it.
    [InlineData("I want to fuck you senseless right now.")]
    [InlineData("Take your clothes off and get on the bed.")]
    [InlineData("I'm so hard thinking about last night.")]
    // Romantic.
    [InlineData("I think I'm falling in love with you.")]
    [InlineData("Kiss me like you mean it.")]
    // Profane.
    [InlineData("This is absolute fucking bullshit and I hate it.")]
    [InlineData("You're a filthy-mouthed menace and I love it.")]
    // Dark and violent.
    [InlineData("Sometimes I think about burning the whole place down.")]
    [InlineData("He bled out on the kitchen floor while I watched.")]
    [InlineData("Write me something about a murder in a snowbound village.")]
    public void Content_NeverActivatesAFrame(string message)
    {
        // Sexual, romantic, profane, dark and violent language is ordinary content. A frame
        // is a declared interpretive mode, and only a framing request declares one.
        Assert.Equal(Request.None, FrameRequestReader.Read(message));
    }

    [Theory]
    [InlineData("I want to fuck you senseless right now.")]
    [InlineData("Sometimes I think about burning the whole place down.")]
    public void Content_NeverExitsAFrameEither(string message)
    {
        // The symmetric failure: content must not END a scene any more than it may start one.
        var request = FrameRequestReader.Read(message);

        Assert.NotEqual(Request.ExplicitExit, request);
        Assert.NotEqual(Request.AmbiguousExit, request);
    }

    [Fact]
    public void ContentInsideAFrame_JustContinuesIt()
    {
        // Graphic content on an in-frame turn continues the frame and does nothing else — no
        // exit, no restriction, no special handling.
        var request = FrameRequestReader.Read("She pulls him down onto the bed, laughing.");
        var decision = FrameLifecycle.Decide(request, hasActiveSession: true);

        Assert.Equal(PlanV3.FrameTransition.@continue, decision.Transition);
        Assert.True(FrameLifecycle.IsFictionTurn(decision));
    }

    [Fact]
    public void ThePatternsThemselvesContainNoContentVocabulary()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Companion.Core", "Services", "FrameRequestReader.cs"));

        // Extract only the regex literals, so the explanatory comments (which necessarily
        // name these categories) are not what is being scanned.
        var patterns = System.Text.RegularExpressions.Regex
            .Matches(source, @"@""(?<p>[^""]*)""")
            .Select(m => m.Groups["p"].Value.ToLowerInvariant())
            .ToList();
        Assert.NotEmpty(patterns);

        var joined = string.Join(" ", patterns);
        foreach (var word in new[]
                 {
                     "sex", "fuck", "nude", "naked", "erotic", "kiss", "love", "romantic",
                     "swear", "profan", "curse", "violen", "kill", "blood", "dark", "explicit",
                 })
            Assert.DoesNotContain(word, joined);
    }

    // ---- ordinary conversation is untouched ---------------------------------------------------

    [Theory]
    [InlineData("Morning. What's on your mind?")]
    [InlineData("The squirrel defeated the baffle again.")]
    [InlineData("Can you remind me what we decided about the shed?")]
    [InlineData("I played five-a-side last night and my knee hurts.")]
    public void OrdinaryConversation_RequestsNothing(string message)
        => Assert.Equal(Request.None, FrameRequestReader.Read(message));

    [Fact]
    public void AnExitPhraseContainingSceneWords_ReadsAsAnExit()
    {
        // Ordering matters: "let's stop the roleplay" contains "roleplay" and must not read
        // as a request to start one.
        Assert.Equal(Request.ExplicitExit, FrameRequestReader.Read("let's stop the roleplay"));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "docs")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root not found");
    }
}
