using Companion.Eval;

// Scores the deterministic heuristics against labelled data, so that when a specialist model is
// added there is a number to beat rather than an impression to argue with.
//
//   dotnet run --project tools/Companion.Eval
//   dotnet run --project tools/Companion.Eval -- --only assertion
//   dotnet run --project tools/Companion.Eval -- --verbose
//
// Exits non-zero when a suite falls below its recorded baseline, so it can gate a change.

var only = ArgValue("--only");
var verbose = args.Contains("--verbose");
var datasets = Path.Combine(AppContext.BaseDirectory, "datasets");

if (!Directory.Exists(datasets))
{
    Console.Error.WriteLine($"No datasets at {datasets}.");
    return 2;
}

var suites = new (string Name, Func<bool, Metrics> Run)[]
{
    ("decision", v => Evaluate.Decision(Path.Combine(datasets, "decision.jsonl"), v)),
    ("assertion", v => Evaluate.Assertion(Path.Combine(datasets, "assertion.jsonl"), v)),
    ("supersession", v => Evaluate.Supersession(Path.Combine(datasets, "supersession.jsonl"), v)),
};

var chosen = only is null
    ? suites
    : suites.Where(s => s.Name.Equals(only, StringComparison.OrdinalIgnoreCase)).ToArray();

// Build the training corpus and score the incumbent heuristics against it.
if (only is not null && only.Equals("corpus", StringComparison.OrdinalIgnoreCase))
{
    var outDir = ArgValue("--out") ?? Path.Combine(AppContext.BaseDirectory, "corpus");
    return CorpusSuite.Run(outDir, verbose);
}

// Truth tables for the single-message decisions: pure functions, no store, no models.
if (only is not null && only.Equals("text", StringComparison.OrdinalIgnoreCase))
    return TextTruthTables.Run(verbose);

// Tier 0: the pipeline with no model in the loop. Milliseconds per scenario, so the decision
// space can be covered combinatorially rather than sampled.
if (only is not null && only.Equals("tier0", StringComparison.OrdinalIgnoreCase))
{
    var seed0 = int.TryParse(ArgValue("--seed"), out var s0) ? s0 : 1;
    return await Tier0Suite.RunAsync(seed0, verbose);
}

// The synthetic-lives harness: generate a life whose correct final store is known, run it through
// the real extractor and pipeline, and diff. Opt-in because it costs real inference time.
if (only is not null && only.Equals("lives", StringComparison.OrdinalIgnoreCase))
{
    var count = int.TryParse(ArgValue("--lives"), out var n) ? n : 10;
    var seed = int.TryParse(ArgValue("--seed"), out var sd) ? sd : 1;
    var model = ArgValue("--extraction") ?? "qwen2.5:7b-instruct";
    var url = ArgValue("--ollama") ?? "http://localhost:11434/v1";
    return await LifeSuite.RunAsync(count, seed, model, url, verbose);
}

// Structured synthetic-life corpus: hidden canonical state is generated and evolved before any
// dialogue is rendered. This mode is deterministic, provider-independent, and requires no LLM.
if (only is not null && only.Equals("synthetic", StringComparison.OrdinalIgnoreCase))
{
    var seed = int.TryParse(ArgValue("--seed"), out var sd) ? sd : 1;
    var people = int.TryParse(ArgValue("--people"), out var p) ? p : 1;
    var turns = int.TryParse(ArgValue("--turns"), out var t) ? t : 160;
    var events = int.TryParse(ArgValue("--events"), out var e) ? e : 0;
    var outPath = ArgValue("--out") ?? Path.Combine(AppContext.BaseDirectory, "synthetic-life.jsonl");
    var failuresPath = ArgValue("--failures");
    var replayPerson = ArgValue("--replay");
    var splitDir = ArgValue("--split-out");
    var splitGroup = ArgValue("--split-group") ?? "life";
    var avaUrl = ArgValue("--ava-url");
    var avaToken = ArgValue("--ava-token");
    var e2eFailuresPath = ArgValue("--e2e-failures");
    var minFamilies = ParseMinimums("--min-family");
    var minDifficulty = ParseMinimums("--min-difficulty");
    var naturalize = args.Contains("--naturalize", StringComparer.OrdinalIgnoreCase);
    var request = new SyntheticRunRequest(
        seed,
        people,
        turns,
        EventsPerPerson: events,
        MinSemanticFamilies: minFamilies,
        MinDifficulty: minDifficulty);

    if (replayPerson is not null)
    {
        var replayed = SyntheticLife.Replay(seed, replayPerson, request);
        Console.WriteLine($"{replayed.ScenarioId}: {replayed.Turns.Count} turns, {replayed.Examples.Count} labelled events");
        foreach (var turn in replayed.Turns.Where(tn => tn.Relevant).Take(8))
            Console.WriteLine($"{turn.Turn,4}: {turn.Utterance}");
        return 0;
    }

    var watch = System.Diagnostics.Stopwatch.StartNew();
    var scenarios = SyntheticLife.Generate(request);
    var rows = scenarios.SelectMany(s => s.Examples).ToList();

    if (naturalize)
    {
        var verbalizerUrl = ArgValue("--verbalizer-url") ?? Environment.GetEnvironmentVariable("OPENAI_API_BASE") ?? "http://localhost:11434/v1";
        var verbalizerModel = ArgValue("--verbalizer-model") ?? Environment.GetEnvironmentVariable("MODEL_NAME") ?? "gpt-4.1-mini";
        var apiKey = Environment.GetEnvironmentVariable(ArgValue("--verbalizer-key-env") ?? "OPENAI_API_KEY");
        var maxStructured = int.TryParse(ArgValue("--naturalize-events"), out var ne) ? ne : 300;
        var paraphrases = int.TryParse(ArgValue("--paraphrases"), out var np) ? np : 3;
        var concurrency = int.TryParse(ArgValue("--naturalize-concurrency"), out var nc) ? nc : 4;
        var quarantinePath = ArgValue("--quarantine") ?? Path.ChangeExtension(outPath, ".quarantine.jsonl");
        var structuredPath = ArgValue("--structured-out");
        if (structuredPath is not null)
            SyntheticLife.WriteJsonl(structuredPath, rows);

        using var model = new SyntheticOpenAiChatModel(verbalizerUrl, verbalizerModel, apiKey);
        var verbalizer = new LlmSyntheticVerbalizer(model, new ConservativeSyntheticVerbalizationValidator(), $"llm:{verbalizerModel}");
        var naturalized = await SyntheticNaturalizationPipeline.RunAsync(rows, verbalizer,
            new SyntheticNaturalizationRequest(seed, maxStructured, paraphrases, concurrency));
        SyntheticLife.WriteJsonl(outPath, naturalized.Accepted);
        SyntheticLife.WriteJsonl(quarantinePath, naturalized.Quarantined);
        Console.WriteLine($"naturalized structured events: {naturalized.StructuredEvents}");
        Console.WriteLine($"verbalization attempts: {naturalized.Attempted}, accepted {naturalized.Accepted.Count}, quarantined {naturalized.Quarantined.Count}");
        PrintDiversity(naturalized.Diagnostics);

        if (splitDir is not null)
        {
            Directory.CreateDirectory(splitDir);
            var split = SyntheticLife.Split(naturalized.Accepted, splitGroup, seed);
            SyntheticLife.WriteJsonl(Path.Combine(splitDir, "synthetic.train.jsonl"), split.Train);
            SyntheticLife.WriteJsonl(Path.Combine(splitDir, "synthetic.validation.jsonl"), split.Validation);
            SyntheticLife.WriteJsonl(Path.Combine(splitDir, "synthetic.test.jsonl"), split.Test);
            Console.WriteLine($"split ({splitGroup} groups): train {split.Train.Count}, validation {split.Validation.Count}, test {split.Test.Count}");
        }
        return 0;
    }

    if (avaUrl is not null)
    {
        var e2eLives = int.TryParse(ArgValue("--e2e-lives"), out var el) ? el : 1;
        if (e2eLives != 1)
        {
            Console.Error.WriteLine("HTTP end-to-end mode currently supports one life per Ava process because User:Id is server-side.");
            Console.Error.WriteLine("Launch a separate isolated Ava instance per synthetic user for multi-life HTTP runs.");
            return 2;
        }

        var scenario = scenarios[0];
        var syntheticUser = SyntheticUserSafety.UserIdFor(scenario);
        Console.WriteLine($"end-to-end HTTP synthetic eval → {avaUrl}");
        Console.WriteLine($"expected Ava User:Id: {syntheticUser}");
        await using var client = HttpAvaConversationClient.FromBaseUrl(
            avaUrl, syntheticUser, avaToken, TimeSpan.FromMinutes(15));
        var evaluator = new SyntheticAvaEvaluator(_ => client);
        var e2e = await evaluator.EvaluateAsync(scenario);
        Console.WriteLine($"life {e2e.LifeId}, turns {e2e.TurnsEvaluated}, semantic events {e2e.SemanticEventsEvaluated}");
        Console.WriteLine($"current canonical recall: {e2e.Survival.CurrentFactRecall:P1}");
        Console.WriteLine($"missing current facts: {e2e.Survival.MissingCurrentFacts}");
        Console.WriteLine($"stale superseded facts: {e2e.Survival.StaleSupersededFacts}");
        Console.WriteLine($"foreign-person contamination: {e2e.Survival.ForeignPersonContamination}");
        Console.WriteLine($"temporary-state promotions: {e2e.Survival.TemporaryStatePromotions}");
        var byStage = e2e.Failures.GroupBy(f => f.FailureStage).OrderBy(g => g.Key);
        foreach (var stage in byStage)
            Console.WriteLine($"     {stage.Key,-36} {stage.Count(),5}");
        if (e2eFailuresPath is not null)
        {
            SyntheticFailureArtifacts.WriteJsonl(e2eFailuresPath, e2e.Failures);
            Console.WriteLine($"e2e failure export: {e2eFailuresPath}");
        }
        return 0;
    }

    var evaluated = SyntheticEvaluation.Evaluate(rows, new KeywordProbe());
    var written = SyntheticLife.WriteJsonl(outPath, evaluated);
    watch.Stop();

    Console.WriteLine($"synthetic-life wrote {written} rows to {outPath}");
    Console.WriteLine($"seed {seed}, people {people}, turns/person {turns}, events/person {(events == 0 ? "seeded" : events)}, {watch.Elapsed.TotalSeconds:F2}s");

    var report = SyntheticLife.Report(scenarios, request);
    PrintCounts("semantic", report.BySemanticFamily);
    PrintCounts("operation", report.ByMemoryOperation);
    PrintCounts("difficulty", report.ByDifficulty);
    PrintCounts("distance", report.ByEventDistance);
    Console.WriteLine($"scenario families: {report.UniqueScenarioFamilies}");
    Console.WriteLine($"duplicates: exact {report.ExactDuplicateUtterances} ({report.ExactDuplicateRate:P1}), normalized {report.NormalizedDuplicateUtterances} ({report.NormalizedDuplicateRate:P1}), structures {report.DuplicateStructures}");
    Console.WriteLine($"lexical diversity/row: {report.LexicalDiversityPerRow:F2}");
    foreach (var warning in report.Warnings)
        Console.WriteLine($"warning: {warning}");

    var failures = SyntheticEvaluation.FailuresAndDisagreements(evaluated);
    Console.WriteLine($"failures/disagreements: {failures.Count}");
    if (failuresPath is not null)
    {
        SyntheticLife.WriteJsonl(failuresPath, failures);
        Console.WriteLine($"failure export: {failuresPath}");
    }

    if (splitDir is not null)
    {
        Directory.CreateDirectory(splitDir);
        var split = SyntheticLife.Split(evaluated, splitGroup, seed);
        SyntheticLife.WriteJsonl(Path.Combine(splitDir, "synthetic.train.jsonl"), split.Train);
        SyntheticLife.WriteJsonl(Path.Combine(splitDir, "synthetic.validation.jsonl"), split.Validation);
        SyntheticLife.WriteJsonl(Path.Combine(splitDir, "synthetic.test.jsonl"), split.Test);
        Console.WriteLine(
            $"split ({splitGroup} groups): train {split.Train.Count}, validation {split.Validation.Count}, test {split.Test.Count}");
    }

    return 0;
}

Console.WriteLine("evaluating the current heuristics — this is the baseline a model has to beat");
Console.WriteLine();

var rankingOnly = only is not null && only.Equals("resolution", StringComparison.OrdinalIgnoreCase);
if (chosen.Length == 0 && !rankingOnly)
{
    Console.Error.WriteLine(
        $"Unknown suite '{only}'. Known: {string.Join(", ", suites.Select(s => s.Name))}, resolution, lives, synthetic, tier0, text, corpus");
    return 2;
}

var regressions = 0;
foreach (var (name, run) in chosen)
{
    var metrics = run(verbose);
    Console.WriteLine(metrics.ToLine());

    if (Baselines.Floors.TryGetValue(name, out var floor) && metrics.F1 + 1e-6 < floor)
    {
        Console.WriteLine($"     ✗ F1 {metrics.F1:F3} is below the recorded baseline {floor:F3}");
        regressions++;
    }
}

// NLI head-to-head on supersession: the judgment the wording signal only half makes.
{
    var models = ArgValue("--models") ?? DefaultModelDirectory();
    var nli = Evaluate.SupersessionNli(
        Path.Combine(datasets, "supersession.jsonl"),
        Directory.Exists(models) ? models : null, verbose, out var nliMs);
    if (nli is not null)
        Console.WriteLine($"{nli.ToLine()}   {nliMs:F0} ms/call");
}

// The head-to-head. Runs only when a model directory is supplied, so the harness is still useful
// with no weights anywhere — the heuristic baselines above are the point of it either way.
if (only is null || only.Equals("resolution", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine();
    var models = ArgValue("--models") ?? DefaultModelDirectory();
    var (keyword, model, hybrid, msPerQuery) = Ranking.Run(
        Path.Combine(datasets, "resolution.jsonl"), Directory.Exists(models) ? models : null, verbose);

    Console.WriteLine(keyword.ToLine());
    if (model is null)
    {
        Console.WriteLine($"{"cross-encoder",-22} not run (no model at {models})");
    }
    else
    {
        Console.WriteLine(model.ToLine());
        if (hybrid is not null)
            Console.WriteLine(hybrid.ToLine());
        Console.WriteLine(
            $"{"",-22} {msPerQuery:F0} ms per query; " +
            $"model {model.Precision1 - keyword.Precision1:+0.000;-0.000;0.000} P@1 vs keyword, " +
            $"hybrid {(hybrid?.Precision1 ?? 0) - keyword.Precision1:+0.000;-0.000;0.000}");
    }
}

Console.WriteLine();
Console.WriteLine(regressions == 0 ? "no regressions" : $"{regressions} suite(s) below baseline");
return regressions == 0 ? 0 : 1;

static string DefaultModelDirectory()
    => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Companion.Api", "models"));

string? ArgValue(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

Dictionary<string, int> ParseMinimums(string name)
{
    var result = new Dictionary<string, int>(StringComparer.Ordinal);
    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].Equals(name, StringComparison.OrdinalIgnoreCase) || i + 1 >= args.Length)
            continue;
        var parts = args[i + 1].Split('=', 2);
        if (parts.Length == 2 && int.TryParse(parts[1], out var count))
            result[parts[0]] = count;
    }
    return result;
}

static void PrintCounts(string title, IReadOnlyDictionary<string, int> counts)
{
    Console.WriteLine(title + ":");
    foreach (var (key, count) in counts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        Console.WriteLine($"     {key,-34} {count,6}");
}

static void PrintDiversity(SyntheticDiversityReport report)
{
    var o = report.Overall;
    Console.WriteLine($"diversity: rows {o.Rows}, unique {o.UniqueUtterances}, unique normalized {o.UniqueNormalizedUtterances}, exact duplicates {o.ExactDuplicates}, normalized duplicates {o.NormalizedDuplicates}, paraphrases/event {o.ParaphrasesPerStructuredEvent:F2}");
    PrintCounts("label", report.ByLabel.ToDictionary(kv => kv.Key, kv => kv.Value.Rows, StringComparer.Ordinal));
    PrintCounts("boundary", report.BoundaryTargets);
    PrintCounts("validation", report.ValidationOutcomes);
    if (report.ValidationReasons.Count > 0)
        PrintCounts("quarantine reason", report.ValidationReasons);
}
