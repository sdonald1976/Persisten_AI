using Companion.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>H4: provider configuration is validated at composition time; unknown values fail fast.</summary>
public class ProviderConfigValidationTests
{
    private static IConfiguration Config(params (string Key, string Value)[] pairs)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => (string?)p.Value))
            .Build();

    [Fact]
    public void UnknownProvider_FailsStartup()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var config = Config(("Models:Provider", "Frankenstein"));

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddCompanion(config, "Data Source=:memory:"));
        Assert.Contains("Unknown Models.Provider", ex.Message);
        Assert.Contains("Frankenstein", ex.Message);
    }

    [Theory]
    [InlineData("Mock")]
    [InlineData("mock")] // case-insensitive
    public void MockProvider_IsAccepted(string provider)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // Should not throw.
        services.AddCompanion(Config(("Models:Provider", provider)), "Data Source=:memory:");
    }

    [Fact]
    public void RealProvider_WithMissingModelNames_FailsStartup()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // OpenAiCompatible with no model names configured → clear error.
        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddCompanion(Config(("Models:Provider", "OpenAiCompatible")), "Data Source=:memory:"));
        Assert.Contains("no model name configured", ex.Message);
    }
}
