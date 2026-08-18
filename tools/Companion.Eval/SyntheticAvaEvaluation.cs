using System.Net.Http.Json;
using System.Text.Json;
using Companion.Core.Abstractions;

namespace Companion.Eval;

public interface IAvaConversationClient : IAsyncDisposable
{
    string UserId { get; }
    Task<string> StartConversationAsync(string title, CancellationToken ct = default);
    Task<AvaTurnResult> SendAsync(string conversationId, string message, CancellationToken ct = default);
    Task<IReadOnlyList<AvaMemoryRecord>> MemoriesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<AvaTraceRecord>> RecentTracesAsync(CancellationToken ct = default);
    Task CleanupAsync(CancellationToken ct = default);
}

public static class SyntheticUserSafety
{
    public const string Prefix = "synthetic:";

    public static string UserIdFor(SyntheticScenario scenario)
        => $"{Prefix}{scenario.Provenance.Seed}:{scenario.Person.Id}";

    public static void EnsureSyntheticUser(string userId)
    {
        if (!userId.StartsWith(Prefix, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Refusing synthetic cleanup for non-synthetic user '{userId}'.");
    }
}

public sealed record AvaTurnResult(string Reply, string? TraceId, IReadOnlyList<AvaRetrievedMemory> Retrieved);

public sealed record AvaRetrievedMemory(string Content, double Score, string? Id = null);

public sealed record AvaTraceRecord(
    string? TraceId,
    string UserMessage,
    IReadOnlyList<AvaRetrievedMemory> Retrieved,
    IReadOnlyList<AvaMemoryDecisionRecord> Decisions);

public sealed record AvaMemoryDecisionRecord(
    string? Outcome,
    string? Reason,
    string? ResultingMemoryId,
    string? MatchedMemoryId);

public sealed record AvaMemoryRecord(
    string? Id,
    string Content,
    string Status,
    string? Validity = null,
    string? Subject = null,
    string? Predicate = null,
    string? Value = null);

/// <summary>HTTP client for the same REST path normal callers use: conversations, chat, memories, diagnostics.</summary>
public sealed class HttpAvaConversationClient : IAvaConversationClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public HttpAvaConversationClient(HttpClient http, string userId, bool ownsClient = false)
    {
        _http = http;
        UserId = userId;
        _ownsClient = ownsClient;
    }

    public string UserId { get; }

    public static HttpAvaConversationClient FromBaseUrl(string baseUrl, string userId, string? token, TimeSpan timeout)
    {
        var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = timeout };
        if (!string.IsNullOrWhiteSpace(token))
            http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return new HttpAvaConversationClient(http, userId, ownsClient: true);
    }

    public async Task<string> StartConversationAsync(string title, CancellationToken ct = default)
    {
        using var res = await _http.PostAsJsonAsync("/conversations", new { title, source = "synthetic-eval" }, Json, ct);
        res.EnsureSuccessStatusCode();
        var doc = await res.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
        return doc.GetProperty("conversationId").GetString()!;
    }

    public async Task<AvaTurnResult> SendAsync(string conversationId, string message, CancellationToken ct = default)
    {
        using var res = await _http.PostAsJsonAsync("/chat", new { conversationId, message }, Json, ct);
        res.EnsureSuccessStatusCode();
        var doc = await res.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
        var reply = doc.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
        var traces = await RecentTracesAsync(ct);
        var trace = traces.FirstOrDefault();
        return new AvaTurnResult(reply, trace?.TraceId, trace?.Retrieved ?? Array.Empty<AvaRetrievedMemory>());
    }

    public async Task<IReadOnlyList<AvaMemoryRecord>> MemoriesAsync(CancellationToken ct = default)
    {
        var mem = await _http.GetFromJsonAsync<JsonElement>("/memories", Json, ct);
        if (mem.ValueKind != JsonValueKind.Array)
            return Array.Empty<AvaMemoryRecord>();
        return mem.EnumerateArray().Select(m => new AvaMemoryRecord(
            Id: GetString(m, "id"),
            Content: GetString(m, "content") ?? "",
            Status: GetString(m, "status") ?? "",
            Validity: GetString(m, "validity"))).ToList();
    }

    public async Task<IReadOnlyList<AvaTraceRecord>> RecentTracesAsync(CancellationToken ct = default)
    {
        var turns = await _http.GetFromJsonAsync<JsonElement>("/diagnostics/turns", Json, ct);
        if (turns.ValueKind != JsonValueKind.Array)
            return Array.Empty<AvaTraceRecord>();
        return turns.EnumerateArray().Select(ParseTrace).ToList();
    }

    public Task CleanupAsync(CancellationToken ct = default) => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        if (_ownsClient)
            _http.Dispose();
        return ValueTask.CompletedTask;
    }

    private static AvaTraceRecord ParseTrace(JsonElement t)
    {
        var retrieved = t.TryGetProperty("retrieved", out var r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Select(x => new AvaRetrievedMemory(
                GetString(x, "content") ?? GetString(x, "memory") ?? "",
                x.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : 0)).ToList()
            : new List<AvaRetrievedMemory>();

        return new AvaTraceRecord(
            GetString(t, "traceId"),
            GetString(t, "userMessage") ?? "",
            retrieved,
            Array.Empty<AvaMemoryDecisionRecord>());
    }

    private static string? GetString(JsonElement e, string name)
        => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}

public sealed record SyntheticAvaEvaluationOptions(int ThroughTurn = int.MaxValue, bool StopOnFirstFailure = false);

public sealed class SyntheticAvaEvaluator
{
    private readonly Func<SyntheticScenario, IAvaConversationClient> _clientFactory;

    public SyntheticAvaEvaluator(Func<SyntheticScenario, IAvaConversationClient> clientFactory)
        => _clientFactory = clientFactory;

    public async Task<SyntheticLifeEvaluationResult> EvaluateAsync(
        SyntheticScenario scenario,
        SyntheticAvaEvaluationOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new SyntheticAvaEvaluationOptions();
        await using var client = _clientFactory(scenario);
        var conversationId = await client.StartConversationAsync($"synthetic {scenario.ScenarioId}", ct);
        var turnResults = new List<SyntheticAvaTurnResult>();

        foreach (var turn in scenario.Turns.Where(t => t.Turn <= options.ThroughTurn).OrderBy(t => t.Turn))
        {
            var sent = await client.SendAsync(conversationId, turn.Utterance, ct);
            var row = turn.ScenarioId is null ? null : scenario.Examples.FirstOrDefault(e => e.ScenarioId == turn.ScenarioId);
            var memories = await client.MemoriesAsync(ct);
            var comparison = row is null ? null : SyntheticCanonicalComparer.Compare(row, memories, sent.Retrieved);
            turnResults.Add(new SyntheticAvaTurnResult(turn.Turn, turn.Utterance, turn.ScenarioId, sent, comparison));
            if (comparison?.Failures.Count > 0 && options.StopOnFirstFailure)
                break;
        }

        var finalMemories = await client.MemoriesAsync(ct);
        var survival = SyntheticCanonicalComparer.Survival(scenario, finalMemories);
        var failures = turnResults
            .Where(t => t.Comparison is not null)
            .SelectMany(t => t.Comparison!.Failures.Select(f => SyntheticFailureArtifact.From(scenario, t, f, finalMemories)))
            .ToList();
        await client.CleanupAsync(ct);
        return new SyntheticLifeEvaluationResult(
            scenario.ScenarioId, scenario.Person.Id, scenario.Provenance.Seed,
            scenario.Turns.Count, scenario.Examples.Count, turnResults, finalMemories, survival, failures);
    }
}

public sealed record SyntheticAvaTurnResult(
    int Turn,
    string Utterance,
    string? ScenarioId,
    AvaTurnResult Ava,
    CanonicalComparisonResult? Comparison);

public sealed record SyntheticLifeEvaluationResult(
    string ScenarioId,
    string LifeId,
    int Seed,
    int TurnsEvaluated,
    int SemanticEventsEvaluated,
    IReadOnlyList<SyntheticAvaTurnResult> Turns,
    IReadOnlyList<AvaMemoryRecord> FinalMemories,
    SyntheticSurvivalReport Survival,
    IReadOnlyList<SyntheticFailureArtifact> Failures);

public static class SyntheticCanonicalComparer
{
    public static CanonicalComparisonResult Compare(
        SyntheticCorpusRow row,
        IReadOnlyList<AvaMemoryRecord> memories,
        IReadOnlyList<AvaRetrievedMemory> retrieved)
    {
        var failures = new List<CanonicalFailure>();
        var active = memories.Where(IsCurrent).ToList();
        var current = row.CanonicalStateAfter.Facts.Where(f => f.SubjectId == row.PersonId && f.Active && f.Permanent).ToList();

        foreach (var fact in current)
            if (!ContainsFact(active, fact))
                failures.Add(new CanonicalFailure("missing_current_fact", FailureStage.UnknownPipelineFailure, fact, null));

        foreach (var fact in row.AffectedFacts)
            if (fact.Active && ContainsFact(active, fact))
                failures.Add(new CanonicalFailure("stale_superseded_fact", FailureStage.MemoryOperationFailure, fact, null));

        if (!row.Permanent && row.ExpectedMemoryOperation == "DO_NOT_PROMOTE" && ContainsFact(active, row.CurrentFact))
            failures.Add(new CanonicalFailure("temporary_state_promoted", FailureStage.TemporalInterpretationFailure, row.CurrentFact, null));

        if (row.CurrentFact.SubjectId.StartsWith("other:", StringComparison.Ordinal) &&
            active.Any(m => Mentions(m, row.CurrentFact.Value)))
            failures.Add(new CanonicalFailure("foreign_person_contamination", FailureStage.SubjectResolutionFailure, row.CurrentFact, null));

        if (row.ExpectedMemoryOperation == "REPLACE" && row.PreviousFact is not null &&
            row.CandidateFacts.Count > 0 && !RetrievedAny(row.CandidateFacts, retrieved))
            failures.Add(new CanonicalFailure("previous_memory_not_retrieved", FailureStage.RetrievalFailure, row.PreviousFact, null));

        if (row.ExpectedLabel == "CORRECTS" && row.PreviousFact is not null && ContainsFact(active, row.PreviousFact))
            failures.Add(new CanonicalFailure("correction_not_applied", FailureStage.MemoryOperationFailure, row.PreviousFact, null));

        if (row.ExpectedLabel == "REFINES" && !ContainsFact(active, row.CurrentFact))
            failures.Add(new CanonicalFailure("refinement_not_merged", FailureStage.MemoryOperationFailure, row.CurrentFact, null));

        return new CanonicalComparisonResult(row.ScenarioId, row.Turn, failures);
    }

    public static SyntheticSurvivalReport Survival(SyntheticScenario scenario, IReadOnlyList<AvaMemoryRecord> memories)
    {
        var final = scenario.Examples.LastOrDefault()?.CanonicalStateAfter
            ?? new CanonicalStateSnapshot(Array.Empty<CanonicalFact>());
        var current = final.Facts.Where(f => f.SubjectId == scenario.Person.Id && f.Active && f.Permanent).ToList();
        var active = memories.Where(IsCurrent).ToList();
        var retained = current.Count(f => ContainsFact(active, f));
        var missing = current.Count - retained;
        var stale = final.Facts.Count(f => f.SubjectId == scenario.Person.Id && !f.Active && ContainsFact(active, f));
        var foreign = final.Facts.Count(f => f.SubjectId.StartsWith("other:", StringComparison.Ordinal) && ContainsFact(active, f));
        var temporary = final.Facts.Count(f => !f.Permanent && ContainsFact(active, f));
        return new SyntheticSurvivalReport(
            current.Count,
            retained,
            current.Count == 0 ? 1 : retained / (double)current.Count,
            missing,
            stale,
            foreign,
            temporary,
            CountFailures(scenario, memories, "CORRECTS"),
            CountFailures(scenario, memories, "REFINES"));
    }

    private static int CountFailures(SyntheticScenario scenario, IReadOnlyList<AvaMemoryRecord> memories, string label)
        => scenario.Examples
            .Where(r => r.ExpectedLabel == label)
            .Count(r => !ContainsFact(memories.Where(IsCurrent), r.CurrentFact));

    private static bool RetrievedAny(IEnumerable<CanonicalFact> facts, IEnumerable<AvaRetrievedMemory> retrieved)
        => facts.Any(f => retrieved.Any(r => r.Content.Contains(f.Value, StringComparison.OrdinalIgnoreCase)));

    private static bool ContainsFact(IEnumerable<AvaMemoryRecord> memories, CanonicalFact fact)
        => memories.Any(m => Mentions(m, fact.Value) || Mentions(m, fact.Label) && Mentions(m, fact.Value));

    private static bool Mentions(AvaMemoryRecord memory, string value)
        => (memory.Content?.Contains(value, StringComparison.OrdinalIgnoreCase) == true)
            || (memory.Value?.Contains(value, StringComparison.OrdinalIgnoreCase) == true);

    private static bool IsCurrent(AvaMemoryRecord memory)
        => memory.Status.Equals("Active", StringComparison.OrdinalIgnoreCase)
            || memory.Status.Equals("Current", StringComparison.OrdinalIgnoreCase);
}

public sealed record CanonicalComparisonResult(
    string ScenarioId,
    int Turn,
    IReadOnlyList<CanonicalFailure> Failures);

public sealed record CanonicalFailure(
    string Kind,
    FailureStage Stage,
    CanonicalFact ExpectedFact,
    string? TraceId);

public enum FailureStage
{
    RetrievalFailure,
    SemanticClassificationFailure,
    MemoryOperationFailure,
    PersistenceFailure,
    SubjectResolutionFailure,
    TemporalInterpretationFailure,
    UnknownPipelineFailure,
}

public sealed record SyntheticSurvivalReport(
    int CurrentCanonicalFacts,
    int CurrentFactsRetained,
    double CurrentFactRecall,
    int MissingCurrentFacts,
    int StaleSupersededFacts,
    int ForeignPersonContamination,
    int TemporaryStatePromotions,
    int CorrectionFailures,
    int RefinementFailures);

public sealed record SyntheticFailureArtifact(
    string LifeId,
    int Seed,
    int Turn,
    string ScenarioId,
    string Family,
    IReadOnlyList<string> Difficulty,
    CanonicalStateSnapshot CanonicalStateBefore,
    CanonicalStateSnapshot CanonicalStateAfter,
    IReadOnlyList<CanonicalFact> CandidateFacts,
    IReadOnlyList<CanonicalFact> AffectedFacts,
    string Utterance,
    string ExpectedLabel,
    string ExpectedMemoryOperation,
    string FailureKind,
    FailureStage FailureStage,
    IReadOnlyList<AvaMemoryRecord> ResultingMemoryState,
    string? TraceId,
    string Generator)
{
    public static SyntheticFailureArtifact From(
        SyntheticScenario scenario,
        SyntheticAvaTurnResult turn,
        CanonicalFailure failure,
        IReadOnlyList<AvaMemoryRecord> memories)
    {
        var row = scenario.Examples.Single(e => e.ScenarioId == turn.ScenarioId);
        return new SyntheticFailureArtifact(
            scenario.Person.Id,
            scenario.Provenance.Seed,
            row.Turn,
            row.ScenarioId,
            row.Family,
            row.Difficulty,
            row.CanonicalStateBefore,
            row.CanonicalStateAfter,
            row.CandidateFacts,
            row.AffectedFacts,
            row.Utterance,
            row.ExpectedLabel,
            row.ExpectedMemoryOperation,
            failure.Kind,
            failure.Stage,
            memories,
            turn.Ava.TraceId,
            row.Generator);
    }
}

public static class SyntheticFailureArtifacts
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static void WriteJsonl(string path, IEnumerable<SyntheticFailureArtifact> failures)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var writer = new StreamWriter(path, append: false);
        foreach (var failure in failures)
            writer.WriteLine(JsonSerializer.Serialize(failure, Json));
    }

    public static IReadOnlyList<SyntheticFailureArtifact> ReadJsonl(string path)
        => File.ReadLines(path)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonSerializer.Deserialize<SyntheticFailureArtifact>(l, Json)!)
            .Where(f => f is not null)
            .ToList();
}

public interface ISyntheticUtteranceVerbalizer
{
    string Id { get; }
    Task<SyntheticVerbalization> VerbalizeAsync(SyntheticCorpusRow row, CancellationToken ct = default);
}

public sealed record SyntheticVerbalization(
    string OriginalUtterance,
    string CandidateUtterance,
    string VerbalizerId,
    string ValidationStatus,
    bool TrustedForTraining,
    string? Reason = null);

public sealed class DeterministicTemplateVerbalizer : ISyntheticUtteranceVerbalizer
{
    public string Id => "deterministic-template";

    public Task<SyntheticVerbalization> VerbalizeAsync(SyntheticCorpusRow row, CancellationToken ct = default)
        => Task.FromResult(new SyntheticVerbalization(
            row.DeterministicUtterance ?? row.Utterance,
            row.DeterministicUtterance ?? row.Utterance,
            Id,
            "accepted",
            true));
}

public sealed class LlmSyntheticVerbalizer : ISyntheticUtteranceVerbalizer
{
    private readonly IChatModel _model;
    private readonly ISyntheticVerbalizationValidator _validator;

    public LlmSyntheticVerbalizer(IChatModel model, ISyntheticVerbalizationValidator validator, string id = "llm-verbalizer")
    {
        _model = model;
        _validator = validator;
        Id = id;
    }

    public string Id { get; }

    public async Task<SyntheticVerbalization> VerbalizeAsync(SyntheticCorpusRow row, CancellationToken ct = default)
    {
        var prompt =
            "Paraphrase the user's utterance naturally. Preserve subject, value, temporality, and meaning. " +
            "Do not add facts. Return only the paraphrase.";
        var user =
            $"Utterance: {row.Utterance}\nSubject: {row.CurrentFact.SubjectId}\n" +
            $"Value: {row.CurrentFact.Value}\nExpected label: {row.ExpectedLabel}\n" +
            $"Operation: {row.ExpectedMemoryOperation}\nTemporal: {row.TemporalScope}";
        var completion = await _model.CompleteAsync(prompt, user, ct: ct);
        var candidate = completion.Text.Trim();
        var verdict = _validator.Validate(row, candidate);
        return new SyntheticVerbalization(row.Utterance, candidate, Id, verdict.Status, verdict.TrustedForTraining, verdict.Reason);
    }
}

public interface ISyntheticVerbalizationValidator
{
    SyntheticVerbalizationVerdict Validate(SyntheticCorpusRow source, string candidate);
}

public sealed record SyntheticVerbalizationVerdict(string Status, bool TrustedForTraining, string? Reason = null);

public sealed class ConservativeSyntheticVerbalizationValidator : ISyntheticVerbalizationValidator
{
    public SyntheticVerbalizationVerdict Validate(SyntheticCorpusRow source, string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return new("rejected", false, "empty paraphrase");
        if (!ContainsImportantValue(source.CurrentFact.Value, candidate))
            return new("quarantined", false, "current value not preserved");
        if (source.CurrentFact.SubjectId.StartsWith("other:", StringComparison.Ordinal) &&
            candidate.Contains("I ", StringComparison.OrdinalIgnoreCase))
            return new("quarantined", false, "possible subject shift");
        return new("accepted", true);
    }

    private static bool ContainsImportantValue(string value, string candidate)
        => value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2)
            .Any(w => candidate.Contains(w, StringComparison.OrdinalIgnoreCase));
}

public static class SyntheticVerbalizationExtensions
{
    public static SyntheticCorpusRow ApplyVerbalization(this SyntheticCorpusRow row, SyntheticVerbalization verbalization)
        => row with
        {
            Utterance = verbalization.CandidateUtterance,
            DeterministicUtterance = verbalization.OriginalUtterance,
            VerbalizedUtterance = verbalization.CandidateUtterance,
            VerbalizerId = verbalization.VerbalizerId,
            VerbalizationStatus = verbalization.ValidationStatus,
            TrustedForTraining = verbalization.TrustedForTraining,
            VerbalizationGroupId = row.VerbalizationGroupId ?? row.ScenarioId,
        };

    public static IReadOnlyList<SyntheticCorpusRow> TrustedTrainingRows(this IEnumerable<SyntheticCorpusRow> rows)
        => rows.Where(r => r.TrustedForTraining && r.VerbalizationStatus == "accepted").ToList();
}
