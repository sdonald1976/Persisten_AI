using Companion.Core.Domain;
using Companion.Infrastructure.Models;
using Companion.Infrastructure.Persistence;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The capability registry must describe the CURRENT configuration, not the one that happened
/// to be live when the database was first touched: config changes re-sync the rows (resetting
/// verification, which described the old backend), unchanged rows keep their history, and
/// capabilities this build no longer knows disappear.
/// </summary>
public class CapabilitySyncTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static CapabilityRegistry Registry(IServiceScope scope, ModelOptions models) =>
        new(scope.ServiceProvider.GetRequiredService<CompanionDbContext>(), models);

    private static ModelOptions Mock() => new(); // Provider "Mock" by default

    private static ModelOptions Ollama() => new()
    {
        Provider = "Ollama",
        Chat = new EndpointOptions { Model = "stheno-8b" },
        Embeddings = new EndpointOptions { Model = "nomic-embed" },
    };

    [Fact]
    public async Task FirstRead_SeedsTheDefaultRows()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();

        var all = await Registry(scope, Mock()).GetAllAsync();

        Assert.Contains(all, c => c.Id == "text.chat" && c.Provider == "Mock");
        Assert.Contains(all, c => c.Id == "image.describe" && c.Availability == CapabilityAvailability.Unavailable);
    }

    [Fact]
    public async Task ConfigChange_ResyncsTheRow_AndResetsStaleVerification()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();

        // Seeded and verified under Mock…
        var mock = Registry(scope, Mock());
        await mock.GetAllAsync();
        await mock.MarkSuccessAsync("text.chat", Now);

        // …then the user switches to a real model. The chat row must follow the config, and its
        // Mock-era verification must not vouch for a backend it never saw.
        var chat = (await Registry(scope, Ollama()).GetAllAsync()).Single(c => c.Id == "text.chat");
        Assert.Equal("Ollama", chat.Provider);
        Assert.Equal("stheno-8b", chat.Model);
        Assert.Null(chat.LastVerifiedAt);
        Assert.Equal(0, chat.ConsecutiveFailures);
    }

    [Fact]
    public async Task UnchangedConfig_PreservesVerificationHistory()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();

        var registry = Registry(scope, Mock());
        await registry.GetAllAsync();
        await registry.MarkSuccessAsync("text.chat", Now);

        var again = (await Registry(scope, Mock()).GetAllAsync()).Single(c => c.Id == "text.chat");
        Assert.Equal(Now, again.LastVerifiedAt);
        Assert.Equal(CapabilityAvailability.Available, again.Availability);
    }

    [Fact]
    public async Task ObsoleteRow_IsRemoved()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CompanionDbContext>();
        db.Capabilities.Add(new CapabilityDescriptor
        {
            Id = "time.travel", Name = "Time travel", Description = "visit last tuesday",
            InputTypes = "intent", OutputTypes = "paradox", Provider = "none",
            Availability = CapabilityAvailability.Available, LocalOnly = true, Confidence = 1,
        });
        await db.SaveChangesAsync();

        var all = await Registry(scope, Mock()).GetAllAsync();

        Assert.DoesNotContain(all, c => c.Id == "time.travel");
    }
}
