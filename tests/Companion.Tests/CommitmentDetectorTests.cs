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
}
