using Companion.Core;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Companion.Infrastructure.Models;
using Companion.Infrastructure.Seeding;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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

    [Fact]
    public async Task Greeting_AcknowledgesHowLongItsBeen()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var conversations = scope.ServiceProvider.GetRequiredService<IConversationStore>();
        var conv = await conversations.StartConversationAsync(User, "t", "mock", "test");
        // A message left three days before "now".
        await conversations.AddMessageAsync(new Message
        {
            Id = Guid.NewGuid(),
            ConversationId = conv.Id,
            UserId = User,
            Role = MessageRole.User,
            Content = "see you later",
            Timestamp = Now.AddDays(-3),
        });

        var greeting = await scope.ServiceProvider.GetRequiredService<IGreeter>().GreetAsync(User);

        Assert.Equal("3 days", greeting.TimeContext);
        Assert.Contains("It's been 3 days", greeting.Message);
    }

    [Fact]
    public async Task Greeting_FollowsUpOnACompanionCommitment()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectStore>();
        await projects.AddOpenLoopAsync(new OpenLoop
        {
            Id = Guid.NewGuid(),
            UserId = User,
            Owner = "companion",
            Description = "check in about your interview",
            Status = OpenLoopStatus.Open,
            CreatedAt = Now,
        });

        var greeting = await scope.ServiceProvider.GetRequiredService<IGreeter>().GreetAsync(User);

        Assert.Contains(greeting.Openers, o => o.Contains("I said I'd check in about your interview"));
    }

    [Fact]
    public async Task Greeting_DoesNotNagAboutAStaleCommitment()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectStore>();
        await projects.AddOpenLoopAsync(new OpenLoop
        {
            Id = Guid.NewGuid(),
            UserId = User,
            Owner = "companion",
            Description = "check in about your interview",
            Status = OpenLoopStatus.Open,
            CreatedAt = Now.AddDays(-20), // past the surfacing window
        });

        var greeting = await scope.ServiceProvider.GetRequiredService<IGreeter>().GreetAsync(User);

        Assert.DoesNotContain(greeting.Openers, o => o.Contains("I said I'd"));
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

    [Fact]
    public async Task LlmGreeter_FallsBackToDeterministic_WhenTheProviderFails()
    {
        var greeter = new LlmGreeter(
            new StubGreeter(Grounded),
            new ThrowingChatModel(() => new ModelProviderException("the model server is down")),
            NullLogger<LlmGreeter>.Instance);

        var greeting = await greeter.GreetAsync(User);

        Assert.Equal("FALLBACK MESSAGE", greeting.Message);
    }

    [Fact]
    public async Task Rephraser_RewordsTheMessage_ButNeverTouchesTheOpeners()
    {
        // The two-phase contract behind the instant `ready` + `greeting` upgrade frame: only the
        // wording changes; the memory-grounded openers the UI already showed stay exactly as-is.
        IGreetingRephraser rephraser = new LlmGreeter(
            new StubGreeter(Grounded),
            new CannedChatModel("Well hey, you're back! How did the buoy board behave?"),
            NullLogger<LlmGreeter>.Instance);

        var upgraded = await rephraser.RephraseAsync(Grounded);

        Assert.StartsWith("Well hey, you're back!", upgraded.Message);
        Assert.Equal(Grounded.Openers, upgraded.Openers);
    }

    [Fact]
    public async Task Rephraser_StripsLeakedChatTemplateTokens()
    {
        // Seen live with a local model: the greeting arrived ending in a literal "<|im_end|>".
        IGreetingRephraser rephraser = new LlmGreeter(
            new StubGreeter(Grounded),
            new CannedChatModel("Good to see you again!<|im_end|>"),
            NullLogger<LlmGreeter>.Instance);

        var upgraded = await rephraser.RephraseAsync(Grounded);

        Assert.Equal("Good to see you again!", upgraded.Message);
    }

    [Fact]
    public async Task Rephraser_OnProviderFailure_ReturnsTheGroundedGreetingUnchanged()
    {
        // An upgrade is cosmetic: if the model is down, the caller gets the grounded greeting
        // back (same message), which the API layer uses to skip the upgrade frame entirely.
        IGreetingRephraser rephraser = new LlmGreeter(
            new StubGreeter(Grounded),
            new ThrowingChatModel(() => new ModelProviderException("the model server is down")),
            NullLogger<LlmGreeter>.Instance);

        var upgraded = await rephraser.RephraseAsync(Grounded);

        Assert.Equal("FALLBACK MESSAGE", upgraded.Message);
    }

    [Fact]
    public async Task OfflineMocks_RegisterNoRephraser_SoTheInstantGreetingIsFinal()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        Assert.Null(scope.ServiceProvider.GetService<IGreetingRephraser>());
    }

    [Fact]
    public async Task LlmGreeter_GenuineCancellation_Propagates_InsteadOfMasqueradingAsAFailure()
    {
        // A client disconnect (or Ctrl-C) mid-greeting is not a model failure: there is no one
        // left to greet, so the cancellation must unwind normally rather than be swallowed and
        // logged as "LLM greeting failed".
        using var cts = new CancellationTokenSource();
        var greeter = new LlmGreeter(
            new StubGreeter(Grounded),
            new ThrowingChatModel(() =>
            {
                cts.Cancel();
                return new OperationCanceledException(cts.Token);
            }),
            NullLogger<LlmGreeter>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => greeter.GreetAsync(User, cts.Token));
    }

    private sealed class ThrowingChatModel : IChatModel
    {
        private readonly Func<Exception> _make;
        public ThrowingChatModel(Func<Exception> make) => _make = make;

        public Task<ChatCompletion> CompleteAsync(
            string systemPrompt, string userMessage, bool jsonMode = false,
            string? assistantPrefix = null, CancellationToken ct = default)
            => Task.FromException<ChatCompletion>(_make());

        public Task<ChatCompletion> StreamAsync(
            string systemPrompt, string userMessage, IProgress<string> sink,
            string? assistantPrefix = null, CancellationToken ct = default)
            => Task.FromException<ChatCompletion>(_make());
    }

    // ---- in-character greeting (identity + personality reach the greeting prompt) ----

    [Fact]
    public async Task Greeting_IsWrittenInCharacter_WhenIdentityAndPersonaAreKnown()
    {
        var capturing = new CapturingChatModel("Hey you — good to see you back.");
        var personality = new PersonalityService(
            Options.Create(new PersonalityOptions { Default = "flirty" }),
            Options.Create(new IdentityOptions { Name = "Ava", Gender = "female", Pronouns = "she/her" }));
        var profiles = new FakeProfileStore(new UserProfile { UserId = User });

        var greeter = new LlmGreeter(
            new StubGreeter(Grounded), capturing, NullLogger<LlmGreeter>.Instance, personality, profiles);

        await greeter.GreetAsync(User);

        // The identity and personality were injected into the system prompt the greeter sent.
        Assert.Contains("Ava", capturing.LastSystem);
        Assert.Contains("she/her", capturing.LastSystem);
        Assert.Contains("flirt", capturing.LastSystem, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class CapturingChatModel : IChatModel
    {
        private readonly string _reply;
        public string LastSystem { get; private set; } = "";
        public CapturingChatModel(string reply) => _reply = reply;

        public Task<ChatCompletion> CompleteAsync(
            string systemPrompt, string userMessage, bool jsonMode = false,
            string? assistantPrefix = null, CancellationToken ct = default)
        {
            LastSystem = systemPrompt;
            return Task.FromResult(ChatCompletion.FromText(_reply));
        }

        public Task<ChatCompletion> StreamAsync(
            string systemPrompt, string userMessage, IProgress<string> sink,
            string? assistantPrefix = null, CancellationToken ct = default)
        {
            LastSystem = systemPrompt;
            sink.Report(_reply);
            return Task.FromResult(ChatCompletion.FromText(_reply));
        }
    }

    private sealed class FakeProfileStore : IProfileStore
    {
        private readonly UserProfile _profile;
        public FakeProfileStore(UserProfile profile) => _profile = profile;
        public Task<UserProfile> GetOrCreateAsync(string userId, CancellationToken ct = default) => Task.FromResult(_profile);
        public Task SetPersonaAsync(string userId, string? persona, CancellationToken ct = default) { _profile.Persona = persona; return Task.CompletedTask; }
        public Task SetPersonalityPresetAsync(string userId, string? presetName, CancellationToken ct = default) { _profile.PersonalityPreset = presetName; return Task.CompletedTask; }
        public Task SetIdentityAsync(string userId, string? name, string? gender, string? pronouns, CancellationToken ct = default) => Task.CompletedTask;
    }
}
