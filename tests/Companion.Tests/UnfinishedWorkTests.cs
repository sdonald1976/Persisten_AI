using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Unfinished work has to reach the open-loop store, or "what's unfinished?" answers "nothing"
/// while the user is in the middle of something they told you about.
///
/// Measured against a running companion: seventeen turns of ordinary conversation produced zero
/// episodic memories, and since open loops are only opened from episodic candidates, zero loops —
/// including for "I'm rebuilding the greenhouse irrigation before the frost and I've only got the
/// weekend". The extraction prompt now asks for both kinds explicitly; this is the deterministic
/// backstop for when a small local model doesn't oblige.
/// </summary>
public class UnfinishedWorkTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private const string User = "loop-user";

    [Theory]
    [InlineData("I need to finish the export job tonight.", "finish the export job tonight")]
    [InlineData("I've got to get the beds built before the frost.", "get the beds built before the frost")]
    [InlineData("I still need to sort the irrigation timer.", "sort the irrigation timer")]
    [InlineData("I'm supposed to send the slides over by Friday.", "send the slides over by Friday")]
    [InlineData("I have to renew the allotment lease.", "renew the allotment lease")]
    public void OutstandingWork_IsSpotted(string message, string expected)
        => Assert.Equal(expected, UnfinishedWorkDetector.Detect(message));

    [Theory]
    [InlineData("I needed to renew the lease and I already did.")]  // settled
    [InlineData("I finished the export job.")]                       // done
    [InlineData("I'll be honest, that one got away from me.")]       // not an obligation
    [InlineData("I need to go.")]                                    // turn of phrase
    [InlineData("You need to try the coffee there.")]                // not the user's job
    [InlineData("")]
    public void EverythingElse_IsLeftAlone(string message)
        => Assert.Null(UnfinishedWorkDetector.Detect(message));

    private static Message UserMsg(string text) => new()
    {
        Id = Guid.NewGuid(),
        ConversationId = Guid.NewGuid(),
        UserId = User,
        Role = MessageRole.User,
        Content = text,
        Timestamp = Now,
    };

    /// <summary>
    /// The end-to-end shape of the failure: extraction returns only a lasting fact, no episode, and
    /// a loop still has to open.
    /// </summary>
    [Fact]
    public async Task AnObligation_OpensALoop_EvenWithNoEpisodicCandidate()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var message = UserMsg("I need to rebuild the greenhouse irrigation before the frost.");

        var extraction = new MemoryExtractionResult
        {
            Decisions = new[]
            {
                new MemoryDecision
                {
                    Candidate = new MemoryCandidate
                    {
                        Kind = MemoryKind.Semantic,   // the only kind the live extractor produced
                        Subject = "user",
                        Predicate = "works_on",
                        Value = "greenhouse irrigation",
                        Content = "The user is rebuilding the greenhouse irrigation.",
                        Evidence = new[] { new CandidateEvidence(message.Id, "rebuild the greenhouse irrigation") },
                    },
                    Outcome = MemoryDecisionKind.Accepted,
                    Reason = "test",
                    FinalConfidence = 0.9,
                },
            },
        };

        var result = await scope.ServiceProvider.GetRequiredService<IProjectUpdater>()
            .ApplyAsync(User, new[] { message }, extraction, ProjectContext.Empty);

        Assert.Contains(result.Actions, a => a.StartsWith("opened loop:"));

        var loop = Assert.Single(
            await scope.ServiceProvider.GetRequiredService<IProjectStore>()
                .GetOpenLoopsAsync(User, onlyOpen: true));
        Assert.Contains("greenhouse irrigation", loop.Description);
        Assert.Equal("user", loop.Owner);
    }

    /// <summary>Saying it again next turn is the same job, not a second one.</summary>
    [Fact]
    public async Task RepeatingIt_DoesNotStackUpLoops()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var updater = scope.ServiceProvider.GetRequiredService<IProjectUpdater>();

        foreach (var _ in Enumerable.Range(0, 3))
        {
            await updater.ApplyAsync(
                User,
                new[] { UserMsg("I need to rebuild the greenhouse irrigation before the frost.") },
                MemoryExtractionResult.Empty,
                ProjectContext.Empty);
        }

        Assert.Single(
            await scope.ServiceProvider.GetRequiredService<IProjectStore>()
                .GetOpenLoopsAsync(User, onlyOpen: true));
    }
}
