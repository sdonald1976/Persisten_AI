using Companion.Core;
using Companion.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Companion.Tests.Fixtures;

/// <summary>
/// Spins up the real composition root (<see cref="DependencyInjection.AddCompanion"/>) over a
/// private shared-cache in-memory SQLite database and a fixed clock, so integration tests run
/// against the actual wiring — stores, retriever, assembler, vector index — with no network.
/// </summary>
public sealed class TestHost : IAsyncDisposable
{
    private readonly SqliteConnection _keepAlive;

    public IServiceProvider Services { get; }
    public FixedTimeProvider Clock { get; }

    public TestHost(DateTimeOffset now, Action<CompanionOptions>? configureOptions = null)
    {
        Clock = new FixedTimeProvider(now);

        // A named shared-cache in-memory DB survives as long as at least one connection is open.
        var connectionString = $"Data Source=file:test-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(connectionString);
        _keepAlive.Open();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCompanion(configuration, connectionString);

        // Override the system clock with the deterministic one (last singleton registration wins).
        services.AddSingleton<TimeProvider>(Clock);
        if (configureOptions is not null)
            services.Configure(configureOptions);

        Services = services.BuildServiceProvider();
        Services.MigrateDatabaseAsync().GetAwaiter().GetResult();
    }

    public IServiceScope CreateScope() => Services.CreateScope();

    public async ValueTask DisposeAsync()
    {
        if (Services is IAsyncDisposable d)
            await d.DisposeAsync();
        await _keepAlive.DisposeAsync();
    }
}
