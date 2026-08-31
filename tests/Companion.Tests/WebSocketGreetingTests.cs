using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Companion.Tests.Fixtures;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The WebSocket session opener: the `ready` frame carries the instant deterministic greeting.
/// A model-phrased `greeting` upgrade frame follows only when a real model is configured — with
/// the offline mocks no rephraser is registered, so no upgrade frame may ever appear.
/// </summary>
[Collection(ApiCollection.Name)]
public class WebSocketGreetingTests
{
    private readonly CompanionApiFactory _factory;
    public WebSocketGreetingTests(CompanionApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Connect_GetsAnInstantReadyGreeting_AndTheGreetingFrame_OnMocks()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var client = _factory.Server.CreateWebSocketClient();
        var uri = new UriBuilder(_factory.Server.BaseAddress)
        {
            Scheme = "ws",
            Path = "/ws",
            Query = $"access_token={CompanionApiFactory.Token}",
        }.Uri;
        using var socket = await client.ConnectAsync(uri, timeout.Token);

        // The very first frame is `ready` with a complete greeting — nothing blocks on a model.
        var ready = await ReceiveJsonAsync(socket, timeout.Token);
        Assert.Equal("ready", ready.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(ready.GetProperty("message").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(ready.GetProperty("conversationId").GetString()));

        // Run a full turn. Offline there is no rephraser, so a `greeting` frame must never appear.
        await SendJsonAsync(socket, new { type = "chat", text = "tell me something interesting" }, timeout.Token);
        var types = new List<string>();
        string frameType;
        do
        {
            var frame = await ReceiveJsonAsync(socket, timeout.Token);
            frameType = frame.GetProperty("type").GetString()!;
            types.Add(frameType);
        }
        while (frameType != "reply");

        // The face displays only the 'greeting' frame (the 'ready' message is no longer
        // rendered), so the server must ALWAYS deliver one - on mocks it carries the
        // deterministic grounded message rather than being skipped.
        Assert.Contains("greeting", types);
    }

    private static async Task SendJsonAsync(WebSocket socket, object payload, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    private static async Task<JsonElement> ReceiveJsonAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, ct);
            Assert.NotEqual(WebSocketMessageType.Close, result.MessageType);
            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(ms.ToArray()));
        return doc.RootElement.Clone();
    }
}
