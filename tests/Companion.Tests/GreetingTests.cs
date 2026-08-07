using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Companion.Infrastructure.Models;
using Companion.Infrastructure.Seeding;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The companion opens the conversation so the user never has to initiate: a warm, low-pressure
/// message plus memory-grounded starters. "Hi" gets the same welcome instead of a blank reply.
/// </summary>
public class GreetingTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private const string User = CompanionSeeder.DemoUserId;

    [Fact]
    public async Task Greeting_WithNoHistory_IsWarm_AndAsksForNothingSpecific()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();

        var greeting = await scope.ServiceProvider.GetRequiredService<IGreeter>().GreetAsync(User);

        Assert.Empty(greeting.Openers);                       // nothing to resume yet
        Assert.Contains("haven't talked before", greeting.Message);
        Assert.False(string.IsNullOrWhiteSpace(greeting.ToDisplayText()));
    }

    [Fact]
    public async Task Greeting_WithHistory_OffersGroundedOpeners_FromOpenLoops()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await sp.GetRequiredService<CompanionSeeder>().SeedAsync(Now);

        var greeting = await sp.GetRequiredService<IGreeter>().GreetAsync(User);

        Assert.NotEmpty(greeting.Openers);
        // The seed has an open "awaiting delivery" loop and hardware projects — openers are real.
        Assert.Contains(greeting.Openers, o =>
            o.Contains("buoy", StringComparison.OrdinalIgnoreCase) ||
            o.Contains("Jetson", StringComparison.OrdinalIgnoreCase) ||
            o.Contains("left off", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("just say what's on your mind", greeting.Message); // no pressure to pick one
    }

    [Theory]
    [InlineData("hi")]
    [InlineData("Hi!")]
    [InlineData("hello there")]
    [InlineData("hey")]
    [InlineData("good morning")]
    [InlineData("howdy")]
    public void Parser_TreatsBareGreetings_AsGreetingIntent(string text)
        => Assert.Equal(IntentKind.Greeting, new RuleBasedIntentParser().Parse(text).Kind);

    [Theory]
    [InlineData("hi, can you help me plan the trip?")]
    [InlineData("hello, what do you remember about me?")]
    public void Parser_DoesNotHijack_GreetingsThatCarryARequest(string text)
        => Assert.NotEqual(IntentKind.Greeting, new RuleBasedIntentParser().Parse(text).Kind);

    [Fact]
    public async Task Agent_SayingHi_ReturnsTheGreeting_NotAModelReply()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await sp.GetRequiredService<CompanionSeeder>().SeedAsync(Now);
        var conv = (await sp.GetRequiredService<IConversationStore>()
            .StartConversationAsync(User, "t", "mock", "test")).Id;

        var reply = await sp.GetRequiredService<IAgent>().HandleAsync(User, conv, "hi");

        Assert.Equal(AgentReplyKind.Action, reply.Kind);
        Assert.Equal(IntentKind.Greeting, reply.Intent);
        Assert.Contains("where we left things", reply.Text);
    }

    // ---- LLM-written greeting (real-model configuration) ----

    private sealed class StubGreeter : IGreeter
    {
        private readonly Greeting _greeting;
        public StubGreeter(Greeting greeting) => _greeting = greeting;
        public Task<Greeting> GreetAsync(string userId, CancellationToken ct = default) => Task.FromResult(_greeting);
    }

    private static readonly Greeting Grounded = new()
    {
        Message = "FALLBACK MESSAGE",
        Openers = new[] { "Pick up where we left off — the buoy sensor board?", "How's the Jetson going?" },
    };

    [Fact]
    public async Task LlmGreeter_UsesTheModelsWording_ButKeepsTheRealOpeners()
    {
        var greeter = new LlmGreeter(
            new StubGreeter(Grounded),
            new CannedChatModel("Hey, good to see you back! Want to pick up the buoy board, or just chat?"),
            NullLogger<LlmGreeter>.Instance);

        var greeting = await greeter.GreetAsync(User);

        Assert.StartsWith("Hey, good to see you back!", greeting.Message); // model-written, not the template
        Assert.Equal(Grounded.Openers, greeting.Openers);                  // real threads preserved for the UI chips
    }

    [Fact]
    public async Task LlmGreeter_FallsBackToDeterministic_WhenTheModelReturnsNothing()
    {
        var greeter = new LlmGreeter(
            new StubGreeter(Grounded),
            new CannedChatModel("   "), // empty/whitespace reply
            NullLogger<LlmGreeter>.Instance);

        var greeting = await greeter.GreetAsync(User);

        Assert.Equal("FALLBACK MESSAGE", greeting.Message);
    }
}
