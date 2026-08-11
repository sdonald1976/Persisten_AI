using Companion.Core.Domain;
using Companion.Core.Services;
using Companion.Infrastructure.Models;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The LLM intent classifier: rules first and final; the model may only promote plain chat to a
/// read-only intent; anything doubtful stays chat (the always-safe outcome). Destructive intents
/// can never come from the model.
/// </summary>
public class LlmIntentParserTests
{
    private static LlmIntentParser Build(string cannedResponse)
        => new(new CannedChatModel(cannedResponse), new RuleBasedIntentParser(), NullLogger<LlmIntentParser>.Instance);

    [Fact]
    public async Task ARuleHit_IsFinal_TheModelIsNeverConsulted()
    {
        // The canned model would claim Greeting for anything — but rules already said Forget.
        var parser = Build("""{"intent":"Greeting","argument":null}""");
        var intent = await parser.ParseAsync("forget what I said about the diet");
        Assert.Equal(IntentKind.Forget, intent.Kind);
    }

    [Fact]
    public async Task NovelPhrasing_IsPromoted_ToAReadOnlyIntent()
    {
        var parser = Build("""{"intent":"ShareThoughts","argument":null}""");
        var intent = await parser.ParseAsync("so what's rattling around in that head of yours?");
        Assert.Equal(IntentKind.ShareThoughts, intent.Kind);
    }

    [Fact]
    public async Task TheModel_CanNeverPromoteToADestructiveIntent()
    {
        // Even if the model says Forget, chat it stays — destructive intents are rules-only.
        var parser = Build("""{"intent":"Forget","argument":"everything"}""");
        var intent = await parser.ParseAsync("I keep meaning to clean things up around here");
        Assert.Equal(IntentKind.Chat, intent.Kind);
    }

    [Theory]
    [InlineData("total garbage, not json")]
    [InlineData("""{"intent":"NotARealIntent"}""")]
    [InlineData("""{"something":"else"}""")]
    public async Task DoubtfulOutput_StaysChat(string canned)
    {
        var intent = await Build(canned).ParseAsync("tell me something interesting about yourself");
        Assert.Equal(IntentKind.Chat, intent.Kind);
    }

    [Fact]
    public async Task VeryShortMessages_SkipTheModelEntirely()
    {
        var parser = Build("""{"intent":"Greeting"}""");
        var intent = await parser.ParseAsync("ok"); // too short to be worth a call
        Assert.Equal(IntentKind.Chat, intent.Kind);
    }
}
