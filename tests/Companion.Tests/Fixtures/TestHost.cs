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

    public TestHost(
        DateTimeOffset now,
        Action<CompanionOptions>? configureOptions = null,
        Action<IServiceCollection>? configureServices = null,
        string? connectionString = null,
        IEnumerable<KeyValuePair<string, string?>>? settings = null)
    {
        Clock = new FixedTimeProvider(now);

        // A named shared-cache in-memory DB survives as long as at least one connection is open.
        // Tests that need real file semantics (restart, WAL, concurrency) pass their own path.
        connectionString ??= $"Data Source=file:test-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(connectionString);
        _keepAlive.Open();

        // Raw configuration keys, for the parts of composition that read IConfiguration directly
        // rather than a bound options class — the model roster and the cognitive-model registry
        // both decide what to construct before options binding has happened.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings ?? new Dictionary<string, string?>())
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCompanion(configuration, connectionString);

        // Override the system clock with the deterministic one (last singleton registration wins).
        services.AddSingleton<TimeProvider>(Clock);
        if (configureOptions is not null)
            services.Configure(configureOptions);

        // Late overrides for test doubles (e.g. a scripted IChatModel) — last registration wins.
        configureServices?.Invoke(services);

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
