using Companion.Core;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Companion.Infrastructure;
using Companion.Infrastructure.Seeding;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Companion.Eval;

/// <summary>
/// The pipeline on its own, with no model of any kind in the loop.
///
/// Tier 1 sends a sentence and lets the real 7B decide what to extract, which costs about four and
/// a half seconds a message — fine for hundreds, seven days for ten thousand. Tier 0 hands the
/// pipeline the candidate a <em>correct</em> extractor would have produced and asks only what the
/// pipeline does with it. No network, no GPU, microseconds.
///
/// The speed is the smaller half of the point. Running both tiers separates two questions this
/// project has repeatedly conflated:
///
///   Tier 0 fails  →  the pipeline mishandles a correct candidate. A rule is wrong.
///   Tier 0 passes, Tier 1 fails  →  the rule is right and extraction did not produce the input.
///
/// Every bug found today lived in the first category — slot keys, cardinality, subject attribution,
/// duplicate absorption — and each was hunted through eighty-second turns that could not tell the
/// two apart.
///
/// What it deliberately cannot answer: whether the thresholds are right. The mock embedding is
/// token-based and its geometry is not nomic's, so Tier 0 tests the LOGIC given a similarity, never
/// the similarity itself. Threshold calibration stays measured against the real model.
/// </summary>
public sealed class Tier0Runner : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    private Tier0Runner(ServiceProvider services) => _services = services;

    public static async Task<Tier0Runner> StartAsync()
    {
        // Mock provider: MockEmbeddingModel is deterministic and in-process, so a run is
        // reproducible and costs nothing. Shared-cache in-memory SQLite keeps the real store,
        // because the store's own behaviour — scoping, statuses, revisions — is under test too.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Models:Provider"] = "Mock" })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Error));
        services.AddCompanion(
            configuration, $"Data Source=file:tier0-{Guid.NewGuid():N}?mode=memory&cache=shared");
        var provider = services.BuildServiceProvider();
        await provider.MigrateDatabaseAsync();
        return new Tier0Runner(provider);
    }

    public async Task<Tier0Result> RunAsync(PipelineScenario scenario, CancellationToken ct = default)
    {
        var userId = $"{CompanionSeeder.DemoUserId}-{scenario.Name}";
        using var scope = _services.CreateScope();
        var sp = scope.ServiceProvider;

        var store = sp.GetRequiredService<IMemoryStore>();
        var embeddings = sp.GetRequiredService<IEmbeddingModel>();
        var conversations = sp.GetRequiredService<IConversationStore>();
        var clock = sp.GetRequiredService<TimeProvider>();
        var conversation = await conversations.StartConversationAsync(userId, scenario.Name, "eval", "tier0", ct);

        foreach (var step in scenario.Steps)
        {
            var message = new Message
            {
                Id = Guid.NewGuid(),
                ConversationId = conversation.Id,
                UserId = userId,
                Role = MessageRole.User,
                Content = step.Said,
                Timestamp = clock.GetUtcNow(),
            };
            await conversations.AddMessageAsync(message, ct);

            // The candidate is built here rather than extracted, with its evidence pointing at the
            // message just stored — which is what the pipeline's provenance checks require, and
            // what makes this a test of the pipeline rather than of the extractor.
            var candidate = step.Candidate with
            {
                Evidence = new[] { new CandidateEvidence(message.Id, step.Excerpt ?? step.Said) },
            };

            var pipeline = new MemoryPipeline(
                new FixedExtractor(candidate), store,
                new MemoryCurator(store, embeddings, clock, NullLogger<MemoryCurator>.Instance),
                embeddings,
                sp.GetRequiredService<IProfileStore>(),
                sp.GetRequiredService<IPersonalityService>(),
                Options.Create(RuleOnly()),
                clock, NullLogger<MemoryPipeline>.Instance);

            await pipeline.ProcessAsync(userId, new[] { message }, ct);
        }

        var stored = (await store.GetRetrievableMemoriesAsync(userId, ct)).OfType<SemanticMemory>().ToList();
        return new Tier0Result(scenario, stored);
    }


    /// <summary>
    /// Similarity taken out of the way, so this tier measures the RULES and nothing else.
    ///
    /// The mock embedding's geometry is not nomic's, and leaving the production thresholds in place
    /// made fourteen correct behaviours look like failures: "dislikes coriander" against "dislikes
    /// olives" scores 0.5 on the mock and 0.753 on the real model, so the replacement bar of 0.6
    /// fell on opposite sides of the same case. Asserting an outcome that depends on which embedder
    /// is loaded would make this suite a measure of the mock.
    ///
    /// Threshold calibration belongs to Tier 1, against the model that actually runs.
    /// </summary>
    private static CompanionOptions RuleOnly() => new()
    {
        DuplicateSimilarityThreshold = 0.99,
        ContradictionSimilarityThreshold = 0.0,
        ReplacementSimilarityThreshold = 0.0,
    };

    public ValueTask DisposeAsync() => _services.DisposeAsync();

    /// <summary>Returns exactly the candidate the scenario specified — the whole point of the tier.</summary>
    private sealed class FixedExtractor : IMemoryExtractor
    {
        private readonly MemoryCandidate _candidate;
        public FixedExtractor(MemoryCandidate candidate) => _candidate = candidate;

        public Task<IReadOnlyList<MemoryCandidate>> ExtractAsync(
            string userId, IReadOnlyList<Message> exchange, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MemoryCandidate>>(new[] { _candidate });
    }
}

/// <summary>One thing said, and the candidate a correct extractor would produce from it.</summary>
public sealed record CandidateStep(string Said, MemoryCandidate Candidate, string? Excerpt = null);

/// <summary>A pipeline-level scenario: candidates in, expected store state out.</summary>
public sealed record PipelineScenario(
    string Name,
    Provenance Provenance,
    IReadOnlyList<CandidateStep> Steps,
    IReadOnlyList<Expectation> Expected);

public sealed record Tier0Result(PipelineScenario Scenario, IReadOnlyList<SemanticMemory> Stored)
{
    public IEnumerable<Mismatch> Check()
    {
        foreach (var want in Scenario.Expected)
        {
            // Whole-value equality, not substring. Tier 0 supplies the values itself, so they are
            // known exactly — and substring matching reported a correct result as a failure
            // twenty-six times: asked whether "Scott" was still current, it found "Scott Donald"
            // and said yes. Tier 1 keeps substring matching, because there the extractor
            // paraphrases and the exact wording is not ours to predict.
            var active = Stored
                .Where(m => string.Equals(m.Predicate, want.Predicate, StringComparison.OrdinalIgnoreCase))
                .Where(m => string.Equals(
                    (m.Value ?? "").Trim(), want.Keyword.Trim(), StringComparison.OrdinalIgnoreCase))
                .Where(m => m.Status == MemoryStatus.Active)
                .ToList();

            var slot = Stored
                .Select(m => $"{m.Subject}/{m.Predicate} = \"{m.Value}\" [{m.Status}]")
                .ToList();

            if (want.ShouldBeActive && active.Count == 0)
                yield return new Mismatch(Scenario.Name, want, "missing or retired", 0, slot);
            else if (!want.ShouldBeActive && active.Count > 0)
                yield return new Mismatch(Scenario.Name, want, "still current", active.Count, slot);
            else if (want.ShouldBeActive && active.Count > 1)
                yield return new Mismatch(Scenario.Name, want, "stored more than once", active.Count, slot);
        }
    }
}
