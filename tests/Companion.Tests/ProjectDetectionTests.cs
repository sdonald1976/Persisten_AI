using Companion.Core.Domain;
using Companion.Infrastructure.Models;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// A project only comes into existence if an extracted memory carries its name in
/// <see cref="MemoryCandidate.RelatedProject"/> — nothing else in the pipeline reads the user's
/// words for it. The offline extractor never filled that field, so the whole projects feature was
/// unreachable without an LLM. These cover the naming phrasings it now recognizes, and (more
/// importantly) the ones it must keep refusing: inventing a project is worse than missing one.
/// </summary>
public class ProjectDetectionTests
{
    private static IReadOnlyList<MemoryCandidate> Extract(string text)
    {
        var message = new Message
        {
            Id = Guid.NewGuid(),
            ConversationId = Guid.NewGuid(),
            Role = MessageRole.User,
            Content = text,
            Timestamp = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
        };
        return new RuleBasedMemoryExtractor().ExtractAsync("u", new[] { message }).GetAwaiter().GetResult();
    }

    private static string? ProjectOf(string text) =>
        Extract(text).Select(c => c.RelatedProject).FirstOrDefault(p => p is not null);

    [Theory]
    [InlineData("I'm working on a project called Halyard.", "Halyard")]
    [InlineData("I am working on a project named Halyard.", "Halyard")]
    [InlineData("I'm working on Halyard.", "Halyard")]
    [InlineData("We're working on Halyard.", "Halyard")]
    public void NamedProject_TagsTheMemory(string text, string expected)
        => Assert.Equal(expected, ProjectOf(text));

    [Fact]
    public void NameFollowedByADescription_KeepsOnlyTheName()
    {
        // The spaced dash separates the name from the gloss; an unspaced one would be part of it.
        Assert.Equal("Halyard", ProjectOf("I'm working on a project called Halyard - it's a C# service."));
        Assert.Equal("Halyard", ProjectOf("I'm working on a project called Halyard — it's a C# service."));
        Assert.Equal("open-loop-ui", ProjectOf("I'm working on a project called open-loop-ui."));
    }

    [Fact]
    public void EveryMemoryFromTheMessage_CarriesTheProject()
    {
        // The project is named once, in the first sentence; the second sentence is about the same
        // work and has to inherit it or the follow-up memory is orphaned.
        var candidates = Extract("I'm working on a project called Halyard. I need to finish the export job.");

        Assert.True(candidates.Count >= 2);
        Assert.All(candidates, c => Assert.Equal("Halyard", c.RelatedProject));
    }

    [Theory]
    [InlineData("I'm working on it.")]                          // pronoun, not a name
    [InlineData("I'm working on the export job.")]              // a task, not a named project
    [InlineData("I'm working on my project.")]                  // generic
    [InlineData("I need to finish the export job.")]            // no naming phrase at all
    [InlineData("I prefer dark roast coffee.")]
    public void WithoutAName_NoProjectIsInvented(string text)
        => Assert.Null(ProjectOf(text));
}
