using Companion.Core.Domain;
using Companion.Infrastructure.Models;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Companion.Tests;

public class LlmMemoryExtractorTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static Message UserMsg(string text) => new()
    {
        Id = Guid.NewGuid(),
        ConversationId = Guid.NewGuid(),
        UserId = "u",
        Role = MessageRole.User,
        Content = text,
        Timestamp = Now,
    };

    [Fact]
    public async Task ParsesJsonArray_AndResolvesEvidenceToTheCitingMessage()
    {
        var msg = UserMsg("I switched from the Raspberry Pi to a Jetson Nano.");
        var json = """
        Sure, here you go:
        [
          {"kind":"semantic","subject":"user","predicate":"uses","value":"a Jetson Nano",
           "content":"The user uses a Jetson Nano.","confidence":0.8,
           "excerpt":"switched from the Raspberry Pi to a Jetson Nano"}
        ]
        """;
        var extractor = new LlmMemoryExtractor(new CannedChatModel(json), NullLogger<LlmMemoryExtractor>.Instance);

        var candidates = await extractor.ExtractAsync("u", new[] { msg });

        var c = Assert.Single(candidates);
        Assert.Equal(MemoryKind.Semantic, c.Kind);
        Assert.Equal("a Jetson Nano", c.Value);
        Assert.Equal(0.8, c.ProposedConfidence, precision: 5);
        Assert.Equal(msg.Id, Assert.Single(c.Evidence).MessageId); // excerpt matched the message
    }

    [Fact]
    public async Task UnparseableOutput_YieldsNoCandidates()
    {
        var msg = UserMsg("hello");
        var extractor = new LlmMemoryExtractor(
            new CannedChatModel("I couldn't find anything useful."), NullLogger<LlmMemoryExtractor>.Instance);

        Assert.Empty(await extractor.ExtractAsync("u", new[] { msg }));
    }

    [Fact]
    public async Task EmptyArray_YieldsNoCandidates()
    {
        var msg = UserMsg("hello");
        var extractor = new LlmMemoryExtractor(new CannedChatModel("[]"), NullLogger<LlmMemoryExtractor>.Instance);

        Assert.Empty(await extractor.ExtractAsync("u", new[] { msg }));
    }

    [Fact]
    public async Task ParsesEnvelopeObject_WithMemoriesArray()
    {
        var msg = UserMsg("I use a Jetson Nano.");
        var json = """{"memories":[{"kind":"semantic","content":"The user uses a Jetson Nano.","excerpt":"Jetson Nano"}]}""";
        var extractor = new LlmMemoryExtractor(new CannedChatModel(json), NullLogger<LlmMemoryExtractor>.Instance);

        var c = Assert.Single(await extractor.ExtractAsync("u", new[] { msg }));
        Assert.Equal("The user uses a Jetson Nano.", c.Content);
    }

    [Fact]
    public async Task ParsesMarkdownFencedArray()
    {
        var msg = UserMsg("I use a Jetson Nano.");
        var json = "```json\n[{\"kind\":\"semantic\",\"content\":\"The user uses a Jetson Nano.\",\"excerpt\":\"Jetson Nano\"}]\n```";
        var extractor = new LlmMemoryExtractor(new CannedChatModel(json), NullLogger<LlmMemoryExtractor>.Instance);

        Assert.Single(await extractor.ExtractAsync("u", new[] { msg }));
    }

    [Fact]
    public async Task ABracketInsideAStringValue_DoesNotBreakParsing()
    {
        // The naive "first [ to last ]" slice would mis-slice on the ] inside the content string.
        var msg = UserMsg("I labeled it list[0].");
        var json = """[{"kind":"semantic","content":"The user labeled it list[0].","excerpt":"list[0]"}] trailing prose ]""";
        var extractor = new LlmMemoryExtractor(new CannedChatModel(json), NullLogger<LlmMemoryExtractor>.Instance);

        var c = Assert.Single(await extractor.ExtractAsync("u", new[] { msg }));
        Assert.Equal("The user labeled it list[0].", c.Content);
    }

    [Fact]
    public async Task SchemaViolation_NonArrayJson_YieldsNoCandidates()
    {
        var msg = UserMsg("hello");
        var extractor = new LlmMemoryExtractor(
            new CannedChatModel("""{"unexpected":"shape","value":42}"""), NullLogger<LlmMemoryExtractor>.Instance);

        Assert.Empty(await extractor.ExtractAsync("u", new[] { msg }));
    }
}
