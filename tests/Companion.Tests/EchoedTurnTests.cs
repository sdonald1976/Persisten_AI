using Companion.Core.Domain;
using Companion.Core.Services;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// She does not reproduce an earlier reply before writing the new one.
///
/// From a real conversation: asked "can you remember that I have a dog named Precious?", she emitted
/// all 679 characters of her opening reply from three turns earlier — word for word, down to its
/// closing question — then a blank line, then the actual answer. Her transcript is in the prompt,
/// and a model shown a document will sometimes continue it rather than reply to it.
///
/// Nothing caught it. Stop sequences and the fabricated-turn trim key on a role marker and there
/// was none; the structural trim only ever looks at the END. It arrived bare, and at the front.
///
/// The boundary is the delicate part: repeating a phrase is voice, repeating a whole turn is a
/// fault, and only the second may be touched.
/// </summary>
public class EchoedTurnTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static Message Said(MessageRole role, string text, int minute = 0) => new()
    {
        Id = Guid.NewGuid(),
        ConversationId = Guid.Empty,
        UserId = "u",
        Role = role,
        Content = text,
        Timestamp = Now.AddMinutes(minute),
    };

    /// <summary>Long enough to be a real turn rather than a greeting.</summary>
    private const string EarlierReply =
        "Hello again! It's nice to reconnect with you after our earlier hello. As for how I'm doing, " +
        "this quiet time of the morning suits me just fine. I've had a chance to think and reflect in " +
        "peace before we spoke. What brings this hello today? Anything specific on your mind, or would " +
        "you rather start with a general catch-up?";

    private static IReadOnlyList<Message> Transcript() => new[]
    {
        Said(MessageRole.User, "Hey, how's it going?", 1),
        Said(MessageRole.Assistant, EarlierReply, 2),
        Said(MessageRole.User, "Fair enough.", 3),
        Said(MessageRole.Assistant, "That makes sense to me.", 4),
    };

    [Fact]
    public void AWholeEarlierTurnRepeatedUpFrontIsRemoved()
    {
        const string actualAnswer = "I remember Precious, your dog. What's she been up to?";
        var reply = EarlierReply + "\n\n" + actualAnswer;

        Assert.Equal(actualAnswer, EchoedTurnFilter.Strip(reply, Transcript()));
    }

    [Fact]
    public void TheEchoIsCaughtEvenWhenTheLineBreaksMoved()
    {
        // The observed copy was not byte-identical — its paragraph breaks had been reflowed. A
        // repeat that survived on a change of line wrapping would be no less a repeat.
        const string actualAnswer = "Anyway — yes, I remember Precious.";
        var reflowed = EarlierReply.Replace(". ", ".\n");
        var reply = reflowed + "\n\n" + actualAnswer;

        Assert.Equal(actualAnswer, EchoedTurnFilter.Strip(reply, Transcript()));
    }

    // ---- what must never be touched ----

    [Fact]
    public void AnOrdinaryReplyIsLeftExactlyAsWritten()
    {
        const string reply = "That's lovely. Border collies do pick up a routine fast, don't they?";
        Assert.Equal(reply, EchoedTurnFilter.Strip(reply, Transcript()));
    }

    [Fact]
    public void OpeningTheSameWayIsVoice_NotAFault()
    {
        // She greets people similarly. A shared opening sentence is style; only a whole turn
        // reproduced counts.
        const string reply = "Hello again! It's nice to reconnect. How did the deck turn out in the end?";
        Assert.Equal(reply, EchoedTurnFilter.Strip(reply, Transcript()));
    }

    [Fact]
    public void AShortEarlierTurnIsNeverMatched()
    {
        // "That makes sense to me." is in the transcript, but a phrase that short recurring is
        // ordinary speech, and cutting on it would butcher real replies.
        const string reply = "That makes sense to me. Shall we pick up where we left off?";
        Assert.Equal(reply, EchoedTurnFilter.Strip(reply, Transcript()));
    }

    [Fact]
    public void AReplyThatIsNothingButAnEchoIsKept()
    {
        // Returning empty would hide the failure. Better it is visible and reported.
        Assert.Equal(EarlierReply, EchoedTurnFilter.Strip(EarlierReply, Transcript()));
    }

    [Fact]
    public void HerOwnWordsAreNeverCutFromTheMiddleOrEnd()
    {
        // Only a leading echo is removed; the same text later in a reply is her quoting herself,
        // which is a thing people do.
        var reply = "Here's what I said before:\n\n" + EarlierReply;
        Assert.Equal(reply, EchoedTurnFilter.Strip(reply, Transcript()));
    }

    [Fact]
    public void WithNoTranscriptNothingHappens()
        => Assert.Equal(EarlierReply, EchoedTurnFilter.Strip(EarlierReply, Array.Empty<Message>()));

    [Fact]
    public void TheUsersOwnWordsAreNeverTreatedAsAnEcho()
    {
        // Only her turns are compared. Echoing the user is a different behaviour — sometimes a
        // deliberate one — and not this filter's business.
        var transcript = new[] { Said(MessageRole.User, EarlierReply, 1) };
        var reply = EarlierReply + "\n\nAnd that's my answer.";

        Assert.Equal(reply, EchoedTurnFilter.Strip(reply, transcript));
    }
}
