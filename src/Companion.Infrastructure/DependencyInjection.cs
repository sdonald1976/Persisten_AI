using Companion.Core;
using Companion.Core.Abstractions;
using Companion.Core.Services;
using Companion.Infrastructure.Models;
using Companion.Infrastructure.Persistence;
using Companion.Infrastructure.Vector;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Companion.Infrastructure;

/// <summary>Composition root for the companion. Keeps provider choices in one place.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddCompanion(
        this IServiceCollection services, IConfiguration configuration, string sqliteConnectionString)
    {
        services.Configure<CompanionOptions>(configuration.GetSection(CompanionOptions.SectionName));

        services.AddSingleton(TimeProvider.System);

        services.AddDbContext<CompanionDbContext>(o => o.UseSqlite(sqliteConnectionString));

        // Stores (authoritative) + vector index (derived).
        services.AddScoped<IConversationStore, ConversationStore>();
        services.AddScoped<IMemoryStore, MemoryStore>();
        services.AddScoped<IProjectStore, ProjectStore>();
        services.AddScoped<IVectorIndex, SqliteBlobVectorIndex>();

        // Model providers. Phase 2 ships the deterministic mocks; real providers plug in here.
        services.AddSingleton<IEmbeddingModel>(new MockEmbeddingModel(dimensions: 128));
        services.AddSingleton<IChatModel, MockChatModel>();

        // Memory extraction. The rule-based extractor is the offline default; swap for
        // LlmMemoryExtractor (same interface) when a real chat model is configured.
        services.AddScoped<IMemoryExtractor, RuleBasedMemoryExtractor>();
        services.AddScoped<IMemoryPipeline, MemoryPipeline>();

        // Project awareness: resolution, summary/open-loop context, and post-turn updates.
        services.AddScoped<IEntityResolver, EntityResolver>();
        services.AddScoped<IProjectContextService, ProjectContextService>();
        services.AddScoped<IProjectUpdater, ProjectUpdater>();

        // Temporal revision & corrections (Phase 5).
        services.AddScoped<IMemoryCurator, MemoryCurator>();
        services.AddScoped<IProjectCurator, ProjectCurator>();

        // Core services.
        services.AddScoped<IRetriever, Retriever>();
        services.AddScoped<IContextAssembler, ContextAssembler>();
        services.AddScoped<ICompanion, Core.Services.Companion>();

        services.AddScoped<Seeding.CompanionSeeder>();

        return services;
    }

    /// <summary>
    /// Applies any pending EF Core migrations, creating the schema on a fresh database and
    /// upgrading an existing one in place. This replaces EnsureCreated so schema changes across
    /// phases apply incrementally instead of silently no-op'ing on an existing database.
    /// </summary>
    public static async Task MigrateDatabaseAsync(this IServiceProvider provider, CancellationToken ct = default)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CompanionDbContext>();

        // A database created by the old EnsureCreated path has application tables but no
        // migrations-history table; MigrateAsync would then fail trying to re-create them.
        // Detect that and give an actionable message instead of a cryptic SQL error.
        if (await IsLegacyPreMigrationDatabaseAsync(db, ct))
        {
            throw new InvalidOperationException(
                "The local database predates schema migrations and can't be upgraded in place. " +
                "Delete the database file (e.g. companion.db) and run again — it will be recreated, " +
                "then reload demo data with the 'seed' command.");
        }

        await db.Database.MigrateAsync(ct);
    }

    private static async Task<bool> IsLegacyPreMigrationDatabaseAsync(CompanionDbContext db, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(ct);

        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                tables.Add(reader.GetString(0));
        }

        // Legacy = has our tables but no migrations history. A fresh DB has neither.
        return tables.Contains("Messages") && !tables.Contains("__EFMigrationsHistory");
    }
}
