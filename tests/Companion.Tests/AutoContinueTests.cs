using System.Net;
using Companion.Infrastructure.Models;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Completion detection + auto-continuation: when the server says a reply was cut off by the
/// token limit (<c>finish_reason: "length"</c>), the adapter keeps asking the model to continue
/// until it finishes naturally (<c>"stop"</c>) — so a long task completes in one turn instead of
/// the user typing "keep going".
/// </summary>
public class AutoContinueTests
{
    private static string ChatBody(string content, string finishReason) =>
        $$"""{"choices":[{"message":{"content":"{{content}}"},"finish_reason":"{{finishReason}}"}]}""";

    private static string SseBody(string content, string finishReason) =>
        $"data: {{\"choices\":[{{\"delta\":{{\"content\":\"{content}\"}}}}]}}\n" +
        $"data: {{\"choices\":[{{\"delta\":{{}},\"finish_reason\":\"{finishReason}\"}}]}}\n" +
        "data: [DONE]\n";

    private static OpenAiCompatibleChatModel Chat(StubHttpMessageHandler handler, EndpointOptions opts)
    {
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:1234/v1/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        return new OpenAiCompatibleChatModel(opts, () => http, NullLogger<OpenAiCompatibleChatModel>.Instance);
    }

    private static EndpointOptions Options(bool autoContinue = true, int maxContinuations = 6)
        => new() { Model = "m", MaxRetries = 0, AutoContinue = autoContinue, MaxContinuations = maxContinuations };

    [Fact]
    public async Task Complete_AutoContinues_UntilTheModelFinishes()
    {
        var handler = new StubHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, ChatBody("Once upon a time, ", "length"))   // cut off
            .Enqueue(HttpStatusCode.OK, ChatBody("the buoy bobbed on. ", "length")) // cut off again
            .Enqueue(HttpStatusCode.OK, ChatBody("The end.", "stop"));              // finished

        var reply = await Chat(handler, Options()).CompleteAsync("sys", "write me a story");

        Assert.Equal("Once upon a time, the buoy bobbed on. The end.", reply);
        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task Complete_NaturalStop_MakesOneRequestOnly()
    {
        var handler = new StubHttpMessageHandler().Enqueue(HttpStatusCode.OK, ChatBody("Short answer.", "stop"));

        var reply = await Chat(handler, Options()).CompleteAsync("sys", "hi");

        Assert.Equal("Short answer.", reply);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Complete_StopsAtTheContinuationCap()
    {
        // Every response claims "length" — the cap must halt the loop (initial + N continuations).
        var handler = new StubHttpMessageHandler().Enqueue(HttpStatusCode.OK, ChatBody("more ", "length"));

        var reply = await Chat(handler, Options(maxContinuations: 2)).CompleteAsync("sys", "go");

        Assert.Equal(3, handler.CallCount); // 1 initial + 2 continuations
        Assert.Equal("more more more", reply.Trim());
    }

    [Fact]
    public async Task Complete_DoesNotContinue_WhenDisabled()
    {
        var handler = new StubHttpMessageHandler().Enqueue(HttpStatusCode.OK, ChatBody("partial", "length"));

        var reply = await Chat(handler, Options(autoContinue: false)).CompleteAsync("sys", "go");

        Assert.Equal("partial", reply);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task JsonMode_NeverStitchesContinuations()
    {
        // Half a JSON document is useless — structured output must not be auto-continued.
        var handler = new StubHttpMessageHandler().Enqueue(HttpStatusCode.OK, ChatBody("[{\\\"kind\\\":", "length"));

        await Chat(handler, Options()).CompleteAsync("sys", "extract", jsonMode: true);

        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Stream_AutoContinues_AcrossRounds_AndYieldsTheWholeAnswer()
    {
        var handler = new StubHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, SseBody("Hello ", "length"), "text/event-stream")
            .Enqueue(HttpStatusCode.OK, SseBody("world.", "stop"), "text/event-stream");

        var chunks = new List<string>();
        await foreach (var c in Chat(handler, Options()).StreamAsync("sys", "go"))
            chunks.Add(c);

        Assert.Equal("Hello world.", string.Concat(chunks));
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Stream_NaturalStop_DoesNotContinue()
    {
        var handler = new StubHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, SseBody("Done.", "stop"), "text/event-stream");

        var chunks = new List<string>();
        await foreach (var c in Chat(handler, Options()).StreamAsync("sys", "go"))
            chunks.Add(c);

        Assert.Equal("Done.", string.Concat(chunks));
        Assert.Equal(1, handler.CallCount);
    }
}
