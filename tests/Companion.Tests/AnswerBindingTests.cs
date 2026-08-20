using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Companion.Infrastructure.Seeding;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Phase 1 of the language-organ plan: when the companion asks a question and the user answers
/// with a short elliptical reply, the SYSTEM binds the answer to the question â€” in the packet,
/// as an authoritative reading, and in the retrieval query. The reference failure is real and
/// verbatim: she asked "What's your favorite kind of magic?", the user said "Additive.", and
/// the chat model reinterpreted the reply as being about the relationship. The question was in
/// the prompt; the missing piece was authority over the interpretation.
/// </summary>
public class AnswerBindingTests
{
    // ---- the pure rule ----

    private static Message Assistant(string content) =>
        new() { Role = MessageRole.Assistant, Content = content };

    private static Message User(string content) =>
        new() { Role = MessageRole.User, Content = content };

    [Fact]
    public void ShortReply_AfterATrailingQuestion_Binds()
    {
        var recent = new[] { User("hey"), Assistant("What's your favorite kind of magic?") };

        var binding = AnswerBindingDetector.Detect(recent, "Additive.");

        Assert.NotNull(binding);
        Assert.Equal("What's your favorite kind of magic?", binding.Question);
        Assert.Equal("Additive.", binding.Answer);
    }

    [Fact]
    public void OnlyTheTrailingQuestion_IsBoundTo_NotOneAskedMidMessage()
    {
        var recent = new[] { Assistant(
            "How was the trip? I spent the afternoon reading about tides. What's your favorite kind of magic?") };

        var binding = AnswerBindingDetector.Detect(recent, "Additive.");

        Assert.NotNull(binding);
        Assert.Equal("What's your favorite kind of magic?", binding.Question);
    }

    [Fact]
    public void AQuestionTalkedPast_DoesNotBind()
    {
        // She asked mid-message and then moved on â€” the question is not left hanging.
        var recent = new[] { Assistant("What's your favorite kind of magic? Anyway, I repotted the ferns.") };
        Assert.Null(AnswerBindingDetector.Detect(recent, "Additive."));
    }

    [Fact]
    public void ALongReply_DoesNotBind()
    {
        var recent = new[] { Assistant("What's your favorite kind of magic?") };
        var essay = "Honestly it depends on the season, but if I had to pick one school of magic " +
                    "over all the others I would probably say something additive.";
        Assert.Null(AnswerBindingDetector.Detect(recent, essay));
    }

    [Fact]
    public void AReplyThatAsksItsOwnQuestion_DoesNotBind()
    {
        var recent = new[] { Assistant("What's your favorite kind of magic?") };
        Assert.Null(AnswerBindingDetector.Detect(recent, "What do you mean by magic?"));
    }

    [Theory]
    [InlineData("lol")]      // the live Phase-2 shadow catch: laughter bound as an answer
    [InlineData("haha")]
    [InlineData("hmm")]
    [InlineData("wow!")]
    public void ABareReaction_DoesNotBind(string reaction)
    {
        var recent = new[] { Assistant("Do you have any favorite recipes you'd like to include?") };
        Assert.Null(AnswerBindingDetector.Detect(recent, reaction));
    }

    [Theory]
    [InlineData("yeah")]
    [InlineData("no")]
    [InlineData("sure")]
    public void APolarAnswer_StillBinds(string answer)
    {
        var recent = new[] { Assistant("Want me to keep track of the seed order?") };
        Assert.NotNull(AnswerBindingDetector.Detect(recent, answer));
    }

    [Fact]
    public void NoTrailingQuestion_OrWrongSpeaker_DoesNotBind()
    {
        Assert.Null(AnswerBindingDetector.Detect(
            new[] { Assistant("I repotted the ferns today.") }, "Additive."));
        Assert.Null(AnswerBindingDetector.Detect(
            new[] { User("Do you like magic?") }, "Additive."));
        Assert.Null(AnswerBindingDetector.Detect(Array.Empty<Message>(), "Additive."));
    }

    // ---- the rule wired through the turn ----

    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private const string UserId = CompanionSeeder.DemoUserId;

    private static async Task<(TestHost host, Guid conversationId)> SessionWithHangingQuestionAsync(string question)
    {
        var host = new TestHost(Now);
        using var scope = host.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CompanionSeeder>().SeedAsync(Now);
        var conversations = scope.ServiceProvider.GetRequiredService<IConversationStore>();
        var conv = await conversations.StartConversationAsync(UserId, "session", "mock", "test");

        // The prior exchange, ending with the companion's question left hanging.
        await conversations.AddMessageAsync(new Message
        {
            Id = Guid.NewGuid(), ConversationId = conv.Id, UserId = UserId,
            Role = MessageRole.User, Content = "Tell me something fun.",
            Timestamp = Now.AddMinutes(-2),
        });
        await conversations.AddMessageAsync(new Message
        {
            Id = Guid.NewGuid(), ConversationId = conv.Id, UserId = UserId,
            Role = MessageRole.Assistant, Content = question,
            Timestamp = Now.AddMinutes(-1),
        });
        return (host, conv.Id);
    }

    [Fact]
    public async Task TheAdditiveTurn_IsBound_InThePacketAndTheDecisions()
    {
        var (host, conversationId) = await SessionWithHangingQuestionAsync(
            "What's your favorite kind of magic?");
        await using var _ = host;

        TurnTrace trace;
        using (var scope = host.CreateScope())
        {
            trace = await scope.ServiceProvider.GetRequiredService<ICompanion>()
                .RespondAsync(UserId, conversationId, "Additive.");
        }

        // The system's reading is IN the packet, next to the transcript, quoting both halves.
        var rendered = trace.Packet.Render();
        Assert.Contains("## Reading this turn", rendered);
        Assert.Contains("\"Additive.\"", rendered);
        Assert.Contains("What's your favorite kind of magic?", rendered);

        // And the decision is on the record with the question as its reason.
        var turn = Assert.Single(host.Services.GetRequiredService<ITurnTraceLog>().Recent(UserId, 1));
        var decision = turn.Decisions.Single(d => d.Stage == "interpretation");
        Assert.Equal("answers-open-question", decision.Verdict);
        Assert.Equal("What's your favorite kind of magic?", decision.Reason);
        Assert.Contains("interpretation", turn.ContextSections);
    }

    [Fact]
    public async Task AnOrdinaryTurn_RecordsUnbound_AndCarriesNoInterpretationSection()
    {
        var (host, conversationId) = await SessionWithHangingQuestionAsync(
            "What's your favorite kind of magic?");
        await using var _ = host;

        TurnTrace trace;
        using (var scope = host.CreateScope())
        {
            trace = await scope.ServiceProvider.GetRequiredService<ICompanion>()
                .RespondAsync(UserId, conversationId,
                    "Never mind that â€” I finally got the irrigation pump running this morning.");
        }

        Assert.DoesNotContain("## Reading this turn", trace.Packet.Render());
        var turn = Assert.Single(host.Services.GetRequiredService<ITurnTraceLog>().Recent(UserId, 1));
        Assert.Equal("new-topic", turn.Decisions.Single(d => d.Stage == "interpretation").Verdict);

        // The hanging question is not lost â€” it is held as working-context state, not a hijack.
        Assert.NotNull(turn.WorkingContext);
        Assert.Contains(turn.WorkingContext!.OpenQuestions,
            q => q.Question == "What's your favorite kind of magic?");
    }
}

