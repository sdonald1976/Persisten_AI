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
        services.AddScoped<IVectorIndex, SqliteBlobVectorIndex>();

        // Model providers. Phase 2 ships the deterministic mocks; real providers plug in here.
        services.AddSingleton<IEmbeddingModel>(new MockEmbeddingModel(dimensions: 128));
        services.AddSingleton<IChatModel, MockChatModel>();

        // Core services.
        services.AddScoped<IRetriever, Retriever>();
        services.AddScoped<IContextAssembler, ContextAssembler>();
        services.AddScoped<ICompanion, Core.Services.Companion>();

        services.AddScoped<Seeding.CompanionSeeder>();

        return services;
    }

    /// <summary>Creates the schema if it doesn't exist (Phase 2 uses EnsureCreated, not migrations).</summary>
    public static async Task EnsureDatabaseCreatedAsync(this IServiceProvider provider, CancellationToken ct = default)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CompanionDbContext>();
        await db.Database.EnsureCreatedAsync(ct);
    }
}
