using Companion.Core;
using Companion.Core.Abstractions;
using Companion.Core.Services;
using Companion.Infrastructure.Cognition;
using Companion.Infrastructure.Models;
using Companion.Infrastructure.World;
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
        services.Configure<CognitiveModelOptions>(configuration.GetSection(CognitiveModelOptions.Section));
        services.Configure<SafetyOptions>(configuration.GetSection(SafetyOptions.Section));
        AddCognitiveModels(services, configuration);
        services.AddSingleton<IPersonalityService, PersonalityService>();

        services.AddSingleton(TimeProvider.System);

        // The active user is derived from trusted execution context, never from a request body.
        // Local single-user default; a future auth boundary replaces this registration only.
        var localUserId = configuration["User:Id"] ?? Seeding.CompanionSeeder.DemoUserId;
        services.AddSingleton<IUserContext>(new FixedUserContext(localUserId));

        services.AddDbContext<CompanionDbContext>(o => o.UseSqlite(sqliteConnectionString));

        // Durable operational telemetry (model calls, tool calls). Singleton because its writers
        // are singletons (the model decorators); it opens its own scope per operation.
        services.AddSingleton<IDiagnosticsStore, DiagnosticsStore>();

        // Stores (authoritative) + vector index (derived).
        services.AddScoped<IConversationStore, ConversationStore>();
        services.AddScoped<IMemoryStore, MemoryStore>();
        services.AddScoped<IProjectStore, ProjectStore>();
        services.AddScoped<IProfileStore, ProfileStore>();
        services.AddScoped<IFeedbackStore, FeedbackStore>();
        services.AddScoped<IPendingClarificationStore, PendingClarificationStore>();
        services.AddScoped<IEmotionStore, EmotionStore>();
        services.AddScoped<IReflectionStore, ReflectionStore>();
        services.AddScoped<IExperienceStore, ExperienceStore>();
        services.AddScoped<IAnticipationStore, AnticipationStore>();
        services.AddScoped<IPreferenceStore, PreferenceStore>();
        services.AddScoped<IAttentionStore, AttentionStore>();
        services.AddScoped<IMemoryAssociationStore, MemoryAssociationStore>();
        services.AddScoped<ISharedPerspectiveStore, SharedPerspectiveStore>();
        services.AddScoped<IProcedureStore, ProcedureStore>();
        services.AddScoped<ICapabilityRegistry, CapabilityRegistry>();
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

        // Stop sequences for conversation, unless the config sets its own. A default rather than a
        // documented setting on purpose: an existing installation should get this without editing
        // anything, because the failure it prevents — her continuing the context packet's structure
        // into the reply — looks like the model being broken rather than a missing option.
        modelOptions.Chat.Stop ??= ConversationStopSequences;

        // Make the reply reserve real. Reserving room for the answer and then not telling the model
        // about it left the arithmetic half-done: one observed reply ran to ~3,200 tokens against a
        // 4,096-token window with a ~1,700-token prompt already in it, so the server was shifting
        // context in the middle of writing — dropping her identity and standing rules partway
        // through a reply, which reads as her wandering off mid-thought.
        modelOptions.Chat.MaxTokens ??= modelOptions.Chat.ReplyReserveTokens;

        // Size the prompt to the window the chat model is actually served with, unless the config
        // states a budget itself. Derived rather than configured by default because the two
        // numbers must agree and only one of them is observable: an operator who changes the chat
        // model, or restarts Ollama with a different OLLAMA_CONTEXT_LENGTH, would otherwise have
        // to remember to change a second setting in a different section — and the symptom of
        // forgetting is not an error but a companion who quietly stops remembering things.
        var configuredBudget = configuration.GetSection(CompanionOptions.SectionName)
            .GetValue<int?>("PromptTokenBudget");
        services.PostConfigure<CompanionOptions>(options =>
        {
            if (configuredBudget is null)
                options.PromptTokenBudget = modelOptions.Chat.PromptBudgetTokens;
        });

        services.AddSingleton(modelOptions); // so the CLI can see which optional models are configured

        // Model HTTP access goes through Microsoft's IHttpClientFactory: one named client per role,
        // per-role timeout/base-url, managed handler lifetime, and a Polly transient-retry policy.
        services.AddModelHttpClients(modelOptions);

        // Her world, if she has one. Absent by default — a companion with no world is the normal
        // case, and nothing else here depends on having one. This is a connection and nothing
        // more: no places are stored, so deleting the world leaves nothing behind in companion.db.
        var worldOptions = configuration.GetSection(WorldOptions.SectionName).Get<WorldOptions>() ?? new WorldOptions();
        services.AddSingleton(worldOptions);
        services.AddSingleton<WebSocketWorldLink>();
        services.AddSingleton<IWorldLink>(sp => sp.GetRequiredService<WebSocketWorldLink>());

        // A model name is the one setting nothing else validates — a typo or an un-pulled tag
        // builds, starts, and tests clean, then 503s on the first real sentence. The preflight
        // checks the provider's catalog once at startup and reports what's actually missing.
        // Registered even offline (where it no-ops) so the host doesn't need to branch; the bare
        // AddHttpClient keeps IHttpClientFactory resolvable when no named clients were registered.
        services.AddHttpClient();
        services.AddSingleton<ModelPreflight>();
        services.AddSingleton<ModelPreflightState>();

        // Present is not working. The catalog probe above would have said the extraction model was
        // fine on every one of the weeks it was quietly finding nothing, because it was installed
        // and responsive and simply not up to the task. Only a real call answers that, and only for
        // a real model — the offline extractor is deterministic and proves nothing.
        if (modelOptions.UsesRealModel)
            services.AddSingleton<ExtractionSelfTest>();

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
            // Every role is wrapped in call telemetry (duration, tokens, outcome per role+model),
            // so model comparisons come from recorded usage instead of impressions.
            IChatModel BuildChat(IServiceProvider sp, EndpointOptions ep, string role) =>
                new LoggingChatModel(
                    new OpenAiCompatibleChatModel(
                        ep, sp.GetRequiredService<IHttpClientFactory>(), ProviderHttpClients.Name(role),
                        sp.GetRequiredService<ILogger<OpenAiCompatibleChatModel>>()),
                    role,
                    sp.GetRequiredService<IDiagnosticsStore>(),
                    sp.GetRequiredService<TimeProvider>());

            services.AddKeyedSingleton<IChatModel>(ChatRoles.Conversation, (sp, _) => BuildChat(sp, modelOptions.Chat, ProviderHttpClients.Conversation));
            services.AddKeyedSingleton<IChatModel>(ChatRoles.Extraction, (sp, _) => BuildChat(sp, modelOptions.ExtractionOrChat, ProviderHttpClients.Extraction));
            services.AddKeyedSingleton<IChatModel>(ChatRoles.Summarizer, (sp, _) => BuildChat(sp, modelOptions.SummarizerOrChat, ProviderHttpClients.Summarizer));
            services.AddKeyedSingleton<IChatModel>(ChatRoles.Reranker, (sp, _) => BuildChat(sp, modelOptions.RerankerOrSummarizer, ProviderHttpClients.Reranker));
            services.AddKeyedSingleton<IChatModel>(ChatRoles.Safety, (sp, _) => BuildChat(sp, modelOptions.SafetyOrExtraction, ProviderHttpClients.Safety));
            services.AddKeyedSingleton<IChatModel>(ChatRoles.TaskAuditor, (sp, _) => BuildChat(sp, modelOptions.TaskAuditorOrSummarizer, ProviderHttpClients.TaskAuditor));
            services.AddKeyedSingleton<IChatModel>(ChatRoles.ToolPlanner, (sp, _) => BuildChat(sp, modelOptions.ToolPlannerOrExtraction, ProviderHttpClients.ToolPlanner));

            // The default IChatModel (the assistant's reply) is the conversational one.
            services.AddSingleton<IChatModel>(sp => sp.GetRequiredKeyedService<IChatModel>(ChatRoles.Conversation));

            // Cache outermost: a repeat of the same text costs nothing and is not recorded as a
            // provider call, so telemetry keeps showing real traffic.
            services.AddSingleton<IEmbeddingModel>(sp =>
                new CachingEmbeddingModel(
                    new LoggingEmbeddingModel(
                        new OpenAiCompatibleEmbeddingModel(
                            modelOptions.Embeddings, sp.GetRequiredService<IHttpClientFactory>(),
                            ProviderHttpClients.Name(ProviderHttpClients.Embeddings),
                            sp.GetRequiredService<ILogger<OpenAiCompatibleEmbeddingModel>>()),
                        sp.GetRequiredService<IDiagnosticsStore>(),
                        sp.GetRequiredService<TimeProvider>())));

            services.AddSingleton<ISummarizer>(sp =>
                new LlmSummarizer(sp.GetRequiredKeyedService<IChatModel>(ChatRoles.Summarizer)));
            services.AddScoped<IMemoryExtractor>(sp =>
                new LlmMemoryExtractor(
                    sp.GetRequiredKeyedService<IChatModel>(ChatRoles.Extraction),
                    sp.GetRequiredService<ILogger<LlmMemoryExtractor>>()));

            services.AddSingleton<IMemoryReranker>(sp => new LlmMemoryReranker(
                sp.GetRequiredKeyedService<IChatModel>(ChatRoles.Reranker),
                new RuleBasedMemoryReranker(),
                sp.GetRequiredService<ILogger<LlmMemoryReranker>>()));
            services.AddSingleton<IPrivacyClassifier>(sp => new LlmPrivacyClassifier(
                sp.GetRequiredKeyedService<IChatModel>(ChatRoles.Safety),
                new RuleBasedPrivacyClassifier(),
                sp.GetRequiredService<ILogger<LlmPrivacyClassifier>>()));

            // The semantic completion check runs on a cheap task-auditor role, never the big one.
            services.AddSingleton<ICompletionJudge>(sp => new LlmCompletionJudge(
                sp.GetRequiredKeyedService<IChatModel>(ChatRoles.TaskAuditor),
                sp.GetRequiredService<ILogger<LlmCompletionJudge>>()));
        }
        else
        {
            // The mocks get the same telemetry wrapper: the diagnostics pipeline stays the one
            // code path whether the models are real or offline stand-ins.
            services.AddSingleton<IEmbeddingModel>(sp => new CachingEmbeddingModel(
                new LoggingEmbeddingModel(
                    new MockEmbeddingModel(), sp.GetRequiredService<IDiagnosticsStore>(),
                    sp.GetRequiredService<TimeProvider>())));
            services.AddSingleton<IChatModel>(sp => new LoggingChatModel(
                new MockChatModel(), ProviderHttpClients.Conversation,
                sp.GetRequiredService<IDiagnosticsStore>(), sp.GetRequiredService<TimeProvider>()));
            services.AddSingleton<ISummarizer, MockSummarizer>();
            services.AddScoped<IMemoryExtractor, RuleBasedMemoryExtractor>();

            // Offline replies are deterministic and self-contained — nothing to continue.
            services.AddSingleton<ICompletionJudge, AlwaysCompleteJudge>();
            services.AddSingleton<IMemoryReranker, RuleBasedMemoryReranker>();
            services.AddSingleton<IPrivacyClassifier, RuleBasedPrivacyClassifier>();
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

        // The cross-encoder reranker goes on last, after the provider block has chosen a reranker,
        // so it decorates that choice and falls back to it whenever the model has no opinion.
        //
        // Two flags, not one: loading a model so it can be measured in shadow is a different
        // decision from putting it in the path of every turn, and collapsing them is how a model
        // gets promoted for the crime of being present.
        var cognitive = configuration.GetSection(CognitiveModelOptions.Section).Get<CognitiveModelOptions>()
            ?? new CognitiveModelOptions();
        if (cognitive.Reranker.Enabled && cognitive.RerankMemories)
        {
            services.AddSingleton<IMemoryReranker>(sp => new CrossEncoderMemoryReranker(
                sp.GetRequiredService<ITextPairScorer>(),
                new RuleBasedMemoryReranker(),
                sp.GetRequiredService<ILogger<CrossEncoderMemoryReranker>>()));
        }

        // The reply gate. Off unless asked for, and watching rather than acting even then — see
        // SafetyOptions. Uses the Safety chat role, which falls back to Extraction, so enabling it
        // does not require another model on the roster.
        var safety = configuration.GetSection(SafetyOptions.Section).Get<SafetyOptions>() ?? new SafetyOptions();
        if (safety.Enabled && modelOptions.UsesRealModel)
        {
            services.AddSingleton<IReplyGate>(sp => new LlmReplyGate(
                sp.GetRequiredKeyedService<IChatModel>(ChatRoles.Safety),
                safety, sp.GetRequiredService<ILogger<LlmReplyGate>>()));
        }
        else
        {
            services.AddSingleton<IReplyGate, OpenReplyGate>();
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
        services.AddScoped<IAttentionService, AttentionService>();
        services.AddScoped<IAssociativeRecallService, AssociativeRecallService>();
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

        // The tool layer: read-only capabilities the chat model may intentionally invoke, plus
        // the bounded loop that mediates every call and the diagnostics ring that records them.
        services.AddSingleton<ITurnTraceLog, InMemoryTurnTraceLog>();
        services.AddScoped<ICompanionTool, Core.Services.Tools.CapabilityListTool>();
        services.AddScoped<ICompanionTool, Core.Services.Tools.MemorySearchTool>();
        services.AddScoped<ICompanionTool, Core.Services.Tools.ProjectTool>();
        services.AddScoped<ICompanionTool, Core.Services.Tools.OpenLoopListTool>();
        services.AddScoped<ICompanionTool, Core.Services.Tools.ProcedureSearchTool>();
        services.AddScoped<ICompanionTool, Core.Services.Tools.PreferenceListTool>();
        services.AddScoped<ICompanionTool, Core.Services.Tools.DiagnosticsTool>();
        // The loop plans with the executive ToolPlanner role (its own small model, falling back
        // to Extraction then Chat), NOT the conversational model: orchestration is a judgment
        // job for an instruction-follower, and the RP model should specialize in being herself.
        services.AddScoped<ToolLoop>(sp => new ToolLoop(
            sp.GetServices<ICompanionTool>(),
            modelOptions.UsesRealModel
                ? sp.GetRequiredKeyedService<IChatModel>(ChatRoles.ToolPlanner)
                : sp.GetRequiredService<IChatModel>(),
            sp.GetRequiredService<IDiagnosticsStore>(),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CompanionOptions>>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<ToolLoop>>()));

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
    /// <summary>
    /// Registers the specialist-model runtime. Every model is a singleton — loading an ONNX graph
    /// costs tens to hundreds of milliseconds and as much memory again, and a per-request copy
    /// would pay both on every turn — and every one is optional.
    ///
    /// Nothing here fails when a model is absent. That is the whole contract: a specialist model
    /// improves on a heuristic that still exists, so "not configured" is the default state and
    /// "configured but missing" is a warning and a fallback, never a dead companion. The exception
    /// is an entry marked Required, which is how an operator asks to be told immediately.
    ///
    /// Models live beside the database rather than the binaries: that is the directory a person
    /// backs up and carries between machines, and the roster differs per machine for the same
    /// reason the Ollama one does.
    /// </summary>
    private static void AddCognitiveModels(IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(CognitiveModelOptions.Section).Get<CognitiveModelOptions>()
            ?? new CognitiveModelOptions();
        var directory = ResolveModelDirectory(options.Directory, configuration["Database:Path"]);
        var timeout = TimeSpan.FromMilliseconds(Math.Max(50, options.TimeoutMilliseconds));

        services.AddSingleton<ITextPairScorer>(sp => options.Reranker.Enabled
            ? new OnnxTextPairScorer(
                "reranker", options.Reranker, directory, timeout,
                sp.GetRequiredService<ILogger<OnnxTextPairScorer>>())
            : new UnavailableTextPairScorer("reranker", "disabled"));

        // Shadow mode costs a real inference per turn whose answer is then discarded, so when it is
        // off the recorder says so and callers skip the work entirely rather than paying for it.
        if (options.ShadowMode)
            services.AddSingleton<IShadowRecorder, ShadowRecorder>();
        else
            services.AddSingleton<IShadowRecorder, NullShadowRecorder>();

        services.AddSingleton<INliModel>(sp => options.Nli.Enabled
            ? new OnnxNliModel(
                "nli", options.Nli, directory, timeout, sp.GetRequiredService<ILogger<OnnxNliModel>>())
            : new UnavailableNliModel("nli", "disabled"));

        // Phase 5 supplies the classifier; the seam and the honest "not here" exist now so callers
        // can be written against the interface rather than around its absence.
        services.AddSingleton<ITextClassifier>(_ => new UnavailableTextClassifier(
            "classifier",
            options.Classifier.Enabled ? "not implemented yet — see docs/SPECIALIST_MODELS.md" : "disabled"));
    }

    /// <summary>
    /// Model files sit next to the database unless an absolute path says otherwise, so a relative
    /// directory follows the data rather than the working directory of whoever launched the process.
    /// </summary>
    private static string ResolveModelDirectory(string directory, string? databasePath)
    {
        if (string.IsNullOrWhiteSpace(directory))
            directory = "models";
        if (Path.IsPathRooted(directory))
            return directory;

        var databaseDirectory = string.IsNullOrWhiteSpace(databasePath)
            ? null
            : Path.GetDirectoryName(Path.GetFullPath(databasePath));

        return Path.Combine(
            string.IsNullOrWhiteSpace(databaseDirectory) ? Directory.GetCurrentDirectory() : databaseDirectory,
            directory);
    }

    /// <summary>The provider values the app understands. Anything else is a configuration error.</summary>
    private static readonly HashSet<string> SupportedProviders =
        new(StringComparer.OrdinalIgnoreCase) { "Mock", "OpenAiCompatible", "Ollama", "LMStudio" };

    /// <summary>
    /// Shapes that only ever appear in her context packet, never in speech.
    ///
    /// Two families. Document structure — a horizontal rule, a markdown heading — which she copies
    /// from the packet's form rather than its words. And conversational role markers, which are the
    /// dangerous ones: without them the model does not stop at the end of *her* turn, and writes
    /// the user's next line, then its own reply, then continues — inventing an entire dialogue from
    /// a single message and escalating it without any input at all. One observed completion ran to
    /// several fabricated turns and arrived somewhere far worse than anything that was asked.
    ///
    /// Fabricated turns are not merely wrong output: they are stored as her reply, fed back as
    /// context next turn, and can reach extraction as though the user had said them.
    ///
    /// Deliberately not bullets — she writes lists legitimately.
    /// </summary>
    private static readonly string[] ConversationStopSequences =
    {
        "\n---", "\n## ",
        "\nuser", "\nUser:", "\nUSER:",
        "\nassistant", "\nAssistant:", "\nASSISTANT:",
        "\n<|", "\n[USER]", "\n[COMPANION]",
    };

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
        Require("Reranker", options.RerankerOrSummarizer);
        Require("Safety", options.SafetyOrExtraction);
        Require("TaskAuditor", options.TaskAuditorOrSummarizer);
        Require("ToolPlanner", options.ToolPlannerOrExtraction);
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
            await EnableWriteAheadLoggingAsync(db, ct);
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

    /// <summary>
    /// Puts a file-backed database in WAL mode. In the default rollback journal, a single writer
    /// blocks every reader — so the reflection worker consolidating memories while the user is
    /// mid-turn can surface "database is locked". WAL lets readers continue during a write, which
    /// is exactly this app's shape: constant small reads on the turn path, occasional background
    /// writes. The setting is persisted in the database header, so this runs once and sticks.
    /// (In-memory databases used by tests have no journal to switch; SQLite reports "memory" and
    /// the call is a harmless no-op.)
    /// </summary>
    private static async Task EnableWriteAheadLoggingAsync(CompanionDbContext db, CancellationToken ct)
    {
        try
        {
            // synchronous=NORMAL is the documented companion to WAL: durable across process
            // crashes, and only at risk from an OS/power failure — the right trade for a local
            // companion whose alternative is fsync on every commit.
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
            await db.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;", ct);
        }
        catch (Exception)
        {
            // A database that won't take the pragma still works in rollback-journal mode.
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
