using Companion.Core;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Pair capture: the supersession decision recorded as the (incoming, existing) pair the pipeline
/// actually judged, with the incumbent's outcome as a weak label.
///
/// This is the input the specialised supersession model will be trained and judged on — see
/// docs/SUPERSESSION_TASK.md §2 and §7. Message-level capture cannot produce it, because only the
/// pipeline knows which existing memory was in play and what the code decided. The tests here pin
/// the three promises that make the corpus usable: the pair is recorded at every decision exit
/// including the negative one (a model trained only on pairs that superseded learns that
/// everything supersedes), the row carries provenance and not the whole conversation, and the
/// existing privacy machinery — the capture gate, the credential redaction, the /forget purge —
/// applies to pair rows exactly as it does to sentence rows.
/// </summary>
public class SupersessionPairCaptureTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);
    private const string User = "pair-user";
    private static readonly Guid Conversation = Guid.NewGuid();

    private static Message UserMsg(string text) => new()
    {
        Id = Guid.NewGuid(),
        ConversationId = Conversation,
        UserId = User,
        Role = MessageRole.User,
        Content = text,
        Timestamp = Now,
    };

    private static MemoryCandidate Fact(
        string predicate, string value, string content, Message source, string excerpt) => new()
        {
            Kind = MemoryKind.Semantic,
            Subject = "user",
            Predicate = predicate,
            Value = value,
            Content = content,
            ProposedConfidence = 0.9,
            Evidence = new[] { new CandidateEvidence(source.Id, excerpt) },
        };

    /// <summary>
    /// Same shape as MemoryFidelityTests: similarity thresholds out of the way, so the rules under
    /// test are cardinality and wording rather than the mock embedding's geometry.
    /// </summary>
    private static CompanionOptions RuleOnly() => new()
    {
        DuplicateSimilarityThreshold = 0.99,
        ContradictionSimilarityThreshold = 0.0,
        ReplacementSimilarityThreshold = 0.0,
    };

    private static (MemoryPipeline Pipeline, IShadowRecorder Recorder) Build(
        IServiceProvider sp, TimeProvider clock, params MemoryCandidate[] candidates)
    {
        var store = sp.GetRequiredService<IMemoryStore>();
        var embeddings = sp.GetRequiredService<IEmbeddingModel>();
        var recorder = sp.GetRequiredService<IShadowRecorder>();
        var pipeline = new MemoryPipeline(
            new StubExtractor(candidates), store,
            new MemoryCurator(store, embeddings, clock, NullLogger<MemoryCurator>.Instance, recorder),
            embeddings,
            sp.GetRequiredService<IProfileStore>(),
            sp.GetRequiredService<IPersonalityService>(),
            Options.Create(RuleOnly()), clock, NullLogger<MemoryPipeline>.Instance,
            shadow: recorder,
            capture: new CognitiveCapture(recorder));
        return (pipeline, recorder);
    }

    private static TestHost Host() => new(Now, settings: new Dictionary<string, string?>
    {
        ["CognitiveModels:Capture"] = "true",
    });

    private static async Task<SemanticMemory> Seed(
        IServiceScope scope, string predicate, string value, string fact, DateTimeOffset observed)
    {
        var store = scope.ServiceProvider.GetRequiredService<IMemoryStore>();
        var embeddings = scope.ServiceProvider.GetRequiredService<IEmbeddingModel>();
        var memory = new SemanticMemory
        {
            Id = Guid.NewGuid(),
            UserId = User,
            Subject = "user",
            Predicate = predicate,
            Value = value,
            NormalizedFact = fact,
            Confidence = 0.9,
            Status = MemoryStatus.Active,
            FirstObserved = observed,
            LastConfirmed = observed,
            CreatedAt = observed,
            Embedding = await embeddings.EmbedAsync(fact),
        };
        await store.AddSemanticAsync(memory);
        return memory;
    }

    private static async Task<List<(string Legacy, JsonElement Input)>> PairRows(IShadowRecorder recorder)
    {
        var rows = await recorder.GetCapturesAsync(CognitiveCapture.PairSubject, 100);
        return rows
            .Select(r => (r.Legacy ?? "", r.Input is null
                ? default
                : JsonDocument.Parse(r.Input).RootElement))
            .ToList();
    }

    /// <summary>
    /// A change the user marks in their own words — the coffee case from the regression set. The
    /// pair row must carry the incumbent's verdict, both facts, the utterance the wording signal
    /// actually read, and enough provenance to adjudicate later.
    /// </summary>
    [Fact]
    public async Task AWordingSupersede_RecordsThePairWithProvenance()
    {
        await using var host = Host();
        using var scope = host.CreateScope();
        var old = await Seed(scope, "likes", "black coffee",
            "The user drinks their coffee black.", Now.AddDays(-412));

        var message = UserMsg("Actually I've gone off black coffee. I take oat milk lattes now.");
        var (pipeline, recorder) = Build(scope.ServiceProvider, host.Clock,
            Fact("likes", "oat milk lattes", "The user prefers oat milk lattes.", message,
                "Actually I've gone off black coffee. I take oat milk lattes now."));

        await pipeline.ProcessAsync(User, new[] { message });

        var (legacy, input) = Assert.Single(await PairRows(recorder));
        Assert.Equal("supersedes:wording", legacy);
        Assert.Equal("The user prefers oat milk lattes.", input.GetProperty("incoming").GetProperty("fact").GetString());
        Assert.Contains("gone off", input.GetProperty("incoming").GetProperty("utterance").GetString());
        Assert.Equal(old.Id, input.GetProperty("existing").GetProperty("id").GetGuid());
        Assert.Equal(412, input.GetProperty("existing").GetProperty("age_days").GetInt32());
        Assert.True(input.GetProperty("pair").GetProperty("same_slot").GetBoolean());
        Assert.False(input.GetProperty("pair").GetProperty("single_valued").GetBoolean());
    }

    /// <summary>
    /// The negative decision is a decision. A second dislike joins the first, and that pair —
    /// same slot, no replacement — is the COEXIST training row. A corpus holding only the pairs
    /// that superseded teaches a model that everything supersedes.
    /// </summary>
    [Fact]
    public async Task ACoexistDecision_IsCapturedToo()
    {
        await using var host = Host();
        using var scope = host.CreateScope();
        await Seed(scope, "dislikes", "coriander", "The user dislikes coriander.", Now.AddDays(-30));

        var message = UserMsg("I don't like olives either.");
        var (pipeline, recorder) = Build(scope.ServiceProvider, host.Clock,
            Fact("dislikes", "olives", "The user dislikes olives.", message, "I don't like olives either."));

        await pipeline.ProcessAsync(User, new[] { message });

        var (legacy, input) = Assert.Single(await PairRows(recorder));
        Assert.Equal("coexist", legacy);
        Assert.Equal("The user dislikes coriander.", input.GetProperty("existing").GetProperty("fact").GetString());
    }

    /// <summary>A single-valued displacement records its own outcome string.</summary>
    [Fact]
    public async Task ASingleValuedDisplacement_RecordsItsOutcome()
    {
        await using var host = Host();
        using var scope = host.CreateScope();
        await Seed(scope, "lives_in", "Norwich", "The user lives in Norwich.", Now.AddDays(-700));

        var message = UserMsg("I live in Cambridge now.");
        var (pipeline, recorder) = Build(scope.ServiceProvider, host.Clock,
            Fact("lives_in", "Cambridge", "The user lives in Cambridge.", message, "I live in Cambridge now."));

        await pipeline.ProcessAsync(User, new[] { message });

        var (legacy, input) = Assert.Single(await PairRows(recorder));
        Assert.Equal("supersedes:single_valued", legacy);
        Assert.True(input.GetProperty("pair").GetProperty("single_valued").GetBoolean());
    }

    /// <summary>With capture off, the pipeline writes no pair rows at all.</summary>
    [Fact]
    public async Task CaptureOff_NoPairRows()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        await Seed(scope, "likes", "black coffee", "The user drinks their coffee black.", Now.AddDays(-10));

        var message = UserMsg("Actually I've gone off black coffee. Oat milk lattes now.");
        var (pipeline, recorder) = Build(scope.ServiceProvider, host.Clock,
            Fact("likes", "oat milk lattes", "The user prefers oat milk lattes.", message,
                "Actually I've gone off black coffee. Oat milk lattes now."));

        await pipeline.ProcessAsync(User, new[] { message });

        Assert.Empty(await recorder.GetCapturesAsync(CognitiveCapture.PairSubject, 100));
    }

    /// <summary>
    /// A credential anywhere in the pair drops the text and keeps the verdict — the same promise
    /// sentence capture makes, because the rate survives the redaction and the secret must not.
    /// </summary>
    [Fact]
    public async Task ACredentialInThePair_DropsTheTextAndKeepsTheVerdict()
    {
        await using var host = Host();
        using var scope = host.CreateScope();
        var recorder = scope.ServiceProvider.GetRequiredService<IShadowRecorder>();
        var capture = new CognitiveCapture(recorder);

        await capture.CapturePairAsync(new SupersessionPairCapture(
            IncomingFact: "The user's API key is sk-abc123def456ghi789jkl012mno345.",
            IncomingValue: "sk-abc123def456ghi789jkl012mno345",
            Predicate: "other",
            Utterance: "my key is sk-abc123def456ghi789jkl012mno345",
            ExistingId: Guid.NewGuid(),
            ExistingFact: "The user has an API key.",
            ExistingValue: "an API key",
            ExistingPredicate: "other",
            ExistingAgeDays: 3,
            ExistingConfirmedDays: 3,
            SameSlot: true,
            SingleValued: false,
            Similarity: 0.9,
            IncumbentOutcome: "coexist"));

        var row = Assert.Single(await recorder.GetCapturesAsync(CognitiveCapture.PairSubject, 100));
        Assert.Equal("coexist", row.Legacy);
        Assert.Null(row.Input);
    }

    /// <summary>
    /// Forgetting the existing memory removes the pair rows that reference it. Sentence rows are
    /// found by the memory's evidence excerpts; pair rows carry the memory's id, and the id is the
    /// handle — the excerpts are the user's words for the INCOMING side, not the stored one.
    /// </summary>
    [Fact]
    public async Task ForgettingTheExistingMemory_RemovesItsPairRows()
    {
        await using var host = Host();
        using var scope = host.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMemoryStore>();
        var embeddings = scope.ServiceProvider.GetRequiredService<IEmbeddingModel>();
        var recorder = scope.ServiceProvider.GetRequiredService<IShadowRecorder>();
        var old = await Seed(scope, "likes", "black coffee", "The user drinks their coffee black.", Now.AddDays(-100));

        var message = UserMsg("Actually I've gone off black coffee. I take oat milk lattes now.");
        var (pipeline, _) = Build(scope.ServiceProvider, host.Clock,
            Fact("likes", "oat milk lattes", "The user prefers oat milk lattes.", message,
                "Actually I've gone off black coffee. I take oat milk lattes now."));
        await pipeline.ProcessAsync(User, new[] { message });
        Assert.Single(await recorder.GetCapturesAsync(CognitiveCapture.PairSubject, 100));

        var curator = new MemoryCurator(store, embeddings, host.Clock,
            NullLogger<MemoryCurator>.Instance, recorder);
        await curator.ForgetAsync(User, old.Id, "user asked to forget");

        Assert.Empty(await recorder.GetCapturesAsync(CognitiveCapture.PairSubject, 100));
    }
}
