using Companion.Core.Domain;
using Companion.Infrastructure.Models;
using Companion.Infrastructure.Persistence;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// A model name is the one setting nothing else validates: a typo or an un-pulled tag builds,
/// starts, and tests clean, then fails on the user's first sentence. The preflight checks the
/// provider's catalog and the registry records what it found — so "available" stops meaning
/// "configured" and starts meaning "actually there".
/// </summary>
public class ModelPreflightTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static CapabilityRegistry Registry(IServiceScope scope, ModelOptions models) =>
        new(scope.ServiceProvider.GetRequiredService<CompanionDbContext>(), models);

    private static ModelOptions Ollama() => new()
    {
        Provider = "Ollama",
        Chat = new EndpointOptions { Model = "stheno-8b" },
        Embeddings = new EndpointOptions { Model = "nomic-embed" },
    };

    // ---- catalog name matching ----

    [Theory]
    [InlineData("llama3.1:8b", true)]                  // exact
    [InlineData("nomic-embed-text", true)]             // untagged config, :latest in the catalog
    [InlineData("nomic-embed-text:latest", true)]      // tagged config, untagged in the catalog
    [InlineData("LLAMA3.1:8B", true)]                  // servers are case-insensitive about tags
    [InlineData("llama3.1:70b", false)]                // right model, wrong tag is still missing
    [InlineData("qwen2.5:3b-instruct", false)]
    [InlineData("", false)]
    public void Serves_MatchesTagsTheWayOllamaReportsThem(string configured, bool expected)
    {
        var catalog = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "llama3.1:8b", "nomic-embed-text:latest", "llama3.2:3b",
        };

        Assert.Equal(expected, ModelPreflight.Serves(catalog, configured));
    }

    // ---- the probe itself, over a stubbed provider ----

    private static (ModelPreflight Preflight, CapturingLoggerProvider Logs) Probe(
        ModelOptions models, HttpMessageHandler handler)
    {
        var services = new ServiceCollection();
        var logs = new CapturingLoggerProvider();
        services.AddLogging(b => b.AddProvider(logs));

        // Every role's named client resolves to the same stub, so one catalog answers them all.
        services.AddHttpClient(string.Empty)
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        services.AddSingleton<IHttpClientFactory>(sp => new SingleClientFactory(handler, models.Chat.BaseUrl));

        var sp = services.BuildServiceProvider();
        return (new ModelPreflight(models, sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<ILogger<ModelPreflight>>()), logs);
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        private readonly string _baseUrl;
        public SingleClientFactory(HttpMessageHandler handler, string baseUrl)
        {
            _handler = handler;
            _baseUrl = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
        }

        public HttpClient CreateClient(string name) =>
            new(_handler, disposeHandler: false) { BaseAddress = new Uri(_baseUrl) };
    }

    private const string Catalog =
        """{"data":[{"id":"llama3.1:8b"},{"id":"nomic-embed-text:latest"}]}""";

    [Fact]
    public async Task AMissingModel_IsReportedAsAnError_NotJustReturned()
    {
        // Regression: the error path is the one that only runs when something is already wrong,
        // so a malformed log template here stayed invisible until a user hit it. Exercise it.
        var models = Ollama();
        var handler = new StubHttpMessageHandler().Enqueue(System.Net.HttpStatusCode.OK, Catalog);
        var (preflight, logs) = Probe(models, handler);

        var report = await preflight.RunAsync(Now);

        Assert.Contains(report.Missing, r => r.Model == "stheno-8b");
        var error = logs.Entries.FirstOrDefault(e => e.Level == LogLevel.Error);
        Assert.NotNull(error);
        Assert.Contains("stheno-8b", error!.Message);
        Assert.Null(error.Exception); // a broken template surfaces here as a logging failure
    }

    [Fact]
    public async Task AConfiguredRosterThatIsAllPresent_ReportsNothingMissing()
    {
        var models = new ModelOptions
        {
            Provider = "Ollama",
            Chat = new EndpointOptions { Model = "llama3.1:8b" },
            Embeddings = new EndpointOptions { Model = "nomic-embed-text" }, // :latest in the catalog
        };
        var handler = new StubHttpMessageHandler().Enqueue(System.Net.HttpStatusCode.OK, Catalog);
        var (preflight, logs) = Probe(models, handler);

        var report = await preflight.RunAsync(Now);

        Assert.Empty(report.Missing);
        Assert.Empty(report.Unchecked);
        Assert.DoesNotContain(logs.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task AnUnreachableProvider_IsUnverified_NotMissing()
    {
        var handler = new StubHttpMessageHandler { Throw = new HttpRequestException("connection refused") };
        var (preflight, logs) = Probe(Ollama(), handler);

        var report = await preflight.RunAsync(Now);

        Assert.Empty(report.Missing);
        Assert.NotEmpty(report.Unchecked);
        Assert.DoesNotContain(logs.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task OfflineMode_SkipsTheProbeEntirely()
    {
        var handler = new StubHttpMessageHandler().Enqueue(System.Net.HttpStatusCode.OK, Catalog);
        var (preflight, _) = Probe(new ModelOptions(), handler); // Provider "Mock"

        var report = await preflight.RunAsync(Now);

        Assert.Null(report.CheckedAt);
        Assert.Equal(0, handler.CallCount);
    }

    // ---- what the registry does with the answer ----

    [Fact]
    public async Task MissingModel_MakesTheCapabilityUnavailable()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var registry = Registry(scope, Ollama());
        await registry.GetAllAsync();

        await registry.ApplyModelProbeAsync(
            new Dictionary<string, bool> { ["stheno-8b"] = false }, Now);

        var chat = (await registry.GetAllAsync()).Single(c => c.Id == "text.chat");
        Assert.Equal(CapabilityAvailability.Unavailable, chat.Availability);
        Assert.Equal(0, chat.Confidence);
        Assert.Equal(Now, chat.LastVerifiedAt);
    }

    [Fact]
    public async Task UnreachableProvider_LeavesEveryRowAlone()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var registry = Registry(scope, Ollama());
        var before = (await registry.GetAllAsync()).Single(c => c.Id == "text.chat");
        var availabilityBefore = before.Availability;

        // A probe that couldn't reach the server reports nothing at all — "unreachable" must never
        // be recorded as "missing", or one flaky request condemns a working roster.
        await registry.ApplyModelProbeAsync(new Dictionary<string, bool>(), Now);

        var after = (await registry.GetAllAsync()).Single(c => c.Id == "text.chat");
        Assert.Equal(availabilityBefore, after.Availability);
        Assert.Null(after.LastVerifiedAt);
    }

    [Fact]
    public async Task PresentModel_DoesNotOverturnARealCallFailure()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var registry = Registry(scope, Ollama());
        await registry.GetAllAsync();

        // Three real failures took it out of service.
        for (var i = 0; i < 3; i++)
            await registry.MarkFailureAsync("text.chat", Now);
        Assert.Equal(
            CapabilityAvailability.Unavailable,
            (await registry.GetAllAsync()).Single(c => c.Id == "text.chat").Availability);

        // The model is installed — but being installed is weaker evidence than calls that failed.
        await registry.ApplyModelProbeAsync(
            new Dictionary<string, bool> { ["stheno-8b"] = true }, Now);

        Assert.Equal(
            CapabilityAvailability.Unavailable,
            (await registry.GetAllAsync()).Single(c => c.Id == "text.chat").Availability);
    }

    [Fact]
    public async Task PulledModel_RecoversTheCapability()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var registry = Registry(scope, Ollama());
        await registry.GetAllAsync();

        await registry.ApplyModelProbeAsync(
            new Dictionary<string, bool> { ["stheno-8b"] = false }, Now);
        await registry.ApplyModelProbeAsync(
            new Dictionary<string, bool> { ["stheno-8b"] = true }, Now);

        var chat = (await registry.GetAllAsync()).Single(c => c.Id == "text.chat");
        Assert.Equal(CapabilityAvailability.Available, chat.Availability);
        Assert.True(chat.Confidence > 0);
    }
}
