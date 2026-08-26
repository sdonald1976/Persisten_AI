using System.Text.Json;
using Companion.MouthFactory.Export;
using Companion.MouthFactory.Generation;
using Companion.MouthFactory.Schema;
using Companion.MouthFactory.Validation;
using Companion.PlanV3;

// The Ava Mouth Training Data Factory.
//
// Separate from the companion service on purpose: it generates, evaluates, splits and exports
// without the conversational runtime running at all. What it shares with production is the part
// that must never diverge — the Plan/4 types, their validators, and MouthPromptV4, the one
// definition of the inference-time format.
//
// It does not train anything.

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
var output = ArgValue("--out") ?? Path.Combine(RepoRoot(), "training", "mouth-factory");
var rows = int.TryParse(ArgValue("--rows"), out var r) ? r : 1200;
var seed = long.TryParse(ArgValue("--seed"), out var s) ? s : 20260826;
var variants = int.TryParse(ArgValue("--variants"), out var v) ? v : 2;
var families = ArgValue("--families")?.Split(',', StringSplitOptions.RemoveEmptyEntries);

switch (command)
{
    case "inventory": return Inventory();
    case "dry-run": return await GenerateAsync(dryRun: true);
    case "pilot": return await GenerateAsync(dryRun: false);
    case "resume": return await GenerateAsync(dryRun: false);
    case "validate": return Validate();
    case "export": return await ExportAsync();
    case "critic-audit": return await CriticAuditAsync();
    default:
        Console.WriteLine("""
            mouth-factory <command> [options]

              inventory      the curriculum families and this run's planned counts
              dry-run        build every scenario and plan; generate nothing
              pilot          generate the pilot corpus end to end
              resume         continue an interrupted run (same --out and --seed)
              validate       re-run deterministic checks over accepted rows
              export         split, check contamination, write JSONL + Parquet + manifest
              critic-audit   matched-pair critic asymmetry audit

            options
              --out <dir>        output directory (default training/mouth-factory)
              --rows <n>         approximate scenario count (default 1200)
              --seed <n>         run seed (default 20260826)
              --variants <n>     targets per scenario (default 2)
              --families a1,b3   restrict to named families
            """);
        return 0;
}

// ---- commands ---------------------------------------------------------------------------------

int Inventory()
{
    var plan = PlanCounts();
    Console.WriteLine($"\nCurriculum: docs/RUN2_CURRICULUM_R5.md (supersedes R4)");
    Console.WriteLine($"Format:     {MouthPromptV4.FormatVersion}");
    Console.WriteLine($"Scenario:   {ScenarioTruth.SchemaVersion}   Row: {TrainingRow.SchemaVersion}\n");
    Console.WriteLine($"  {"family",-8}{"layer",-8}{"scenarios",-12}description");
    foreach (var family in Curriculum.Families)
        Console.WriteLine($"  {family.Id,-8}{family.Layer,-8}{plan.GetValueOrDefault(family.Id),-12}{family.Description}");
    Console.WriteLine($"\n  total scenarios: {plan.Values.Sum()}   x{variants} variants = "
                      + $"{plan.Values.Sum() * variants} candidate rows");
    return 0;
}

async Task<int> GenerateAsync(bool dryRun)
{
    var counts = PlanCounts();
    var generator = new ScenarioGenerator(seed);
    var scenarios = Curriculum.Families
        .Where(f => counts.ContainsKey(f.Id))
        .SelectMany(f => generator.Generate(f, counts[f.Id]))
        .ToList();

    Directory.CreateDirectory(output);
    var ledger = JobLedger.Open(Path.Combine(output, "ledger.jsonl"));
    var store = new RowStore(Path.Combine(output, "rows"));
    var roleRouter = BuildRoles(out var roleDescription);

    ITargetSource source = roleRouter is null
        ? new UnavailableTargetSource()
        : new ModelTargetSource(roleRouter, seed);

    if (roleRouter is null && !dryRun)
    {
        Console.Error.WriteLine(
            "No generation roles configured. Set MOUTH_WRITER_MODEL (and optionally "
            + "MOUTH_WRITER_ENDPOINT) to a local OpenAI-compatible model, or use `dry-run`.");
        return 2;
    }

    var pipeline = new FactoryPipeline(
        roleRouter ?? new RoleRouter(new Dictionary<Role, IRoleClient>()),
        ledger, store, new Deduplicator(), source);

    Console.WriteLine($"\n{(dryRun ? "DRY RUN" : "PILOT")}  scenarios={scenarios.Count}  "
                      + $"variants={variants}  seed={seed}");
    Console.WriteLine($"roles     {roleDescription}");
    Console.WriteLine($"output    {output}\n");

    var result = await pipeline.RunAsync(scenarios, new PipelineOptions
    {
        OutputDirectory = output,
        TargetsPerScenario = variants,
        DryRun = dryRun,
    });

    File.WriteAllText(
        Path.Combine(output, "scenarios.jsonl"),
        string.Join('\n', scenarios.Select(sc => JsonSerializer.Serialize(sc, Web()))) + "\n");

    Report(result);
    return result.Accepted == 0 && !dryRun ? 1 : 0;
}

int Validate()
{
    var store = new RowStore(Path.Combine(output, "rows"));
    var accepted = store.ReadRows(Disposition.Accepted).ToList();
    if (accepted.Count == 0)
    {
        Console.Error.WriteLine("no accepted rows found; run `pilot` first");
        return 1;
    }

    // The invariant worth re-checking after the fact: every accepted row is in the shipping
    // format and carries no metadata.
    var wrongFormat = accepted.Where(a => a.FormatVersion != MouthPromptV4.FormatVersion).ToList();
    Console.WriteLine($"\naccepted rows            {accepted.Count}");
    Console.WriteLine($"format {MouthPromptV4.FormatVersion,-18}{accepted.Count - wrongFormat.Count} ok, {wrongFormat.Count} wrong");

    var leaked = accepted.Where(a =>
        a.Target.Contains("must_express", StringComparison.OrdinalIgnoreCase)
        || a.Target.Contains("[plan/", StringComparison.OrdinalIgnoreCase)).ToList();
    Console.WriteLine($"plan echo in target      {leaked.Count}");

    return wrongFormat.Count == 0 && leaked.Count == 0 ? 0 : 1;
}

async Task<int> ExportAsync()
{
    var store = new RowStore(Path.Combine(output, "rows"));
    var rowList = store.ReadRows(Disposition.Accepted).ToList();
    var metadata = store.ReadMetadata(Disposition.Accepted).ToDictionary(m => m.Id, StringComparer.Ordinal);
    if (rowList.Count == 0)
    {
        Console.Error.WriteLine("no accepted rows to export");
        return 1;
    }

    var scenarioPath = Path.Combine(output, "scenarios.jsonl");
    var scenarios = File.Exists(scenarioPath)
        ? File.ReadLines(scenarioPath).Where(l => l.Length > 0)
            .Select(l => JsonSerializer.Deserialize<ScenarioTruth>(l, Web())!).ToList()
        : [];

    var split = FamilySplitter.Plan(scenarios);
    var paired = rowList
        .Where(row => metadata.ContainsKey(row.Id))
        .Select(row =>
        {
            var meta = metadata[row.Id];
            return (Row: row, Meta: meta with
            {
                Split = split.FamilyToSplit.GetValueOrDefault(meta.ScenarioFamilyId, "train"),
            });
        })
        .ToList();

    var findings = Contamination.Search(paired, PriorCorpusTargets());
    Console.WriteLine($"\ncontamination findings   {findings.Count}");
    foreach (var f in findings.Take(10))
        Console.WriteLine($"  {f.Where,-24}{f.RowId}  {f.Detail}");
    if (findings.Count > 0)
    {
        Console.Error.WriteLine("\nRefusing to export: contamination must be resolved before a freeze.");
        return 1;
    }

    var exportDir = Path.Combine(output, "export");
    var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var group in paired.GroupBy(p => p.Meta.Split!, StringComparer.Ordinal))
    {
        var name = $"mouth-v2-{group.Key}";
        var list = group.Select(g => g.Row).ToList();
        var jsonl = Exports.WriteJsonl(exportDir, name, list);
        var parquet = await Exports.WriteParquetAsync(exportDir, name, list);
        hashes[Path.GetFileName(jsonl)] = Exports.Sha256OfFile(jsonl);
        hashes[Path.GetFileName(parquet)] = Exports.Sha256OfFile(parquet);
        Console.WriteLine($"  {group.Key,-12}{list.Count,6} rows -> {Path.GetFileName(jsonl)}, {Path.GetFileName(parquet)}");
    }

    var manifest = new RunManifest
    {
        RunId = $"pilot-{seed}",
        StartedUtc = DateTimeOffset.UtcNow.ToString("O"),
        SchemaVersion = ScenarioTruth.SchemaVersion,
        RowSchemaVersion = TrainingRow.SchemaVersion,
        PromptFormatVersion = MouthPromptV4.FormatVersion,
        RepoCommit = Environment.GetEnvironmentVariable("MOUTH_FACTORY_COMMIT") ?? "(unrecorded)",
        Roles = RoleDescription(),
        Generated = paired.Count,
        Accepted = paired.Count,
        Rejected = store.ReadRows(Disposition.Rejected).Count(),
        ManualReview = store.ReadRows(Disposition.ManualReview).Count(),
        Sources = SourceManifests(paired),
        ExportHashes = hashes,
        KnownLimitations =
        [
            "Corpus size is provisional until the RTX 5070 probe fixes base, sequence length and rank.",
            "MouthPromptV4 defines the plan/4 inference format; no production path serves it yet.",
        ],
    };
    File.WriteAllText(
        Path.Combine(exportDir, "manifest.json"),
        JsonSerializer.Serialize(manifest, new JsonSerializerOptions(Web()) { WriteIndented = true }));

    Console.WriteLine($"\nheld-out compositions    {split.UnseenCompositions.Count}");
    Console.WriteLine($"manifest                 {Path.Combine(exportDir, "manifest.json")}");
    return 0;
}

async Task<int> CriticAuditAsync()
{
    var roleRouter = BuildRoles(out _);
    if (roleRouter is null || !roleRouter.Has(Role.NaturalnessCritic))
    {
        Console.Error.WriteLine("configure MOUTH_NATURALNESS_MODEL to audit a critic");
        return 2;
    }

    var source = new ModelTargetSource(roleRouter, seed);
    var pairs = MatchedPairs.Build();
    var report = await CriticAsymmetry.AuditAsync(pairs, async (scenario, target, ct) =>
    {
        var checks = await source.CriticiseAsync(scenario, target, ct);
        return checks.Any(c => !c.Passed);
    });

    Console.WriteLine($"\nmatched-pair critic asymmetry ({pairs.Count} pairs, ceiling {report.Ceiling:P0})\n");
    foreach (var variant in report.Variants)
        Console.WriteLine($"  {variant.Variant,-14}{variant.Rejected,4}/{variant.Judged,-6}"
                          + $"{variant.RejectionRate,7:P1}   delta {report.Deltas[variant.Variant],+7:P1}");

    Console.WriteLine();
    if (report.CriticAcceptable)
    {
        Console.WriteLine("PASS - no register is rejected materially more than neutral.");
        return 0;
    }
    Console.Error.WriteLine(
        $"FAIL - {string.Join(", ", report.OffendingVariants)} exceed the ceiling. "
        + "Recalibrate or replace the critic. The material stays.");
    return 1;
}

// ---- helpers -----------------------------------------------------------------------------------

Dictionary<string, int> PlanCounts()
{
    var selected = families is null
        ? Curriculum.Families
        : Curriculum.Families.Where(f => families.Contains(f.Id, StringComparer.OrdinalIgnoreCase)).ToList();
    var totalShare = selected.Sum(f => f.PilotShare);
    if (totalShare == 0)
        return [];
    var scenarioBudget = Math.Max(1, rows / Math.Max(1, variants));
    return selected.ToDictionary(
        f => f.Id,
        f => Math.Max(1, (int)Math.Round((double)f.PilotShare / totalShare * scenarioBudget)),
        StringComparer.Ordinal);
}

RoleRouter? BuildRoles(out string description)
{
    var clients = new Dictionary<Role, IRoleClient>();
    var described = new List<string>();
    var http = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };

    void Add(Role role, string envPrefix)
    {
        var model = Environment.GetEnvironmentVariable($"MOUTH_{envPrefix}_MODEL");
        if (string.IsNullOrWhiteSpace(model))
            return;
        var endpoint = Environment.GetEnvironmentVariable($"MOUTH_{envPrefix}_ENDPOINT")
                       ?? "http://localhost:11434/v1";
        clients[role] = new LocalChatRoleClient(http, role, new RoleConfig
        {
            Model = model, Endpoint = endpoint,
        });
        described.Add($"{role}={model}");
    }

    Add(Role.TargetWriter, "WRITER");
    Add(Role.FaithfulnessCritic, "FAITHFULNESS");
    Add(Role.NaturalnessCritic, "NATURALNESS");
    Add(Role.StyleCritic, "STYLE");
    Add(Role.AdversarialCritic, "ADVERSARIAL");

    description = described.Count == 0 ? "(none configured)" : string.Join("  ", described);
    return clients.ContainsKey(Role.TargetWriter) ? new RoleRouter(clients) : null;
}

Dictionary<string, string> RoleDescription()
{
    var map = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var (role, prefix) in new[]
             {
                 (Role.TargetWriter, "WRITER"), (Role.FaithfulnessCritic, "FAITHFULNESS"),
                 (Role.NaturalnessCritic, "NATURALNESS"), (Role.StyleCritic, "STYLE"),
                 (Role.AdversarialCritic, "ADVERSARIAL"),
             })
    {
        var model = Environment.GetEnvironmentVariable($"MOUTH_{prefix}_MODEL");
        if (!string.IsNullOrWhiteSpace(model))
            map[role.ToString()] = model;
    }
    return map;
}

List<SourceManifest> SourceManifests(List<(TrainingRow Row, TrainingRowMetadata Meta)> paired)
    => paired
        .GroupBy(p => p.Meta.SourceFamilyId, StringComparer.Ordinal)
        .Select(g => new SourceManifest
        {
            FamilyId = g.Key,
            Origin = "generated",
            Revision = $"seed={seed};format={MouthPromptV4.FormatVersion}",
            License = "generated-in-house",
            PermittedUse = "unrestricted internal training use",
            Transformations = "constructed from scenario truth; rendered via MouthPromptV4",
            RowCount = g.Count(),
        })
        .OrderBy(m => m.FamilyId, StringComparer.Ordinal)
        .ToList();

/// <summary>Run-1 targets, so the contamination search can see overlap with what run-1c saw.</summary>
List<string> PriorCorpusTargets()
{
    var path = Path.Combine(RepoRoot(), "training", "renderer", "dataset", "train-200.jsonl");
    if (!File.Exists(path))
        return [];
    var targets = new List<string>();
    foreach (var line in File.ReadLines(path))
    {
        if (string.IsNullOrWhiteSpace(line))
            continue;
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("target", out var t) && t.GetString() is { } text)
                targets.Add(text);
        }
        catch (JsonException) { /* a malformed line is not a contamination signal */ }
    }
    return targets;
}

void Report(PipelineResult result)
{
    Console.WriteLine($"scenarios       {result.ScenariosBuilt}");
    Console.WriteLine($"candidates      {result.CandidatesGenerated}");
    Console.WriteLine($"accepted        {result.Accepted}");
    Console.WriteLine($"rejected        {result.Rejected}");
    Console.WriteLine($"manual review   {result.ManualReview}");

    if (result.RejectionCodes.Count > 0)
    {
        Console.WriteLine("\nrejection reasons");
        foreach (var (code, count) in result.RejectionCodes.OrderByDescending(kv => kv.Value))
            Console.WriteLine($"  {count,6}  {code}");
    }

    if (result.AcceptedRows.Count > 0)
    {
        var distribution = Distribution.Build(result.AcceptedRows.Select(a => a.Meta).ToList());
        Console.WriteLine("\ncontext-length buckets");
        foreach (var (bucket, count) in distribution.ByContextBucket.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            Console.WriteLine($"  {count,6}  {bucket}");
        Console.WriteLine($"\nrepeated openings   {distribution.RepeatedOpeningShare:P1}");
    }
}

string? ArgValue(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static string RepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
        dir = dir.Parent;
    return dir?.FullName ?? Directory.GetCurrentDirectory();
}

static JsonSerializerOptions Web() => new(JsonSerializerDefaults.Web);

/// <summary>Used by dry-run, where no model is configured and none should be called.</summary>
file sealed class UnavailableTargetSource : ITargetSource
{
    public Task<TargetCandidate> WriteAsync(
        ScenarioTruth scenario, PlanV3 plan, int variant, CancellationToken ct = default)
        => throw new InvalidOperationException("dry-run must not generate");

    public Task<IReadOnlyList<CheckResult>> CriticiseAsync(
        ScenarioTruth scenario, string target, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<CheckResult>>([]);
}
