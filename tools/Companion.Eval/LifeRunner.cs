using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Infrastructure;
using Companion.Infrastructure.Seeding;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Companion.Eval;

/// <summary>
/// Runs a generated life through the <em>real</em> extractor and the <em>real</em> pipeline, and
/// diffs the resulting store against what the generator said should be there.
///
/// It does not generate replies. Extraction can only cite the user's own messages as evidence, so
/// the companion's side of the conversation has almost no bearing on what is stored â€” and it costs
/// about seventy-seven of the eighty seconds a turn takes. Dropping it is what turns "a life an
/// hour" into "a life a minute", and the part actually under test, the 7B extractor and the
/// validate-before-store pipeline, is untouched and real.
///
/// The reply-shaped faults â€” interrogation, a leaked scratchpad, answering as though the user's
/// allotment were hers â€” are invisible here by construction, and stay the soak harness's job on the
/// model that actually produces them.
/// </summary>
public sealed class LifeRunner : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly string _databasePath;

    private LifeRunner(ServiceProvider services, string databasePath)
    {
        _services = services;
        _databasePath = databasePath;
    }

    public static async Task<LifeRunner> StartAsync(string extractionModel, string ollamaUrl)
    {
        // A scratch database per run, never the real one: several thousand synthetic facts about a
        // fictional person is not something to mix into her memory of an actual one.
        var path = Path.Combine(Path.GetTempPath(), $"companion-lives-{Guid.NewGuid():N}.db");

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Models:Provider"] = "OpenAiCompatible",
            // The chat roles still need a name to pass startup validation; nothing calls them.
            ["Models:Chat:BaseUrl"] = ollamaUrl,
            ["Models:Chat:Model"] = extractionModel,
            ["Models:Extraction:BaseUrl"] = ollamaUrl,
            ["Models:Extraction:Model"] = extractionModel,
            ["Models:Extraction:Temperature"] = "0.2",
            ["Models:Embeddings:BaseUrl"] = ollamaUrl,
            ["Models:Embeddings:Model"] = "nomic-embed-text",
            ["Models:Embeddings:Dimensions"] = "768",
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Error);
            // The one thing worth hearing from inside a run: why a fact did or didn't replace another.
            b.AddFilter("Companion.Core.Services.MemoryPipeline", LogLevel.Debug);
            b.AddSimpleConsole(o => o.SingleLine = true);
        });
        services.AddCompanion(configuration, $"Data Source={path}");
        var provider = services.BuildServiceProvider();
        await provider.MigrateDatabaseAsync();

        return new LifeRunner(provider, path);
    }

    /// <summary>Feeds one life through the pipeline and returns how the store ended up.</summary>
    public async Task<LifeResult> RunAsync(Life life, CancellationToken ct = default)
    {
        // A user per life, so lives cannot contaminate each other â€” the whole store is scoped by
        // user id in the query itself, which is what makes this safe to do in one database.
        var userId = $"{CompanionSeeder.DemoUserId}-{life.Name}";
        var conversationId = Guid.NewGuid();
        var failures = new List<string>();

        using var scope = _services.CreateScope();
        var pipeline = scope.ServiceProvider.GetRequiredService<IMemoryPipeline>();
        var conversations = scope.ServiceProvider.GetRequiredService<IConversationStore>();
        var conversation = await conversations.StartConversationAsync(userId, life.Name, "eval", "lives", ct);

        foreach (var text in life.Messages)
        {
            var message = new Message
            {
                Id = Guid.NewGuid(),
                ConversationId = conversation.Id,
                UserId = userId,
                Role = MessageRole.User,
                Content = text,
                Timestamp = DateTimeOffset.UtcNow,
            };
            await conversations.AddMessageAsync(message, ct);

            try
            {
                await pipeline.ProcessAsync(userId, new[] { message }, resolution: null, ct);
            }
            catch (Exception ex)
            {
                failures.Add($"{text} -> {ex.GetType().Name}: {ex.Message}");
            }
        }

        var store = scope.ServiceProvider.GetRequiredService<IMemoryStore>();
        var stored = (await store.GetRetrievableMemoriesAsync(userId, ct))
            .OfType<SemanticMemory>()
            .ToList();

        return new LifeResult(life, stored, failures);
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        try { File.Delete(_databasePath); } catch (IOException) { /* a leftover scratch file is harmless */ }
    }
}

/// <summary>One life, and what the store actually held afterwards.</summary>
public sealed record LifeResult(Life Life, IReadOnlyList<SemanticMemory> Stored, IReadOnlyList<string> Failures)
{
    /// <summary>
    /// Checks each expectation against the store. Matching is on the predicate plus a distinctive
    /// keyword rather than the exact sentence, because the extractor legitimately paraphrases â€” the
    /// question is whether the right fact is in the right slot, not whether it chose our wording.
    /// </summary>
    public IEnumerable<Mismatch> Check()
    {
        foreach (var want in Life.Expected)
        {
            var hits = Stored
                .Where(m => string.Equals(m.Predicate, want.Predicate, StringComparison.OrdinalIgnoreCase))
                .Where(m => Mentions(m, want.Keyword))
                .ToList();

            var active = hits.Where(m => m.Status == MemoryStatus.Active).ToList();

            if (want.ShouldBeActive && active.Count == 0)
            {
                yield return new Mismatch(Life.Name, want,
                    hits.Count > 0 ? "present but not current" : "missing entirely", hits.Count, Slot(want));
            }
            else if (!want.ShouldBeActive && active.Count > 0)
            {
                yield return new Mismatch(Life.Name, want,
                    "still current when it should not be", active.Count, Slot(want));
            }
            else if (want.ShouldBeActive && active.Count > 1)
            {
                yield return new Mismatch(Life.Name, want, "stored more than once", active.Count, Slot(want));
            }
        }
    }

    /// <summary>
    /// Everything the store actually holds that could plausibly be the fact in question â€” the same
    /// predicate, or any predicate whose value mentions the keyword.
    ///
    /// Without this a mismatch says only that something is wrong, and the interesting part is
    /// always WHERE the fact ended up instead: a different slot, a different subject, or nowhere.
    /// Reading that off a report beats reasoning about it from the code, which is how a plausible
    /// theory becomes a fix that does something else.
    /// </summary>
    private IReadOnlyList<string> Slot(Expectation want)
        => Stored
            .Where(m => string.Equals(m.Predicate, want.Predicate, StringComparison.OrdinalIgnoreCase)
                || Mentions(m, want.Keyword))
            .Select(m => $"{m.Subject}/{m.Predicate} = \"{m.Value}\" [{m.Status}]  content: {m.NormalizedFact}")
            .Distinct()
            .ToList();

    private static bool Mentions(SemanticMemory memory, string keyword)
        => (memory.Value ?? "").Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || (memory.NormalizedFact ?? "").Contains(keyword, StringComparison.OrdinalIgnoreCase);
}

public sealed record Mismatch(
    string Life, Expectation Expected, string Problem, int Count,
    IReadOnlyList<string> Slot);
