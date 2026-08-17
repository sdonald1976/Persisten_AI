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

if (chosen.Length == 0)
{
    Console.Error.WriteLine($"Unknown suite '{only}'. Known: {string.Join(", ", suites.Select(s => s.Name))}");
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

Console.WriteLine();
Console.WriteLine(regressions == 0 ? "no regressions" : $"{regressions} suite(s) below baseline");
return regressions == 0 ? 0 : 1;

string? ArgValue(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
