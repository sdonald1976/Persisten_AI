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
}
