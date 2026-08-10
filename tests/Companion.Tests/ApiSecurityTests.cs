using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Infrastructure.Models;
using Companion.Tests.Fixtures;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Integration tests for the locked-down local API (H1/H2/H3/H6): token auth, CORS allow-list,
/// orphan/foreign conversation rejection, and sanitized errors. Runs the real app offline.
/// </summary>
public class ApiSecurityTests : IClassFixture<CompanionApiFactory>
{
    private readonly CompanionApiFactory _factory;
    public ApiSecurityTests(CompanionApiFactory factory) => _factory = factory;

    private sealed record ErrorBody(string Error, string Message, string CorrelationId);
    private sealed record ConversationCreated(string ConversationId);

    private static HttpClient Authed(HttpClient client)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CompanionApiFactory.Token);
        return client;
    }

    // ---- token authentication ----

    [Fact]
    public async Task Request_WithoutToken_Is401()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Request_WithInvalidToken_Is401()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-the-token");
        var resp = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Request_WithValidToken_Succeeds()
    {
        using var client = Authed(_factory.CreateClient());
        var resp = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // ---- voice loop: TTS endpoint ----

    [Fact]
    public async Task Speak_WhenTtsNotConfigured_Is503()
    {
        // The offline test app has no Models.Speech, so /speak reports it's unavailable rather than 500.
        using var client = Authed(_factory.CreateClient());
        var resp = await client.PostAsJsonAsync("/speak", new { text = "hello there" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
    }

    [Fact]
    public async Task Speak_WithoutToken_Is401()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/speak", new { text = "hello" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task XCompanionKeyHeader_IsAlsoAccepted()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Companion-Key", CompanionApiFactory.Token);
        var resp = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // ---- CORS allow-list ----

    [Fact]
    public async Task Cors_AllowedOrigin_IsEchoed()
    {
        using var client = Authed(_factory.CreateClient());
        var req = new HttpRequestMessage(HttpMethod.Get, "/health");
        req.Headers.Add("Origin", CompanionApiFactory.AllowedOrigin);
        var resp = await client.SendAsync(req);
        Assert.True(resp.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal(CompanionApiFactory.AllowedOrigin, resp.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task Cors_ForeignOrigin_IsNotAllowed()
    {
        using var client = Authed(_factory.CreateClient());
        var req = new HttpRequestMessage(HttpMethod.Get, "/health");
        req.Headers.Add("Origin", "http://evil.example.com");
        var resp = await client.SendAsync(req);
        Assert.False(resp.Headers.Contains("Access-Control-Allow-Origin")); // no ACAO for a disallowed origin
    }

    // ---- orphan / unknown conversation ----

    [Fact]
    public async Task Chat_UnknownConversation_Is404_Sanitized()
    {
        using var client = Authed(_factory.CreateClient());
        var resp = await client.PostAsJsonAsync("/chat",
            new { conversationId = Guid.NewGuid().ToString(), message = "hello" });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Equal("ConversationNotFound", body!.Error);
        Assert.False(string.IsNullOrWhiteSpace(body.CorrelationId));
    }

    [Fact]
    public async Task Chat_KnownConversation_Succeeds()
    {
        using var client = Authed(_factory.CreateClient());
        var created = await (await client.PostAsJsonAsync("/conversations", new { }))
            .Content.ReadFromJsonAsync<ConversationCreated>();

        var resp = await client.PostAsJsonAsync("/chat",
            new { conversationId = created!.ConversationId, message = "hello" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // ---- sanitized errors + server-side logging ----

    [Fact]
    public async Task UnhandledError_IsSanitized_AndDetailIsLoggedServerSide()
    {
        var capture = new CapturingLoggerProvider();
        using var factory = _factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s =>
            {
                s.AddScoped<IAgent, ThrowingAgent>();
                s.AddSingleton<ILoggerProvider>(capture); // registered providers are picked up by the logger factory
            }));
        using var client = Authed(factory.CreateClient());

        var created = await (await client.PostAsJsonAsync("/conversations", new { }))
            .Content.ReadFromJsonAsync<ConversationCreated>();

        var resp = await client.PostAsJsonAsync("/chat",
            new { conversationId = created!.ConversationId, message = "hello" });

        // Client sees a sanitized envelope, never the raw exception text.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Equal("ProviderUnavailable", body!.Error);
        Assert.DoesNotContain("secret-internal-detail", body.Message);
        Assert.False(string.IsNullOrWhiteSpace(body.CorrelationId));

        // Server log retains the detailed exception.
        Assert.Contains(capture.Entries, e => e.Exception is ModelProviderException);
    }

    /// <summary>An agent that always fails, to exercise the error pipeline deterministically.</summary>
    private sealed class ThrowingAgent : IAgent
    {
        public Task<AgentReply> HandleAsync(string userId, Guid conversationId, string input,
            IProgress<string>? tokenSink = null, CancellationToken ct = default)
            => throw new ModelProviderException("secret-internal-detail: provider blew up");

        public Task<AgentReply> ConfirmAsync(string userId, Guid conversationId, string confirmationToken,
            bool confirmed, CancellationToken ct = default)
            => throw new ModelProviderException("secret-internal-detail: provider blew up");
    }
}
