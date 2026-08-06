using Companion.Core.Domain;
using Companion.Core.Services;
using Xunit;

namespace Companion.Tests;

public class IntentParserTests
{
    private static readonly RuleBasedIntentParser Parser = new();

    [Theory]
    [InlineData("forget that", IntentKind.Forget)]
    [InlineData("delete that memory", IntentKind.Forget)]
    [InlineData("that's wrong", IntentKind.Dispute)]
    [InlineData("no, that's not right", IntentKind.Dispute)]
    [InlineData("what do you remember about me", IntentKind.Recall)]
    [InlineData("that was great", IntentKind.FeedbackPositive)]
    [InlineData("that was unhelpful", IntentKind.FeedbackNegative)]
    [InlineData("be more concise", IntentKind.AdjustStyle)]
    [InlineData("be concise", IntentKind.AdjustStyle)]
    [InlineData("talk like a pirate", IntentKind.AdjustStyle)]
    [InlineData("set your persona to a witty sailor", IntentKind.SetPersona)]
    [InlineData("list my projects", IntentKind.ListProjects)]
    [InlineData("what am I working on", IntentKind.ListProjects)]
    [InlineData("what are my open loops", IntentKind.ListOpenLoops)]
    [InlineData("what's unfinished", IntentKind.ListOpenLoops)]
    [InlineData("consolidate your memories", IntentKind.Consolidate)]
    public void RecognizesIntent(string input, IntentKind expected)
        => Assert.Equal(expected, Parser.Parse(input).Kind);

    [Theory]
    [InlineData("How's the Jetson project going?")]
    [InlineData("I finally tested that board at home.")]
    [InlineData("tell me about the buoy project")]
    [InlineData("be quiet")] // not a style adjective — must not be hijacked
    [InlineData("I deployed the service today")]
    public void OrdinaryConversation_StaysChat(string input)
        => Assert.Equal(IntentKind.Chat, Parser.Parse(input).Kind);

    [Fact]
    public void Forget_ExtractsTopic_WhenNamed()
    {
        Assert.Null(Parser.Parse("forget that").Argument);                 // referent → last memory
        Assert.Contains("low-carb", Parser.Parse("forget the low-carb thing").Argument!, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("buoy", Parser.Parse("forget what I said about the buoy").Argument!, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Recall_ExtractsTopic_OrNullForWholeProfile()
    {
        Assert.Null(Parser.Parse("what do you remember about me").Argument);
        Assert.Contains("jetson", Parser.Parse("what do you know about the jetson project").Argument!, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SetPersona_CapturesTheText()
        => Assert.Equal("a witty sailor", Parser.Parse("set your persona to a witty sailor").Argument);
}
