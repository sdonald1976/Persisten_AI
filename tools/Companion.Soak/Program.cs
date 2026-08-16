using Companion.Soak;

// Drives real conversations against a running companion and reports what is wrong with the replies,
// so finding bugs stops depending on somebody thinking of the right thing to say.
//
//   dotnet run --project tools/Companion.Soak
//   dotnet run --project tools/Companion.Soak -- --only memory
//   dotnet run --project tools/Companion.Soak -- --only long --long-turns 30
//
// Exits non-zero when anything failed, so it can gate a change.

var url = Arg("--url") ?? "http://localhost:5266";
var only = Arg("--only");
var budget = int.TryParse(Arg("--budget"), out var b) ? b : 3072;
var longTurns = int.TryParse(Arg("--long-turns"), out var lt) ? lt : 24;
var timeout = TimeSpan.FromMinutes(int.TryParse(Arg("--timeout-minutes"), out var tm) ? tm : 15);

var api = new Api(url, timeout);

if (!await api.HealthyAsync())
{
    Console.Error.WriteLine($"No companion at {url}. Start it first (AvaWorld\\start-all.ps1).");
    return 2;
}

var chosen = only is null
    ? Scenarios.Names
    : Scenarios.Names.Where(n => n.Equals(only, StringComparison.OrdinalIgnoreCase)).ToList();

if (chosen.Count == 0)
{
    Console.Error.WriteLine($"Unknown scenario '{only}'. Known: {string.Join(", ", Scenarios.Names)}");
    return 2;
}

Console.WriteLine($"soak → {url}   scenarios: {string.Join(", ", chosen)}");
Console.WriteLine("A turn can take a couple of minutes when models are swapping. Nothing is stuck.");
Console.WriteLine();

var results = new List<Result>();
foreach (var name in chosen)
{
    Console.Write($"  {name,-9} ");
    try
    {
        var result = await Scenarios.RunAsync(name, api, budget, longTurns);
        results.Add(result);
        Console.WriteLine(result.Passed
            ? $"ok    ({result.Turns.Count} turns, {result.Took.TotalSeconds:N0}s)"
            : $"FAIL  ({result.Faults.Count} fault(s), {result.Turns.Count} turns, {result.Took.TotalSeconds:N0}s)");
    }
    catch (Exception ex)
    {
        results.Add(new Result(name, new List<Fault> { new("crashed", ex.Message, "") }, new List<Turn>(), new List<string>()));
        Console.WriteLine($"CRASH ({ex.Message})");
    }
}

Console.WriteLine();
foreach (var result in results)
{
    Console.WriteLine($"── {result.Scenario}");
    foreach (var note in result.Notes)
        Console.WriteLine($"     · {note}");
    foreach (var fault in result.Faults)
    {
        Console.WriteLine($"     ✗ {fault.Check}: {fault.Detail}");
        if (fault.Excerpt.Length > 0)
            Console.WriteLine($"       “{fault.Excerpt}”");
    }
    if (result.Passed && result.Notes.Count == 0)
        Console.WriteLine("     · nothing to report");
    Console.WriteLine();
}

var failed = results.Count(r => !r.Passed);
var slowest = results.SelectMany(r => r.Turns).OrderByDescending(t => t.Took).FirstOrDefault();
if (slowest is not null)
    Console.WriteLine($"slowest turn: {slowest.Took.TotalSeconds:N0}s");

Console.WriteLine(failed == 0
    ? $"All {results.Count} scenario(s) clean."
    : $"{failed} of {results.Count} scenario(s) had faults.");

return failed == 0 ? 0 : 1;

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
