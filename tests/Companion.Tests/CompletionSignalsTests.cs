using Companion.Infrastructure.Models;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The deterministic, topic-free signals behind auto-continuation: whether the user asked for a
/// produced artifact, and whether a reply looks structurally cut off. No model, no topic list.
/// </summary>
public class CompletionSignalsTests
{
    [Theory]
    [InlineData("write me a story about a lighthouse")]
    [InlineData("Draft an email to my landlord")]
    [InlineData("list all the steps to deploy")]
    [InlineData("explain how TLS works in detail")]
    [InlineData("generate a plan for the week")]
    [InlineData("keep going")]
    [InlineData("continue the story")]
    [InlineData("walk me through the setup step by step")]
    public void DeliverableRequests_AreRecognized(string message)
        => Assert.True(CompletionSignals.IsDeliverableRequest(message));

    [Theory]
    [InlineData("how are things treating you lately?")]
    [InlineData("hi")]
    [InlineData("what do you think about that?")]
    [InlineData("thanks, that helps")]
    [InlineData("good morning")]
    public void Conversation_IsNotADeliverableRequest(string message)
        => Assert.False(CompletionSignals.IsDeliverableRequest(message));

    [Theory]
    [InlineData("The crew set out across the harbor and then")]   // mid-sentence
    [InlineData("Here are the steps:")]                            // dangling colon
    [InlineData("Sure! Here's the code:\n```python\nprint(1)")]   // unclosed code fence
    [InlineData("That's a good start. Want me to continue?")]      // explicit solicitation
    [InlineData("...to be continued")]                            // to be continued
    public void UnfinishedReplies_AreDetected(string reply)
        => Assert.True(CompletionSignals.LooksUnfinished(reply));

    [Theory]
    [InlineData("Here is the finished answer, complete and whole.")]
    [InlineData("Done!")]
    [InlineData("The final line ended with a question mark, right?")]
    [InlineData("Sure! Here's the code:\n```python\nprint(1)\n```")] // closed fence
    [InlineData("\"And that,\" she said, \"is the end.\"")]           // closing quote
    public void CompleteReplies_AreNotFlagged(string reply)
        => Assert.False(CompletionSignals.LooksUnfinished(reply));

    [Fact]
    public void EmptyReply_IsNotFlaggedAsUnfinished()
        => Assert.False(CompletionSignals.LooksUnfinished("   "));
}
