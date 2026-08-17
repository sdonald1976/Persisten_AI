using System.Diagnostics;

namespace Companion.Eval;

/// <summary>
/// Runs a batch of generated lives and reports what the store got wrong, grouped by the rule that
/// was broken rather than by which life broke it — one bug affecting two hundred lives is one bug,
/// and a list of two hundred mismatches hides that.
/// </summary>
public static class LifeSuite
{
    public static async Task<int> RunAsync(int count, int seed, string model, string url, bool verbose)
    {
        var lives = LifeGenerator.Build(count, seed);
        var messages = lives.Sum(l => l.Messages.Count);

        Console.WriteLine($"lives: {count} (seed {seed}), {messages} messages, extraction on {model}");
        Console.WriteLine("no chat model is used — extraction cites only the user's own words, and generation");
        Console.WriteLine("is ~77 of the ~80 seconds a real turn costs.");
        Console.WriteLine();

        await using var runner = await LifeRunner.StartAsync(model, url);

        var mismatches = new List<Mismatch>();
        var failures = new List<string>();
        var watch = Stopwatch.StartNew();
        var done = 0;

        foreach (var life in lives)
        {
            var result = await runner.RunAsync(life);
            mismatches.AddRange(result.Check());
            failures.AddRange(result.Failures);
            done++;

            if (done % 5 == 0 || done == lives.Count)
            {
                var perLife = watch.Elapsed.TotalSeconds / done;
                var left = TimeSpan.FromSeconds(perLife * (lives.Count - done));
                Console.WriteLine(
                    $"  {done}/{lives.Count} lives  {perLife:F0}s each  {mismatches.Count} mismatches" +
                    (done == lives.Count ? "" : $"  ~{left.TotalMinutes:F0} min left"));
            }
        }

        watch.Stop();
        Report(lives.Count, messages, mismatches, failures, watch.Elapsed, verbose);

        // Never non-zero on findings: this harness exists to measure a system known to be imperfect,
        // and a red build on every run is a red build nobody reads. The regression floors in
        // Baselines are the thing that gates a change.
        return 0;
    }

    /// <summary>Every rule the generator asserts, so the report can name the ones that held.</summary>
    private static readonly IReadOnlyList<string> AllRules =
        LifeGenerator.Build(1, seed: 0)[0].Expected.Select(e => e.Why).Distinct().ToList();

    private static string Clip(string text, int max)
        => text.Length <= max ? text : text[..(max - 1)] + "…";

    private static void Report(
        int lives, int messages, List<Mismatch> mismatches, List<string> failures,
        TimeSpan elapsed, bool verbose)
    {
        Console.WriteLine();
        Console.WriteLine($"── {lives} lives, {messages} messages, {elapsed.TotalMinutes:F1} min");
        Console.WriteLine();

        if (failures.Count > 0)
        {
            Console.WriteLine($"crashes: {failures.Count}");
            foreach (var f in failures.Take(3))
                Console.WriteLine($"     {f}");
            Console.WriteLine();
        }

        var byRule = mismatches
            .GroupBy(m => m.Expected.Why)
            .OrderByDescending(g => g.Count())
            .ToList();

        if (byRule.Count == 0)
        {
            Console.WriteLine("every expectation held.");
            return;
        }

        Console.WriteLine($"{"rule",-56} failed   of    rate");
        foreach (var rule in byRule)
        {
            // Every life asserts every rule exactly once, so the denominator is the batch size.
            // Stated rather than derived from the mismatches, because a rule that failed in all of
            // them and a rule that was only checked once must not read the same.
            Console.WriteLine(
                $"{Clip(rule.Key, 56),-56} {rule.Count(),6}  {lives,4}   {100.0 * rule.Count() / lives,4:F0}%");

            if (!verbose)
                continue;
            foreach (var m in rule.Take(3))
                Console.WriteLine($"     {m.Life}: {m.Expected.Predicate}/{m.Expected.Keyword} — {m.Problem}");
        }

        // The rules nobody broke are the more useful half of the report: they are the regressions
        // that would otherwise be noticed months later, in a conversation, by their absence.
        var broken = byRule.Select(g => g.Key).ToHashSet();
        var held = AllRules.Where(r => !broken.Contains(r)).ToList();
        if (held.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("held in every life:");
            foreach (var rule in held)
                Console.WriteLine($"     {rule}");
        }

        Console.WriteLine();
        Console.WriteLine($"total mismatches: {mismatches.Count} across {mismatches.Select(m => m.Life).Distinct().Count()} lives");
    }
}
