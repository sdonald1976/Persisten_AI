using Companion.Core.Services;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Spotting a commitment the companion makes in its own reply ("I'll check in tomorrow") so it can
/// follow up later — while ignoring conversational filler ("I'll be honest").
/// </summary>
public class CommitmentDetectorTests
{
    [Theory]
    [InlineData("Sure — I'll check in about your interview tomorrow.", "check in about your interview tomorrow")]
    [InlineData("I'm going to look that up for you.", "look that up for you")]
    [InlineData("No problem. Later I'll email you the notes, promise.", "email you the notes, promise")]
    public void Detect_FindsRealCommitments(string reply, string expected)
        => Assert.Equal(expected, CommitmentDetector.Detect(reply));

    [Theory]
    [InlineData("I'll be honest, that's a tricky one.")]   // filler
    [InlineData("I'll admit I'm not sure.")]                // filler
    [InlineData("Sure, that sounds good to me!")]           // no commitment
    [InlineData("Let me know how it goes.")]                // not first-person "I'll"
    [InlineData("")]
    public void Detect_IgnoresFillerAndNonCommitments(string reply)
        => Assert.Null(CommitmentDetector.Detect(reply));

    /// <summary>
    /// A promise to do something she has no body or life to do with. This is the exact sentence a
    /// real turn produced when the model answered about the user's allotment as though it were her
    /// own; it became an open loop and then opened the next session, so a hallucinated afternoon in
    /// a greenhouse turned into something she believed she had said she would do.
    /// </summary>
    [Theory]
    [InlineData("I'll have some space set aside for experimental varieties, maybe some heirloom tomatoes.")]
    [InlineData("I'm going to mix compost into the base layer of each bed this week.")]
    [InlineData("I'll plant the tomatoes once the weather warms up.")]
    [InlineData("I'll pop down to the plot on Saturday and take a look.")]
    [InlineData("I'll buy the timber tomorrow.")]
    public void Detect_IgnoresPromisesSheCannotKeep(string reply)
        => Assert.Null(CommitmentDetector.Detect(reply));
}
