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
        services.Configure<PersonalityOptions>(configuration.GetSection(PersonalityOptions.SectionName));
        services.Configure<IdentityOptions>(configuration.GetSection(IdentityOptions.SectionName));
        services.Configure<OutreachOptions>(configuration.GetSection(OutreachOptions.SectionName));
        services.AddSingleton<IPersonalityService, PersonalityService>();

        services.AddSingleton(TimeProvider.System);

        // The active user is derived from trusted execution context, never from a request body.
        // Local single-user default; a future auth boundary replaces this registration only.
        var localUserId = configuration["User:Id"] ?? Seeding.CompanionSeeder.DemoUserId;
        services.AddSingleton<IUserContext>(new FixedUserContext(localUserId));

        services.AddDbContext<CompanionDbContext>(o => o.UseSqlite(sqliteConnectionString));

        // Stores (authoritative) + vector index (derived).
        services.AddScoped<IConversationStore, ConversationStore>();
        services.AddScoped<IMemoryStore, MemoryStore>();
        services.AddScoped<IProjectStore, ProjectStore>();
        services.AddScoped<IProfileStore, ProfileStore>();
        services.AddScoped<IFeedbackStore, FeedbackStore>();
        services.AddScoped<IPendingClarificationStore, PendingClarificationStore>();
        services.AddScoped<IEmotionStore, EmotionStore>();
        services.AddScoped<IReflectionStore, ReflectionStore>();
        services.AddScoped<IAnticipationStore, AnticipationStore>();
        services.AddScoped<IPreferenceStore, PreferenceStore>();
        // The vector index is a singleton in-memory cache (cold-loaded per user from the tables,
        // then kept in step via the write-through hook the memory store calls). Both faces of it —
        // search and maintenance — resolve to the same instance so writes reach the cache.
        services.AddSingleton<InMemoryVectorIndex>();
        services.AddSingleton<IVectorIndex>(sp => sp.GetRequiredService<InMemoryVectorIndex>());
        services.AddSingleton<IVectorIndexMaintenance>(sp => sp.GetRequiredService<InMemoryVectorIndex>());

        // Natural-language intent parsing (so slash commands aren't required). The deterministic
        // rules are always registered (and always run first); with a real model plus the opt-in
        // flag, the LLM classifier layers on top for phrasings the rules don't know — it can only
        // ever promote plain chat to a read-only intent, never override a rule hit.
        services.AddSingleton<RuleBasedIntentParser>();
        var useLlmIntents = configuration.GetSection(CompanionOptions.SectionName)
            .GetValue<bool>("UseLlmIntentParser");
        if (useLlmIntents)
        {
            services.AddSingleton<IIntentParser>(sp => new LlmIntentParser(
                sp.GetRequiredKeyedService<IChatModel>(ChatRoles.Extraction),
                sp.GetRequiredService<RuleBasedIntentParser>(),
                sp.GetRequiredService<ILogger<LlmIntentParser>>()));
        }
        else
        {
            services.AddSingleton<IIntentParser>(sp => sp.GetRequiredService<RuleBasedIntentParser>());
        }

        // Model providers, selected by configuration ("Models" section). Default is the
        // deterministic offline mocks; "OpenAiCompatible" (or "Ollama"/"LMStudio") uses a real
        // local server. When a real model is configured, extraction and summarization use it too.
        var modelOptions = configuration.GetSection(ModelOptions.SectionName).Get<ModelOptions>() ?? new ModelOptions();
        ValidateModelOptions(modelOptions);
        services.AddSingleton(modelOptions); // so the CLI can see which optional models are configured

        // Model HTTP access goes through Microsoft's IHttpClientFactory: one named client per role,
        // per-role timeout/base-url, managed handler lifetime, and a Polly transient-retry policy.
        services.AddModelHttpClients(modelOptions);

        // Optional multimodal models — registered only when configured.
        if (modelOptions.UsesRealModel && modelOptions.Vision is { } visionEndpoint)
        {
            services.AddSingleton<IVisionModel>(sp => new OpenAiCompatibleVisionModel(
                visionEndpoint, sp.GetRequiredService<IHttpClientFactory>(),
                ProviderHttpClients.Name(ProviderHttpClients.Vision),
                sp.GetRequiredService<ILogger<OpenAiCompatibleVisionModel>>()));
        }
        if (modelOptions.UsesRealModel && modelOptions.Transcription is { } transcriptionEndpoint)
        {
            services.AddSingleton<ITranscriber>(sp => new OpenAiCompatibleTranscriber(
                transcriptionEndpoint, sp.GetRequiredService<IHttpClientFactory>(),
                ProviderHttpClients.Name(ProviderHttpClients.Transcription),
                sp.GetRequiredService<ILogger<OpenAiCompatibleTranscriber>>()));
        }
        if (modelOptions.UsesRealModel && modelOptions.Speech is { } speechEndpoint)
        {
            services.AddSingleton<ISpeechSynthesizer>(sp => new OpenAiCompatibleSpeechSynthesizer(
                speechEndpoint, sp.GetRequiredService<IHttpClientFactory>(),
                ProviderHttpClients.Name(ProviderHttpClients.Speech),
                sp.GetRequiredService<ILogger<OpenAiCompatibleSpeechSynthesizer>>()));
        }

        if (modelOptions.UsesRealModel)
        {
            // A separate chat model per job (keyed), so you can run a big conversational model,
            // a small structured-output-friendly extraction model, and a cheap/fast summarizer.
            // Extraction/summarizer fall back to the conversational endpoint when not configured.
            OpenAiCompatibleChatModel BuildChat(IServiceProvider sp, EndpointOptions ep, string role) =>
                new(ep, sp.GetRequiredService<IHttpClientFactory>(), ProviderHttpClients.Name(role),
                    sp.GetRequiredService<ILogger<OpenAiCompatibleChatModel>>());

            services.AddKeyedSingleton<IChatModel>(ChatRoles.Conversation, (sp, _) => BuildChat(sp, modelOptions.Chat, ProviderHttpClients.Conversation));
            services.AddKeyedSingleton<IChatModel>(ChatRoles.Extraction, (sp, _) => BuildChat(sp, modelOptions.ExtractionOrChat, ProviderHttpClients.Extraction));
            services.AddKeyedSingleton<IChatModel>(ChatRoles.Summarizer, (sp, _) => BuildChat(sp, modelOptions.SummarizerOrChat, ProviderHttpClients.Summarizer));

            // The default IChatModel (the assistant's reply) is the conversational one.
            services.AddSingleton<IChatModel>(sp => sp.GetRequiredKeyedService<IChatModel>(ChatRoles.Conversation));

            services.AddSingleton<IEmbeddingModel>(sp =>
                new OpenAiCompatibleEmbeddingModel(
                    modelOptions.Embeddings, sp.GetRequiredService<IHttpClientFactory>(),
                    ProviderHttpClients.Name(ProviderHttpClients.Embeddings),
                    sp.GetRequiredService<ILogger<OpenAiCompatibleEmbeddingModel>>()));

            services.AddSingleton<ISummarizer>(sp =>
                new LlmSummarizer(sp.GetRequiredKeyedService<IChatModel>(ChatRoles.Summarizer)));
            services.AddScoped<IMemoryExtractor>(sp =>
                new LlmMemoryExtractor(
                    sp.GetRequiredKeyedService<IChatModel>(ChatRoles.Extraction),
                    sp.GetRequiredService<ILogger<LlmMemoryExtractor>>()));

            // The semantic completion check runs on the cheap summarizer-role model, never the big one.
            services.AddSingleton<ICompletionJudge>(sp => new LlmCompletionJudge(
                sp.GetRequiredKeyedService<IChatModel>(ChatRoles.Summarizer),
                sp.GetRequiredService<ILogger<LlmCompletionJudge>>()));
        }
        else
        {
            services.AddSingleton<IEmbeddingModel>(new MockEmbeddingModel());
            services.AddSingleton<IChatModel, MockChatModel>();
            services.AddSingleton<ISummarizer, MockSummarizer>();
            services.AddScoped<IMemoryExtractor, RuleBasedMemoryExtractor>();

            // Offline replies are deterministic and self-contained — nothing to continue.
            services.AddSingleton<ICompletionJudge, AlwaysCompleteJudge>();
        }

        // The reply generator owns "when to keep going": it continues a cut-off reply (finish_reason
        // length) and asks the completion judge about a self-stopped one, feeding the text so far
        // back each round so it resumes the same task. Reads its policy from the conversational
        // endpoint's options (AutoContinue / MaxContinuations / CompletionCheck).
        services.AddSingleton<IReplyGenerator>(sp => new ReplyGenerator(
            sp.GetRequiredService<IChatModel>(),
            sp.GetRequiredService<ICompletionJudge>(),
            modelOptions.Chat,
            sp.GetRequiredService<ILogger<ReplyGenerator>>()));

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

        // Relational/emotional memory: derive how things have been feeling from the signal log so
        // the companion can attune its tone across sessions.
        services.AddScoped<IRelationshipTracker, RelationshipTracker>();

        // Her own inner state — the relationship tracker's mirror image, pointed inward. Spirits
        // follow how conversations have actually felt; energy follows her day. Colors every reply
        // and answers "how are you?" honestly.
        services.AddScoped<ICompanionStateTracker, CompanionStateTracker>();

        // The familiarity dial: tenure + real interaction depth calibrate how casual she may be.
        services.AddScoped<IFamiliarityTracker, FamiliarityTracker>();

        // The between-session reflection pass (inner monologue): musings + curiosities, written
        // while the user is away and surfaced through the greeter and the context packet. Thinks
        // with the conversational model — reflection is the companion's own voice, not a
        // structured-extraction chore.
        services.AddScoped<IReflector, Reflector>();

        // The full idle "sleep": think, then tidy (consolidation + curiosity hygiene). This is what
        // the background worker actually runs, so consolidation finally has a life of its own
        // instead of waiting for an explicit command.
        services.AddScoped<ISleepCycle, SleepCycle>();

        // Reaching out on her own: a push notification (ntfy) when the user's been away and she
        // holds a real curiosity to voice. The channel reports itself unconfigured until
        // Outreach.NtfyUrl is set, which keeps the whole feature silently off.
        services.AddHttpClient(NtfyChannel.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddSingleton<IOutboundChannel, NtfyChannel>();
        services.AddScoped<IOutreachStore, OutreachStore>();
        services.AddScoped<IOutreachService, OutreachService>();

        // Core services.
        services.AddScoped<IRetriever, Retriever>();
        services.AddScoped<IContextAssembler, ContextAssembler>();
        services.AddScoped<ICompanion, Core.Services.Companion>();

        // Session openers so the user never faces a blank prompt (the companion initiates). The
        // deterministic Greeter supplies the real remembered threads and the offline fallback; when
        // a real model is configured, LlmGreeter phrases the welcome naturally around those threads.
        services.AddScoped<Greeter>();
        if (modelOptions.UsesRealModel)
        {
            services.AddScoped<LlmGreeter>(sp => new LlmGreeter(
                sp.GetRequiredService<Greeter>(),
                sp.GetRequiredKeyedService<IChatModel>(ChatRoles.Conversation),
                sp.GetRequiredService<ILogger<LlmGreeter>>(),
                sp.GetRequiredService<IPersonalityService>(),
                sp.GetRequiredService<IProfileStore>()));
            services.AddScoped<IGreeter>(sp => sp.GetRequiredService<LlmGreeter>());
            // Registered only alongside a real model: faces that want an instant opener show the
            // deterministic Greeter's message first and upgrade the wording when this completes.
            services.AddScoped<IGreetingRephraser>(sp => sp.GetRequiredService<LlmGreeter>());
        }
        else
        {
            services.AddScoped<IGreeter>(sp => sp.GetRequiredService<Greeter>());
        }

        // Deterministic drafts restyled into her voice when a real model is available; otherwise
        // the draft IS the message. Any rephrase failure falls back to the draft, so this seam can
        // never break a feature — it only ever improves wording.
        if (modelOptions.UsesRealModel)
        {
            services.AddScoped<IVoiceRephraser>(sp => new LlmVoiceRephraser(
                sp.GetRequiredKeyedService<IChatModel>(ChatRoles.Conversation),
                sp.GetRequiredService<IPersonalityService>(),
                sp.GetRequiredService<IProfileStore>(),
                sp.GetRequiredService<ILogger<LlmVoiceRephraser>>()));
        }
        else
        {
            services.AddSingleton<IVoiceRephraser, PassthroughRephraser>();
        }

        // The brain facade every face (CLI, HTTP, voice, avatar) drives the companion through.
        services.AddScoped<IAgent, Agent>();

        services.AddScoped<Seeding.CompanionSeeder>();

        return services;
    }

    /// <summary>
    /// Fails fast on obvious provider misconfiguration when a real model is selected: every role
    /// that will actually be called needs a non-empty model name. Prevents opaque 400/404s at the
    /// first turn and a silent "works on mock, breaks on real" gap.
    /// </summary>
    /// <summary>The provider values the app understands. Anything else is a configuration error.</summary>
    private static readonly HashSet<string> SupportedProviders =
        new(StringComparer.OrdinalIgnoreCase) { "Mock", "OpenAiCompatible", "Ollama", "LMStudio" };

    private static void ValidateModelOptions(ModelOptions options)
    {
        // Never silently treat an unknown provider as "real". Fail fast with the allowed set.
        if (string.IsNullOrWhiteSpace(options.Provider) || !SupportedProviders.Contains(options.Provider))
            throw new InvalidOperationException(
                $"Unknown Models.Provider '{options.Provider}'. Supported values: " +
                $"{string.Join(", ", SupportedProviders.OrderBy(p => p))}.");

        if (!options.UsesRealModel)
            return;

        var missing = new List<string>();
        void Require(string role, EndpointOptions ep)
        {
            if (string.IsNullOrWhiteSpace(ep.Model)) missing.Add(role);
        }

        Require("Chat", options.Chat);
        Require("Extraction", options.ExtractionOrChat);
        Require("Summarizer", options.SummarizerOrChat);
        Require("Embeddings", options.Embeddings);
        if (options.Vision is { } v) Require("Vision", v);
        if (options.Transcription is { } t) Require("Transcription", t);
        if (options.Speech is { } s) Require("Speech", s);

        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"Provider '{options.Provider}' is selected but these roles have no model name configured: " +
                $"{string.Join(", ", missing)}. Set Models.<Role>.Model in configuration.");
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
                "Export it first (the 'export' command), then delete the database file and re-run — " +
                "it will be recreated, after which you can 'import' the snapshot.");
        }

        // Safe upgrade path: back up the existing database before applying any pending migration,
        // and restore it if the migration fails, so a schema change can never lose memories.
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        var dbPath = db.Database.GetDbConnection().DataSource;
        string? backup = null;
        if (pending.Count > 0 && !string.IsNullOrWhiteSpace(dbPath) && File.Exists(dbPath))
        {
            backup = DatabaseMaintenance.Backup(dbPath, DateTimeOffset.UtcNow);
            if (backup is not null)
            {
                var log = scope.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("Companion.Migrations");
                log?.LogInformation("Backed up the database to {Backup} before applying {Count} migration(s).", backup, pending.Count);
            }
        }

        try
        {
            await db.Database.MigrateAsync(ct);
        }
        catch (Exception ex)
        {
            // Restore the pre-migration backup so the user keeps their data, then surface a clear error.
            if (backup is not null && File.Exists(backup))
            {
                try
                {
                    await db.Database.GetDbConnection().CloseAsync();
                    File.Copy(backup, dbPath!, overwrite: true);
                }
                catch { /* best effort — the backup file still exists for manual recovery */ }
                throw new InvalidOperationException(
                    $"Migration failed and the database was restored from the pre-migration backup at '{backup}'. " +
                    "No data was lost. See the inner exception for details.", ex);
            }
            throw;
        }
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
