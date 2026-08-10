using System.Net;
using System.Text.Json;
using Companion.Infrastructure.Models;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Text-to-speech adapter over an OpenAI-compatible <c>/v1/audio/speech</c> endpoint, exercised with
/// a mocked handler — no audio server required. Verifies the request shape and that the raw audio
/// bytes and a sensible content type come back.
/// </summary>
public class SpeechSynthesizerTests
{
    private static readonly byte[] FakeAudio = { 0x49, 0x44, 0x33, 0x04, 0x00, 0x01, 0x02, 0x03 }; // "ID3"…

    private static EndpointOptions Options(string? voice = null, string? format = null) => new()
    {
        BaseUrl = "http://localhost:8080/v1",
        Model = "tts-1",
        Voice = voice,
        AudioFormat = format,
        MaxRetries = 0,
        TimeoutSeconds = 30,
    };

    private sealed class OneClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public OneClientFactory(HttpClient client) => _client = client;
        public HttpClient CreateClient(string name) => _client;
    }

    private static OpenAiCompatibleSpeechSynthesizer Synth(StubHttpMessageHandler handler, EndpointOptions opts)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080/v1/") };
        return new OpenAiCompatibleSpeechSynthesizer(
            opts, new OneClientFactory(http), "speech", NullLogger<OpenAiCompatibleSpeechSynthesizer>.Instance);
    }

    [Fact]
    public async Task Synthesize_PostsTheText_AndReturnsAudioBytes()
    {
        var handler = new StubHttpMessageHandler().EnqueueBytes(HttpStatusCode.OK, FakeAudio, "audio/mpeg");
        var audio = await Synth(handler, Options(voice: "alloy", format: "mp3"))
            .SynthesizeAsync("hello there");

        Assert.Equal(FakeAudio, audio.Audio);
        Assert.Equal("audio/mpeg", audio.ContentType);

        // The request carried the model, the input text, and the configured voice + format.
        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("tts-1", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal("hello there", doc.RootElement.GetProperty("input").GetString());
        Assert.Equal("alloy", doc.RootElement.GetProperty("voice").GetString());
        Assert.Equal("mp3", doc.RootElement.GetProperty("response_format").GetString());
    }

    [Fact]
    public async Task Synthesize_PerCallVoice_OverridesTheDefault()
    {
        var handler = new StubHttpMessageHandler().EnqueueBytes(HttpStatusCode.OK, FakeAudio, "audio/mpeg");
        await Synth(handler, Options(voice: "default-voice")).SynthesizeAsync("hi", voice: "override-voice");

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("override-voice", doc.RootElement.GetProperty("voice").GetString());
    }

    [Fact]
    public async Task Synthesize_DerivesContentType_FromFormat_WhenServerDoesntSayAudio()
    {
        // Server returns a non-audio content type → fall back to the requested format's type.
        var handler = new StubHttpMessageHandler()
            .EnqueueBytes(HttpStatusCode.OK, FakeAudio, "application/octet-stream");
        var audio = await Synth(handler, Options(format: "wav")).SynthesizeAsync("hi");

        Assert.Equal("audio/wav", audio.ContentType);
    }

    [Fact]
    public async Task Synthesize_DefaultsToMp3_WhenNoFormatConfigured()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueBytes(HttpStatusCode.OK, FakeAudio, "application/octet-stream");
        var audio = await Synth(handler, Options()).SynthesizeAsync("hi");

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("mp3", doc.RootElement.GetProperty("response_format").GetString());
        Assert.Equal("audio/mpeg", audio.ContentType);
    }
}
