using System.Diagnostics;

namespace Companion.Eval;

/// <summary>
/// Runs the pipeline truth table and reports where the code and the stated intent disagree.
/// </summary>
public static class Tier0Suite
{
    public static async Task<int> RunAsync(int seed, bool verbose)
    {
        var scenarios = PipelineScenarios.Build(seed);
        Console.WriteLine($"tier 0: {scenarios.Count} scenarios, pipeline only — no extractor, no embedder, no GPU");
        Console.WriteLine();

        await using var runner = await Tier0Runner.StartAsync();

        var mismatches = new List<Mismatch>();
        var watch = Stopwatch.StartNew();
        foreach (var scenario in scenarios)
            mismatches.AddRange((await runner.RunAsync(scenario)).Check());
        watch.Stop();

        var perScenario = watch.Elapsed.TotalMilliseconds / Math.Max(1, scenarios.Count);
        Console.WriteLine(
            $"── {scenarios.Count} scenarios in {watch.Elapsed.TotalSeconds:F1}s " +
            $"({perScenario:F1} ms each, ~{3600_000 / Math.Max(0.01, perScenario):N0}/hour)");
        Console.WriteLine();

        if (mismatches.Count == 0)
        {
            Console.WriteLine("every expectation held.");
            return 0;
        }

        foreach (var rule in mismatches.GroupBy(m => m.Expected.Why).OrderByDescending(g => g.Count()))
        {
            Console.WriteLine($"{rule.Key,-58} {rule.Count(),5}");
            if (!verbose)
                continue;
            foreach (var m in rule.Take(2))
            {
                Console.WriteLine($"     {m.Life}: {m.Expected.Predicate}/{m.Expected.Keyword} — {m.Problem}");
                foreach (var s in m.Slot.Take(3))
                    Console.WriteLine($"        {s}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"total mismatches: {mismatches.Count} of {scenarios.Sum(s => s.Expected.Count)} expectations");
        return 0;
    }
}
