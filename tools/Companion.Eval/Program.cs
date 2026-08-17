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

Console.WriteLine("evaluating the current heuristics — this is the baseline a model has to beat");
Console.WriteLine();

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

var rankingOnly = only is not null && only.Equals("resolution", StringComparison.OrdinalIgnoreCase);
if (chosen.Length == 0 && !rankingOnly)
{
    Console.Error.WriteLine(
        $"Unknown suite '{only}'. Known: {string.Join(", ", suites.Select(s => s.Name))}, resolution, lives, tier0, text, corpus");
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
