using Companion.Core;
using Companion.Core.Abstractions;
using Companion.Core.Services;
using Companion.Infrastructure.Models;
using Companion.Infrastructure.Persistence;
using Companion.Infrastructure.Vector;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
        services.AddScoped<IProfileStore, ProfileStore>();
        services.AddScoped<IFeedbackStore, FeedbackStore>();
        services.AddScoped<IVectorIndex, SqliteBlobVectorIndex>();

        // Natural-language intent parsing (so slash commands aren't required).
        services.AddSingleton<IIntentParser, RuleBasedIntentParser>();

        // Model providers, selected by configuration ("Models" section). Default is the
        // deterministic offline mocks; "OpenAiCompatible" (or "Ollama"/"LMStudio") uses a real
        // local server. When a real model is configured, extraction and summarization use it too.
        var modelOptions = configuration.GetSection(ModelOptions.SectionName).Get<ModelOptions>() ?? new ModelOptions();
        services.AddSingleton(modelOptions); // so the CLI can see which optional models are configured

        // Optional multimodal models — registered only when configured.
        if (modelOptions.UsesRealModel && modelOptions.Vision is { } visionEndpoint)
        {
            services.AddSingleton<IVisionModel>(sp =>
                new OpenAiCompatibleVisionModel(visionEndpoint, sp.GetRequiredService<ILogger<OpenAiCompatibleVisionModel>>()));
        }
        if (modelOptions.UsesRealModel && modelOptions.Transcription is { } transcriptionEndpoint)
        {
            services.AddSingleton<ITranscriber>(sp =>
                new OpenAiCompatibleTranscriber(transcriptionEndpoint, sp.GetRequiredService<ILogger<OpenAiCompatibleTranscriber>>()));
        }

        if (modelOptions.UsesRealModel)
        {
            // A separate chat model per job (keyed), so you can run a big conversational model,
            // a small structured-output-friendly extraction model, and a cheap/fast summarizer.
            // Extraction/summarizer fall back to the conversational endpoint when not configured.
            OpenAiCompatibleChatModel BuildChat(IServiceProvider sp, EndpointOptions ep) =>
                new(ep, sp.GetRequiredService<ILogger<OpenAiCompatibleChatModel>>());

            services.AddKeyedSingleton<IChatModel>(ChatRoles.Conversation, (sp, _) => BuildChat(sp, modelOptions.Chat));
            services.AddKeyedSingleton<IChatModel>(ChatRoles.Extraction, (sp, _) => BuildChat(sp, modelOptions.ExtractionOrChat));
            services.AddKeyedSingleton<IChatModel>(ChatRoles.Summarizer, (sp, _) => BuildChat(sp, modelOptions.SummarizerOrChat));

            // The default IChatModel (the assistant's reply) is the conversational one.
            services.AddSingleton<IChatModel>(sp => sp.GetRequiredKeyedService<IChatModel>(ChatRoles.Conversation));

            services.AddSingleton<IEmbeddingModel>(sp =>
                new OpenAiCompatibleEmbeddingModel(modelOptions.Embeddings, sp.GetRequiredService<ILogger<OpenAiCompatibleEmbeddingModel>>()));

            services.AddSingleton<ISummarizer>(sp =>
                new LlmSummarizer(sp.GetRequiredKeyedService<IChatModel>(ChatRoles.Summarizer)));
            services.AddScoped<IMemoryExtractor>(sp =>
                new LlmMemoryExtractor(
                    sp.GetRequiredKeyedService<IChatModel>(ChatRoles.Extraction),
                    sp.GetRequiredService<ILogger<LlmMemoryExtractor>>()));
        }
        else
        {
            services.AddSingleton<IEmbeddingModel>(new MockEmbeddingModel(dimensions: 128));
            services.AddSingleton<IChatModel, MockChatModel>();
            services.AddSingleton<ISummarizer, MockSummarizer>();
            services.AddScoped<IMemoryExtractor, RuleBasedMemoryExtractor>();
        }

        services.AddScoped<IMemoryPipeline, MemoryPipeline>();

        // Project awareness: resolution, summary/open-loop context, and post-turn updates.
        services.AddScoped<IEntityResolver, EntityResolver>();
        services.AddScoped<IProjectContextService, ProjectContextService>();
        services.AddScoped<IProjectUpdater, ProjectUpdater>();

        // Temporal revision & corrections (Phase 5).
        services.AddScoped<IMemoryCurator, MemoryCurator>();
        services.AddScoped<IProjectCurator, ProjectCurator>();

        // Consolidation (Phase 6).
        services.AddScoped<IMemoryConsolidator, MemoryConsolidator>();

        // Core services.
        services.AddScoped<IRetriever, Retriever>();
        services.AddScoped<IContextAssembler, ContextAssembler>();
        services.AddScoped<ICompanion, Core.Services.Companion>();

        // The brain facade every face (CLI, HTTP, voice, avatar) drives the companion through.
        services.AddScoped<IAgent, Agent>();

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
