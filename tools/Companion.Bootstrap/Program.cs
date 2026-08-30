using Companion.Core;
using Companion.Infrastructure.Models;
using Companion.Infrastructure.Models.Bootstrap;
using Microsoft.Extensions.Configuration;

// Model bootstrap: what does the effective configuration require, is it here, and if not can it
// be fetched? Exits non-zero when a REQUIRED dependency cannot be satisfied, so a startup script
// can simply refuse to launch rather than letting Ava come up quietly missing her mouth.
//
// It resolves configuration exactly as Companion.Api does — same files, same order, same
// environment-variable overrides — because a bootstrap that checks a different configuration
// than the app loads is worse than none.

var mode = BootstrapMode.Normal;
var allConfigured = false;
var inventoryOnly = false;
var force = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
string? contentRoot = null;

for (var i = 0; i < args.Length; i++)
{
    var a = args[i];
    switch (a.ToLowerInvariant())
    {
        case "--dry-run": mode = BootstrapMode.DryRun; break;
        case "--verify-only": mode = BootstrapMode.VerifyOnly; break;
        case "--all-configured": allConfigured = true; break;
        case "--inventory": inventoryOnly = true; break;
        case "--force":
            // -Force names WHAT to reacquire. A bare --force would mean "redownload everything",
            // which on this roster is tens of gigabytes of deliberate waste.
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                force.Add(args[++i]);
            else
            {
                Console.Error.WriteLine("--force requires a dependency id (see --inventory).");
                return 2;
            }
            break;
        case "--content-root":
            if (i + 1 < args.Length) contentRoot = args[++i];
            break;
        case "--help" or "-h":
            Console.WriteLine("""
                model-bootstrap [--dry-run|--verify-only] [--all-configured] [--inventory]
                                [--force <id>]... [--content-root <path>]
                """);
            return 0;
        default:
            Console.Error.WriteLine($"unknown argument '{a}'");
            return 2;
    }
}

// ---- the same effective configuration the application loads ---------------------------------

var repoRoot = FindRepositoryRoot();
contentRoot ??= Path.Combine(repoRoot, "src", "Companion.Api");
var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                  ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                  ?? "Production";

var configuration = new ConfigurationBuilder()
    .SetBasePath(contentRoot)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile($"appsettings.{environment}.json", optional: true)
    // Program.cs adds this in EVERY environment, then re-adds environment variables so they
    // still win. Mirrored exactly, including the order.
    .AddJsonFile("appsettings.local.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var models = configuration.GetSection(ModelOptions.SectionName).Get<ModelOptions>() ?? new ModelOptions();
var cognitive = configuration.GetSection(CognitiveModelOptions.Section).Get<CognitiveModelOptions>() ?? new CognitiveModelOptions();
var companion = configuration.GetSection(CompanionOptions.SectionName).Get<CompanionOptions>() ?? new CompanionOptions();
var safety = configuration.GetSection(SafetyOptions.Section).Get<SafetyOptions>() ?? new SafetyOptions();

// The app resolves the cognitive-model directory relative to the DATABASE, not the binaries,
// and the database path is itself relative to the content root when it is not rooted.
var dbPath = configuration["Database:Path"] ?? "companion.db";
if (!Path.IsPathRooted(dbPath))
    dbPath = Path.Combine(contentRoot, dbPath);
var cognitiveDirectory = Path.IsPathRooted(cognitive.Directory)
    ? cognitive.Directory
    : Path.Combine(Path.GetDirectoryName(dbPath) ?? contentRoot, cognitive.Directory);

var dependencies = ModelDependencies.Discover(
    models, cognitive, companion, safety, cognitiveDirectory, repoRoot);
var routing = ModelDependencies.DescribeRendererRouting(companion.RendererShadow);

// ---- inventory -------------------------------------------------------------------------------

Console.WriteLine();
Console.WriteLine($"Configuration  {contentRoot}  (environment: {environment})");
Console.WriteLine($"Renderer       {routing}");
Console.WriteLine();

if (inventoryOnly)
{
    WriteInventory(dependencies);
    return 0;
}

WriteInventory(dependencies);

// ---- run --------------------------------------------------------------------------------------

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
var bootstrap = new ModelBootstrap(
    new OllamaCliClient(http, models.Chat.BaseUrl),
    new HuggingFaceDownloader(),
    new GitLfsCliClient(repoRoot),
    new LocalFileSystem(),
    new HttpServedAdapterProbe(http));

var report = await bootstrap.RunAsync(
    dependencies, mode, routing, allConfigured, force);

Console.WriteLine(mode switch
{
    BootstrapMode.DryRun => "-- DRY RUN: nothing is downloaded or modified --",
    BootstrapMode.VerifyOnly => "-- VERIFY ONLY: nothing is downloaded --",
    _ => "-- checking, and acquiring what is missing --",
});
Console.WriteLine();

foreach (var problem in report.PrerequisiteProblems)
    Write(ConsoleColor.Yellow, $"  prerequisite   {problem}");
if (report.PrerequisiteProblems.Count > 0)
    Console.WriteLine();

foreach (var r in report.Results.OrderBy(r => r.Dependency.Active ? 0 : 1).ThenBy(r => r.Dependency.Id))
{
    var (colour, label) = r.State switch
    {
        ArtifactState.Verified => (ConsoleColor.Green, "verified"),
        ArtifactState.PresentUnpinned => (ConsoleColor.DarkGreen, "present"),
        ArtifactState.Missing => (ConsoleColor.Yellow, "missing"),
        ArtifactState.Invalid => (ConsoleColor.Red, "INVALID"),
        ArtifactState.Unacquirable => (ConsoleColor.Red, "unacquirable"),
        _ => (ConsoleColor.Yellow, "blocked"),
    };
    if (!r.Dependency.Active)
        colour = ConsoleColor.DarkGray;

    var scope = r.Dependency.Active ? "" : " [inactive]";
    Write(colour, $"  {label,-13}{r.Dependency.Id,-28}{r.Dependency.Identifier}{scope}");
    Write(ConsoleColor.DarkGray, $"                 {r.Detail}");
    if (r.Action is { } action && r.IsFailure)
        Write(ConsoleColor.Cyan, $"                 -> {action}");
}

Console.WriteLine();
var failures = report.Results.Where(r => r.Dependency.Active && r.IsFailure).ToList();
if (report.Ok)
{
    Write(ConsoleColor.Green,
        $"OK - {report.Results.Count(r => r.Dependency.Active)} active dependencies satisfied.");
    return 0;
}

Write(ConsoleColor.Red,
    $"FAILED - {failures.Count} required dependency(ies) unavailable: "
    + string.Join(", ", failures.Select(f => f.Dependency.Id)));
Write(ConsoleColor.Red,
    "Startup is refused rather than substituting a different model.");
return 1;

// ---- helpers -----------------------------------------------------------------------------------

void WriteInventory(IReadOnlyList<ModelDependency> deps)
{
    Console.WriteLine("Configured model inventory (derived from the effective configuration):");
    Console.WriteLine();
    Console.WriteLine($"  {"id",-28}{"provider",-18}{"identifier",-48}{"required",-10}location");
    foreach (var d in deps.OrderBy(d => d.Active ? 0 : 1).ThenBy(d => d.Id, StringComparer.Ordinal))
    {
        var location = d.ExpectedPath is { } p ? Rel(p) : d.BaseUrl ?? "-";
        var required = d.Active ? "yes" : "no";
        Write(d.Active ? ConsoleColor.Gray : ConsoleColor.DarkGray,
            $"  {d.Id,-28}{d.Provider,-18}{Truncate(d.Identifier, 46),-48}{required,-10}{location}");
        if (!d.Active && d.InactiveReason is { } why)
            Write(ConsoleColor.DarkGray, $"  {"",-28}{why}");
    }
    Console.WriteLine();
}

string Rel(string path)
{
    try { return Path.GetRelativePath(repoRoot, path).Replace('\\', '/'); }
    catch { return path; }
}

static string Truncate(string s, int max)
    => string.IsNullOrEmpty(s) ? "(none)" : s.Length <= max ? s : s[..(max - 1)] + "…";

static void Write(ConsoleColor colour, string line)
{
    var previous = Console.ForegroundColor;
    Console.ForegroundColor = colour;
    Console.WriteLine(line);
    Console.ForegroundColor = previous;
}

static string FindRepositoryRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
        dir = dir.Parent;
    return dir?.FullName ?? Directory.GetCurrentDirectory();
}
