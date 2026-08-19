using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Companion.Core.Abstractions;

namespace Companion.Eval;

public sealed record SyntheticNaturalizationRequest(
    int Seed,
    int MaxStructuredEvents = 300,
    int ParaphrasesPerEvent = 3,
    int MaxConcurrency = 4);

public sealed record SyntheticNaturalizationResult(
    IReadOnlyList<SyntheticCorpusRow> Accepted,
    IReadOnlyList<SyntheticCorpusRow> Quarantined,
    SyntheticDiversityReport Diagnostics,
    int StructuredEvents,
    int Attempted);

public sealed record SyntheticDiversitySlice(
    int Rows,
    int UniqueUtterances,
    int UniqueNormalizedUtterances,
    int ExactDuplicates,
    int NormalizedDuplicates,
    double ParaphrasesPerStructuredEvent);

public sealed record SyntheticDiversityReport(
    SyntheticDiversitySlice Overall,
    IReadOnlyDictionary<string, SyntheticDiversitySlice> ByLabel,
    IReadOnlyDictionary<string, SyntheticDiversitySlice> ByScenarioFamily,
    IReadOnlyDictionary<string, SyntheticDiversitySlice> ByVerbalizationGroup,
    IReadOnlyDictionary<string, int> BoundaryTargets,
    IReadOnlyDictionary<string, int> ValidationOutcomes,
    IReadOnlyDictionary<string, int> ValidationReasons);

public static partial class SyntheticNaturalizationPipeline
{
    public static IReadOnlyList<SyntheticCorpusRow> SelectStructuredEvents(
        IReadOnlyList<SyntheticCorpusRow> rows,
        SyntheticNaturalizationRequest request)
    {
        var rng = new Random(request.Seed);
        var ordered = rows.OrderBy(_ => rng.Next()).ToList();
        var selected = new List<SyntheticCorpusRow>();
        var duplicateCap = Math.Max(1, request.MaxStructuredEvents / 14);
        var weights = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["COEXIST"] = .34, ["SUPERSEDES"] = .25, ["REFINES"] = .13,
            ["UNCERTAIN"] = .13, ["CORRECTS"] = .09, ["CONTRADICTS"] = .04,
            ["DUPLICATE"] = .02,
        };
        foreach (var (label, weight) in weights.OrderByDescending(x => x.Value))
        {
            var cap = label == "DUPLICATE"
                ? duplicateCap
                : Math.Max(1, (int)Math.Round(request.MaxStructuredEvents * weight));
            selected.AddRange(ordered.Where(r => r.ExpectedLabel == label).Take(cap));
        }
        if (selected.Count < request.MaxStructuredEvents)
            selected.AddRange(ordered.Where(r => !selected.Contains(r))
                .Where(r => r.ExpectedLabel != "DUPLICATE" || selected.Count(x => x.ExpectedLabel == "DUPLICATE") < duplicateCap)
                .Take(request.MaxStructuredEvents - selected.Count));
        return selected.Take(request.MaxStructuredEvents).OrderByDescending(BoundaryPriority).ThenBy(_ => rng.Next()).ToList();
    }

    public static async Task<SyntheticNaturalizationResult> RunAsync(
        IReadOnlyList<SyntheticCorpusRow> rows,
        ISyntheticUtteranceVerbalizer verbalizer,
        SyntheticNaturalizationRequest request,
        CancellationToken ct = default)
    {
        var selected = SelectStructuredEvents(rows, request);
        var work = selected.SelectMany((row, eventIndex) =>
            Enumerable.Range(0, request.ParaphrasesPerEvent)
                .Select(attempt => (Row: row, Attempt: attempt, Seed: unchecked(request.Seed + eventIndex * 1009 + attempt * 9176))))
            .ToList();
        var results = new SyntheticCorpusRow[work.Count];
        using var gate = new SemaphoreSlim(Math.Max(1, request.MaxConcurrency));
        await Task.WhenAll(work.Select(async (item, index) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var source = item.Row with
                {
                    VerbalizationSeed = item.Seed,
                    VerbalizationGroupId = item.Row.ScenarioId,
                };
                try
                {
                    var verbalization = await verbalizer.VerbalizeAsync(source, ct);
                    results[index] = source.ApplyVerbalization(verbalization) with
                    {
                        ScenarioId = $"{source.ScenarioId}-v{item.Attempt + 1:00}",
                    };
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    results[index] = source with
                    {
                        ScenarioId = $"{source.ScenarioId}-v{item.Attempt + 1:00}",
                        Utterance = source.DeterministicUtterance ?? source.Utterance,
                        VerbalizedUtterance = null,
                        VerbalizerId = verbalizer.Id,
                        VerbalizationStatus = "quarantined",
                        VerbalizationReason = $"verbalizer error: {ex.GetType().Name}",
                        TrustedForTraining = false,
                    };
                }
            }
            finally { gate.Release(); }
        }));

        var accepted = results.Where(r => r.TrustedForTraining && r.VerbalizationStatus == "accepted").ToList();
        var quarantined = results.Where(r => !r.TrustedForTraining || r.VerbalizationStatus != "accepted").ToList();
        return new SyntheticNaturalizationResult(
            accepted, quarantined, Report(results), selected.Count, results.Length);
    }

    public static SyntheticDiversityReport Report(IReadOnlyList<SyntheticCorpusRow> rows)
        => new(
            Slice(rows),
            Slices(rows, r => r.ExpectedLabel),
            Slices(rows, r => r.Family),
            Slices(rows, r => r.VerbalizationGroupId ?? r.ScenarioId),
            rows.GroupBy(BoundaryTarget, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            rows.GroupBy(r => r.VerbalizationStatus ?? "unknown", StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            rows.Where(r => r.VerbalizationReason is not null)
                .GroupBy(r => r.VerbalizationReason!, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal));

    public static string BoundaryTarget(SyntheticCorpusRow row) => row.ExpectedLabel switch
    {
        "COEXIST" or "SUPERSEDES" => "COEXIST_vs_SUPERSEDES",
        "REFINES" or "DUPLICATE" => "REFINES_vs_DUPLICATE_CORRECTS",
        "CORRECTS" => "SUPERSEDES_vs_CORRECTS",
        "UNCERTAIN" => "UNCERTAIN_vs_ACTIONABLE",
        _ => "other",
    };

    private static int BoundaryPriority(SyntheticCorpusRow row) => row.ExpectedLabel switch
    {
        "COEXIST" => 50,
        "SUPERSEDES" => 45,
        "REFINES" => 40,
        "UNCERTAIN" => 35,
        "CORRECTS" => 30,
        "CONTRADICTS" => 15,
        _ => 5,
    };

    private static IReadOnlyDictionary<string, SyntheticDiversitySlice> Slices(
        IEnumerable<SyntheticCorpusRow> rows, Func<SyntheticCorpusRow, string> key)
        => rows.GroupBy(key, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => Slice(g.ToList()), StringComparer.Ordinal);

    private static SyntheticDiversitySlice Slice(IReadOnlyList<SyntheticCorpusRow> rows)
    {
        var exact = rows.Select(r => r.Utterance).Distinct(StringComparer.Ordinal).Count();
        var normalized = rows.Select(r => Normalize(r.Utterance)).Distinct(StringComparer.Ordinal).Count();
        var events = rows.Select(r => r.VerbalizationGroupId ?? r.ScenarioId).Distinct(StringComparer.Ordinal).Count();
        return new(rows.Count, exact, normalized, rows.Count - exact, rows.Count - normalized,
            events == 0 ? 0 : rows.Count / (double)events);
    }

    private static string Normalize(string value)
        => Whitespace().Replace(Punctuation().Replace(value.ToLowerInvariant(), " "), " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"[^\w\s]")]
    private static partial Regex Punctuation();
}

/// <summary>A minimal OpenAI-compatible transport used only by the offline corpus tool.</summary>
public sealed class SyntheticOpenAiChatModel : IChatModel, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _model;

    public SyntheticOpenAiChatModel(string baseUrl, string model, string? apiKey = null, TimeSpan? timeout = null)
    {
        _model = model;
        _http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = timeout ?? TimeSpan.FromSeconds(45) };
        if (!string.IsNullOrWhiteSpace(apiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public async Task<ChatCompletion> CompleteAsync(string systemPrompt, string userMessage,
        ResponseFormat? format = null, string? assistantPrefix = null, CancellationToken ct = default)
    {
        var payload = new
        {
            model = _model,
            temperature = 0.95,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage },
            },
        };
        using var response = await _http.PostAsJsonAsync("chat/completions", payload, ct);
        response.EnsureSuccessStatusCode();
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var text = body.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        var model = body.RootElement.TryGetProperty("model", out var actual) ? actual.GetString() : _model;
        return new ChatCompletion { Text = text, Model = model ?? _model };
    }

    public async Task<ChatCompletion> StreamAsync(string systemPrompt, string userMessage, IProgress<string> sink,
        string? assistantPrefix = null, CancellationToken ct = default)
    {
        var completion = await CompleteAsync(systemPrompt, userMessage, assistantPrefix: assistantPrefix, ct: ct);
        sink.Report(completion.Text);
        return completion;
    }

    public void Dispose() => _http.Dispose();
}
