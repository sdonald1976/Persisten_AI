using Companion.Core.Services;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Telling the difference between words the user said and facts the user asserted.
///
/// Every sentence here is taken from, or modelled on, the conversation that found the bug: asked
/// "Did I ever tell you what timber I bought for the beds?", she answered "I don't recall you
/// mentioning that" and stored "The user bought timber for the raised beds" in the same turn. The
/// excerpt was real and the citation honest — the question simply contains its own presupposition.
/// </summary>
public class AssertionGuardTests
{
    [Theory]
    [InlineData("Did I ever tell you what timber I bought for the beds?", "timber I bought for the beds")]
    [InlineData("What am I building at the allotment again?", "building at the allotment")]
    [InlineData("do you remember where I said I grew up", "where I said I grew up")]
    [InlineData("Have I mentioned that I play the cello?", "I play the cello")]
    [InlineData("Was I the one who suggested the Jetson?", "I ... suggested the Jetson")]
    [InlineData("If I bought cedar for the beds, would it last longer?", "I bought cedar for the beds")]
    [InlineData("Wondering if I ever paid that invoice.", "I ever paid that invoice")]
    public void Questions_And_Suppositions_AreNotAssertions(string message, string excerpt)
        => Assert.False(AssertionGuard.IsAsserted(excerpt, message));

    [Theory]
    [InlineData("I bought cedar for the beds last weekend.", "bought cedar for the beds")]
    [InlineData("My name is Scott.", "My name is Scott")]
    [InlineData("I grew up in a town called Thistlemere.", "I grew up in a town called Thistlemere")]
    [InlineData("Also I don't like coriander. Never have.", "I don't like coriander")]
    public void PlainStatements_AreAssertions(string message, string excerpt)
        => Assert.True(AssertionGuard.IsAsserted(excerpt, message));

    /// <summary>
    /// The common shape in real chat: a fact and a question in one message. The fact survives; only
    /// the presupposition inside the question is refused.
    /// </summary>
    [Fact]
    public void AFactBesideAQuestion_Survives()
    {
        const string message = "I bought cedar for the beds. Did I tell you I hired a mitre saw too?";

        Assert.True(AssertionGuard.IsAsserted("I bought cedar for the beds", message));
        Assert.False(AssertionGuard.IsAsserted("I hired a mitre saw", message));
    }

    /// <summary>
    /// A claim made on the way to asking something else. The sentence is a question, but the
    /// leading clause is not, and dropping the fact in it would be the guard overreaching.
    /// </summary>
    [Theory]
    [InlineData("Since I've moved to Cambridge, do you know any good cafés?", "I've moved to Cambridge")]
    [InlineData("I'm rebuilding the irrigation this weekend, any idea where to start?", "I'm rebuilding the irrigation")]
    public void AClaimMadeOnTheWayToAQuestion_StillCounts(string message, string excerpt)
        => Assert.True(AssertionGuard.IsAsserted(excerpt, message));

    /// <summary>But the presupposition inside the question itself still does not.</summary>
    [Fact]
    public void TheQuestionsOwnPresupposition_StillDoesNot()
        => Assert.False(AssertionGuard.IsAsserted(
            "what timber I bought",
            "Since I've moved to Cambridge, did I say what timber I bought?"));

    /// <summary>
    /// The guard refuses what it can show is a non-assertion; it is not a second opinion on
    /// everything. Text it cannot place stays admissible, so a paraphrasing extractor doesn't
    /// quietly lose genuine memories.
    /// </summary>
    [Theory]
    [InlineData(null, "I bought cedar.")]
    [InlineData("bought cedar", null)]
    [InlineData("", "")]
    public void UnknownOrUnplaceableText_IsAdmitted(string? excerpt, string? message)
        => Assert.True(AssertionGuard.IsAsserted(excerpt, message));
}
