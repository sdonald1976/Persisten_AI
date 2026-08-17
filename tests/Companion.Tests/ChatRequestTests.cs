using System.Text.Json;
using Companion.Core.Abstractions;
using Companion.Infrastructure.Models;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The chat request body only carries sampling params that are actually configured — including the
/// anti-repetition penalties that stop small local models from looping / repeating themselves.
/// </summary>
public class ChatRequestTests
{
    private static readonly object Messages = new[] { new { role = "user", content = "hi" } };

    [Fact]
    public void Penalties_AreSent_WhenConfigured()
    {
        var options = new EndpointOptions { Model = "m", FrequencyPenalty = 0.6, PresencePenalty = 0.3 };

        var body = ChatRequest.Build(options, Messages, stream: false);

        Assert.Equal(0.6, Assert.IsType<double>(body["frequency_penalty"]));
        Assert.Equal(0.3, Assert.IsType<double>(body["presence_penalty"]));
    }

    [Fact]
    public void Penalties_AreOmitted_WhenNotConfigured()
    {
        var options = new EndpointOptions { Model = "m" }; // penalties null → server defaults

        var body = ChatRequest.Build(options, Messages, stream: false);

        Assert.False(body.ContainsKey("frequency_penalty"));
        Assert.False(body.ContainsKey("presence_penalty"));
    }

    [Fact]
    public void ProseAsksForNoParticularFormat()
        => Assert.False(ChatRequest.Build(new EndpointOptions { Model = "m" }, Messages, stream: false)
            .ContainsKey("response_format"));

    [Fact]
    public void PlainJson_AsksForAnObject()
    {
        var body = ChatRequest.Build(
            new EndpointOptions { Model = "m" }, Messages, stream: false, ResponseFormat.Json);

        Assert.Equal("json_object", Property(body["response_format"]!, "type"));
    }

    /// <summary>
    /// The distinction the whole exercise rests on: json_object only asks for JSON and leaves every
    /// field's vocabulary to the model, which is how one fact arrived under two predicates. A
    /// schema is enforced during decoding, so the off-vocabulary value cannot be produced.
    /// </summary>
    [Fact]
    public void ASchema_IsSentAsAConstraint_NotAHint()
    {
        var schema = new Dictionary<string, object?> { ["type"] = "object" };
        var body = ChatRequest.Build(
            new EndpointOptions { Model = "m" }, Messages, stream: false,
            ResponseFormat.FromSchema("memory_candidates", schema));

        var format = body["response_format"]!;
        Assert.Equal("json_schema", Property(format, "type"));

        var wrapper = Property(format, "json_schema")!;
        Assert.Equal("memory_candidates", Property(wrapper, "name"));
        Assert.Same(schema, Property(wrapper, "schema"));
        Assert.Equal(true, Property(wrapper, "strict"));
    }

    /// <summary>The vocabulary has to reach the wire, or none of it constrains anything.</summary>
    [Fact]
    public void TheExtractionSchema_ClosesThePredicateSet()
    {
        var json = JsonSerializer.Serialize(ExtractionSchema.Format.Schema);

        Assert.Contains("\"works_on\"", json);
        Assert.Contains("\"lives_in\"", json);
        Assert.Contains("\"other\"", json);
        Assert.Contains("\"replaces\"", json);
        // The shape the parser expects, and the one small models get right most often.
        Assert.Contains("\"memories\"", json);
    }

    private static object? Property(object source, string name)
        => source.GetType().GetProperty(name)?.GetValue(source);
}
