using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>Priority 5: privacy guardrails — a "don't remember" control and a credential guard.</summary>
public class PrivacyTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private const string User = "priv-user";

    private static async Task<Guid> StartAsync(IServiceProvider sp)
        => (await sp.GetRequiredService<IConversationStore>().StartConversationAsync(User, "t", "mock", "test")).Id;

    [Fact]
    public async Task DoNotRememberIntent_IsRecognized()
    {
        var parser = new RuleBasedIntentParser();
        Assert.Equal(IntentKind.PrivacyDoNotRemember, parser.Parse("don't remember this conversation").Kind);
        Assert.Equal(IntentKind.PrivacyDoNotRemember, parser.Parse("forget this conversation").Kind);
        Assert.Equal(IntentKind.PrivacyDoNotRemember, parser.Parse("let's keep this off the record").Kind);
        // A normal forget-a-memory request is NOT privacy.
        Assert.NotEqual(IntentKind.PrivacyDoNotRemember, parser.Parse("forget the low-carb thing").Kind);
    }

    [Fact]
    public async Task PrivateConversation_CreatesNoDurableMemory_ButStillReplies()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var conv = await StartAsync(sp);
        var agent = sp.GetRequiredService<IAgent>();

        // Toggle privacy for this conversation.
        var ack = await agent.HandleAsync(User, conv, "don't remember this conversation");
        Assert.Equal(IntentKind.PrivacyDoNotRemember, ack.Intent);

        // A subsequent turn replies but must not create durable memory. "I love hiking" is a phrase
        // the rule-based extractor DOES capture normally (see the control test), so 0 memories here
        // proves the privacy skip, not merely that nothing was extractable.
        var trace = await agent.HandleAsync(User, conv, "I love hiking in the mountains.");
        Assert.Equal(AgentReplyKind.Chat, trace.Kind);
        Assert.False(string.IsNullOrWhiteSpace(trace.Text));         // still answered
        Assert.Equal(0, trace.Trace!.Extraction.Accepted + trace.Trace.Extraction.Merged);

        var memories = await sp.GetRequiredService<IMemoryStore>().GetRetrievableMemoriesAsync(User);
        Assert.Empty(memories);

        // But the raw messages were stored (needed for in-session context).
        var msgs = await sp.GetRequiredService<IConversationStore>().GetRecentMessagesAsync(conv, User, 20);
        Assert.Contains(msgs, m => m.Role == MessageRole.User && m.Content.Contains("hiking"));
    }

    [Fact]
    public async Task NonPrivateConversation_StillRemembers()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var conv = await StartAsync(sp);

        await sp.GetRequiredService<ICompanion>()
            .RespondAsync(User, conv, "I love hiking in the mountains.");

        // Control: without the privacy toggle, the rule-based extractor does capture memory.
        var memories = await sp.GetRequiredService<IMemoryStore>().GetRetrievableMemoriesAsync(User);
        Assert.NotEmpty(memories);
    }

    [Theory]
    [InlineData("My OpenAI key is sk-abcdefghijklmnopqrstuvwxyz1234")]
    [InlineData("password: hunter2secret")]
    [InlineData("token=ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789")]
    public void SecretDetector_FlagsObviousCredentials(string text)
        => Assert.True(SecretDetector.LooksLikeSecret(text));

    [Theory]
    [InlineData("I like hiking in the mountains.")]
    [InlineData("My favorite color is teal.")]
    public void SecretDetector_LeavesOrdinaryProseAlone(string text)
        => Assert.False(SecretDetector.LooksLikeSecret(text));

    [Fact]
    public async Task Pipeline_RejectsCredential_Candidates()
    {
        await using var host = new TestHost(Now, o => o.MinAcceptConfidence = 0.0); // accept anything not otherwise rejected
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var conv = await StartAsync(sp);
        var conversations = sp.GetRequiredService<IConversationStore>();

        var msg = new Message
        {
            Id = Guid.NewGuid(), ConversationId = conv, UserId = User, Role = MessageRole.User,
            Content = "Store this: my api_key = sk-abcdefghijklmnopqrstuvwxyz1234", Timestamp = Now,
        };
        await conversations.AddMessageAsync(msg);

        var candidate = new MemoryCandidate
        {
            Kind = MemoryKind.Semantic,
            Subject = "user", Predicate = "secret", Value = "sk-abcdefghijklmnopqrstuvwxyz1234",
            Content = "The user's api_key = sk-abcdefghijklmnopqrstuvwxyz1234",
            ProposedConfidence = 0.9, Importance = 0.5,
            Evidence = new List<CandidateEvidence> { new(msg.Id, "api_key = sk-abcdefghijklmnopqrstuvwxyz1234") },
        };

        var result = await new StubbedPipelineHost(sp, candidate).ProcessAsync(User, new[] { msg });

        Assert.Equal(0, result.Accepted + result.Merged);
        Assert.Contains(result.Decisions, d => d.Outcome == MemoryDecisionKind.Rejected
            && d.Reason.Contains("credential", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(await sp.GetRequiredService<IMemoryStore>().GetRetrievableMemoriesAsync(User));
    }

    /// <summary>Runs the real pipeline but with a stub extractor that proposes a fixed candidate.</summary>
    private sealed class StubbedPipelineHost
    {
        private readonly IMemoryPipeline _pipeline;
        public StubbedPipelineHost(IServiceProvider sp, MemoryCandidate candidate)
        {
            _pipeline = new MemoryPipeline(
                new StubExtractor(candidate),
                sp.GetRequiredService<IMemoryStore>(),
                sp.GetRequiredService<IMemoryCurator>(),
                sp.GetRequiredService<IEmbeddingModel>(),
                sp.GetRequiredService<IProfileStore>(),
                sp.GetRequiredService<IPersonalityService>(),
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Companion.Core.CompanionOptions>>(),
                sp.GetRequiredService<TimeProvider>(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<MemoryPipeline>.Instance);
        }
        public Task<MemoryExtractionResult> ProcessAsync(string userId, IReadOnlyList<Message> exchange)
            => _pipeline.ProcessAsync(userId, exchange);
    }
}
