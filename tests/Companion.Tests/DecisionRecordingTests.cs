using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The Decisions supply line: the deterministic detector, and ProjectUpdater recording an
/// evidence-backed decision against the turn's project (deduped on re-statement).
/// </summary>
public class DecisionRecordingTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private const string User = "decision-user";

    [Theory]
    [InlineData("We decided to use SQLite for storage.", "use SQLite for storage")]
    [InlineData("I've settled on Kokoro for her voice", "Kokoro for her voice")]
    [InlineData("I'm going with the Jetson for detection.", "the Jetson for detection")]
    [InlineData("Let's stick with ntfy for pushes", "ntfy for pushes")]
    [InlineData("we opted for a local-first design, by the way", "a local-first design, by the way")]
    public void Detector_SpotsClearDecisions(string text, string expected)
        => Assert.Equal(expected, DecisionDetector.Detect(text));

    [Theory]
    [InlineData("Let's go with the flow tonight")]         // idiom, not a decision
    [InlineData("I went with it")]                          // no real content
    [InlineData("How was your day?")]                       // plain chat
    [InlineData("She decided to leave early")]              // third person — not the user's decision
    public void Detector_IgnoresNonDecisions(string text)
        => Assert.Null(DecisionDetector.Detect(text));

    [Fact]
    public async Task DecisionInTurn_IsRecordedAgainstTheProject_WithEventAndEvidence()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IProjectStore>();

        var message = new Message
        {
            Id = Guid.NewGuid(), UserId = User, ConversationId = Guid.NewGuid(),
            Role = MessageRole.User,
            Content = "For the greenhouse we decided to use capacitive sensors instead of resistive.",
            Timestamp = Now,
        };
        var extraction = new MemoryExtractionResult
        {
            Decisions = new[]
            {
                new MemoryDecision
                {
                    Candidate = new MemoryCandidate
                    {
                        Kind = MemoryKind.Episodic,
                        Content = "Chose capacitive sensors for the greenhouse",
                        RelatedProject = "Greenhouse automation",
                        Evidence = new[] { new CandidateEvidence(message.Id, message.Content) },
                    },
                    Outcome = MemoryDecisionKind.Accepted, Reason = "test", FinalConfidence = 0.8,
                },
            },
        };

        var updater = scope.ServiceProvider.GetRequiredService<IProjectUpdater>();
        var result = await updater.ApplyAsync(User, new[] { message }, extraction, ProjectContext.Empty);

        Assert.Contains(result.Actions, a => a.StartsWith("recorded decision:"));
        var project = Assert.Single(await store.GetProjectsAsync(User));
        var decision = Assert.Single(await store.GetDecisionsAsync(User, project.Id));
        Assert.Equal("use capacitive sensors instead of resistive", decision.Statement);
        Assert.Equal(message.Id, decision.SourceMessageId);
        var events = await store.GetRecentEventsAsync(User, project.Id, 10);
        Assert.Contains(events, e => e.Kind == ProjectEventKind.DecisionMade);

        // Re-stating the same decision on a later turn records nothing new.
        await updater.ApplyAsync(User, new[] { message }, extraction, ProjectContext.Empty);
        Assert.Single(await store.GetDecisionsAsync(User, project.Id));
    }

    [Fact]
    public async Task DecisionWithoutAnyProject_IsNotRecorded()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();

        var message = new Message
        {
            Id = Guid.NewGuid(), UserId = User, ConversationId = Guid.NewGuid(),
            Role = MessageRole.User,
            Content = "We decided to repaint the kitchen next spring.",
            Timestamp = Now,
        };

        // No accepted memory names a project and none resolved — nowhere to hang the decision.
        await scope.ServiceProvider.GetRequiredService<IProjectUpdater>().ApplyAsync(
            User, new[] { message }, MemoryExtractionResult.Empty, ProjectContext.Empty);

        var store = scope.ServiceProvider.GetRequiredService<IProjectStore>();
        Assert.Empty(await store.GetProjectsAsync(User));
    }
}
