using Companion.Infrastructure.Models;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The semantic completion check parses a small model's one-word verdict and, crucially, fails
/// closed — anything that isn't an unambiguous "CONTINUE" is treated as complete, so a vague or
/// broken judge can never trap the user in runaway continuation.
/// </summary>
public class CompletionJudgeTests
{
    private static LlmCompletionJudge Judge(string cannedVerdict) =>
        new(new CannedChatModel(cannedVerdict), NullLogger<LlmCompletionJudge>.Instance);

    private static Task<bool> Ask(string verdict) =>
        Judge(verdict).IsCompleteAsync("write me a story", "Once upon a time, the story was only half told");

    [Fact]
    public async Task Continue_MeansNotComplete()
        => Assert.False(await Ask("CONTINUE"));

    [Fact]
    public async Task Complete_MeansComplete()
        => Assert.True(await Ask("COMPLETE"));

    [Fact]
    public async Task VagueAnswer_FailsClosed_AsComplete()
        => Assert.True(await Ask("Hmm, it's hard to say really."));

    [Fact]
    public async Task AmbiguousAnswerMentioningBoth_FailsClosed_AsComplete()
        => Assert.True(await Ask("It's not COMPLETE yet, it should CONTINUE."));

    [Fact]
    public async Task EmptyAnswer_FailsClosed_AsComplete()
        => Assert.True(await Ask(""));
}
