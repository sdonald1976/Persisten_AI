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

    /// <summary>
    /// Someone telling you what they are working on has not asked you to do it. The verb list saw
    /// "writing" and put an ordinary conversational turn on the deliverable path, where the
    /// completion judge then chased it through five generation rounds and 277 seconds, returning
    /// four complete answers with four sign-offs concatenated into one reply.
    /// </summary>
    [Theory]
    [InlineData("Separate thing entirely: I'm writing a talk on soil chemistry for the county show in October.")]
    [InlineData("I built the raised beds myself last spring.")]
    [InlineData("We're planning a trip to Skye.")]
    [InlineData("I've been drafting the eulogy all week.")]
    [InlineData("I'm working on the irrigation this weekend.")]
    public void DescribingYourOwnWork_IsNotARequestForIt(string message)
        => Assert.False(CompletionSignals.IsDeliverableRequest(message));

    /// <summary>But asking for help with your own work still is.</summary>
    [Theory]
    [InlineData("I'm writing a talk on soil chemistry — can you draft an outline?")]
    [InlineData("I'm planning a trip to Skye, please write me an itinerary.")]
    [InlineData("I've been drafting the eulogy — help me finish it.")]
    public void AskingForHelpWithIt_StillIs(string message)
        => Assert.True(CompletionSignals.IsDeliverableRequest(message));

    /// <summary>
    /// A reply that has already said goodbye is finished, whatever a small judge model says. This
    /// overrules the judge, which is wrong in exactly one direction: shown a complete answer and
    /// asked whether it is complete, it says no, and the next round writes a whole new answer.
    /// </summary>
    [Theory]
    [InlineData("…so start with a hook. Let me know if you'd like more input on any of it.")]
    [InlineData("Hope this helps!")]
    [InlineData("Best of luck with the talk — you've got this.")]
    [InlineData("I hope these additional ideas are helpful as you refine your presentation. Happy planning!")]
    [InlineData("Feel free to ask if you have any other questions.")]
    public void ARepliesOwnSignOff_MarksItFinished(string reply)
        => Assert.True(CompletionSignals.LooksFinished(reply));

    [Theory]
    [InlineData("Start with a hook, then move through the basics of soil composition and")]
    [InlineData("The three things to cover are:")]
    [InlineData("")]
    public void AReplyStillInFlight_IsNotFinished(string reply)
        => Assert.False(CompletionSignals.LooksFinished(reply));

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
    [InlineData("And the moral is: **never skip backups**")]         // markdown bold close
    [InlineData("| step | done |\n| ---- | ---- |\n| tests | yes |")] // table row
    [InlineData("Can't wait to hear how it goes 😉")]                 // emoji sign-off
    [InlineData("Consider it done ❤")]                                // non-surrogate symbol
    public void CompleteReplies_AreNotFlagged(string reply)
        => Assert.False(CompletionSignals.LooksUnfinished(reply));

    [Fact]
    public void EmptyReply_IsNotFlaggedAsUnfinished()
        => Assert.False(CompletionSignals.LooksUnfinished("   "));
}
