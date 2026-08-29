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
var targetAccepted = int.TryParse(ArgValue("--target-accepted"), out var ta) ? ta : (int?)null;
var maxUnits = int.TryParse(ArgValue("--max-units"), out var mu) ? mu : (int?)null;
var batchSize = int.TryParse(ArgValue("--batch"), out var bs) ? bs : 64;
var interleaved = Array.IndexOf(args, "--interleaved") >= 0;

switch (command)
{
    case "inventory": return Inventory();
    case "dry-run": return await GenerateAsync(dryRun: true);
    case "pilot": return await GenerateAsync(dryRun: false);
    case "resume": return await GenerateAsync(dryRun: false);
    case "validate": return Validate();
    case "export": return await ExportAsync();
    case "critic-audit": return await CriticAuditAsync();
    case "freeze": return await FreezeAsync();
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
              freeze         evaluate the declared acceptance conditions; export only if all pass

            options
              --out <dir>        output directory (default training/mouth-factory)
              --rows <n>         approximate scenario count (default 1200)
              --seed <n>         run seed (default 20260826)
              --variants <n>     targets per scenario (default 2)
              --families a1,b3   restrict to named families

            corpus size
              --rows N           candidate UNITS attempted (scenario x variant). Unchanged.
              --target-accepted N  stop once N rows have been ACCEPTED. Resumable: counts what
                                   earlier runs already accepted.
              --max-units N        hard ceiling on units attempted, so an unreachable target
                                   stops instead of consuming the corpus.
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

    // What each deterministic gate will actually have to look at, BEFORE any model is called.
    // A gate reading a field nothing populates reports zero failures and reads as approval; the
    // pilot published a pass rate over three such gates and it took the finished corpus to notice.
    var coverage = CheckCoverage.Measure(scenarios);
    Console.WriteLine("\ndeterministic gate coverage");
    foreach (var row in coverage.Rows.OrderBy(r => r.Status, StringComparer.Ordinal)
                 .ThenBy(r => r.Check, StringComparer.Ordinal))
        Console.WriteLine($"  {row.Status,-8}{row.Check,-26}{row.Scenarios,6} scenarios supply data");
    if (!coverage.Ok)
    {
        Console.Error.WriteLine();
        foreach (var row in coverage.Missing)
            Console.Error.WriteLine(
                $"COVERAGE: '{row.Check}' has no data in any of the {scenarios.Count} scenarios "
                + "built. The gate would run and enforce nothing.");
        Console.Error.WriteLine("Refusing to generate against gates that cannot fire.");
        return 5;
    }

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

    // Role independence, before anything is generated. RoleRouter guarantees one invocation
    // never both writes and approves; it says nothing about which weights back each slot, and a
    // gating critic sharing the writer's model is marking its own homework.
    var roleModels = RoleModels();
    var violations = RoleIndependence.Check(roleModels);
    if (violations.Count > 0 && !dryRun)
    {
        Console.Error.WriteLine();
        foreach (var v in violations)
            Console.Error.WriteLine("ROLE INDEPENDENCE: " + v.Detail);
        Console.Error.WriteLine("Refusing to generate gated rows.");
        return 4;
    }
    foreach (var note in RoleIndependence.CorrelatedCritics(roleModels))
        Console.WriteLine("note      " + note);

    // Preflight before any unattended run. A wedged GPU runner answers /api/version happily and
    // hangs on generation, so only a real test completion detects it.
    if (!dryRun)
    {
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
        var writerModel = Environment.GetEnvironmentVariable("MOUTH_WRITER_MODEL")!;
        var writerEndpoint = Environment.GetEnvironmentVariable("MOUTH_WRITER_ENDPOINT")
                             ?? "http://localhost:11434/v1";
        var health = await OllamaPreflight.CheckAsync(probe, writerEndpoint, writerModel);
        if (!health.Healthy)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("PREFLIGHT FAILED: " + health.Detail);
            if (health.Action is { } act)
                Console.Error.WriteLine("  -> " + act);
            Console.Error.WriteLine("Refusing to generate. No job was killed.");
            return 3;
        }
        Console.WriteLine("preflight  " + health.Detail);
    }

    var pipeline = new FactoryPipeline(
        roleRouter ?? new RoleRouter(new Dictionary<Role, IRoleClient>()),
        ledger, store, new Deduplicator(), source);

    Console.WriteLine($"\n{(dryRun ? "DRY RUN" : "PILOT")}  scenarios={scenarios.Count}  "
                      + $"variants={variants}  seed={seed}");
    Console.WriteLine($"roles     {roleDescription}");
    Console.WriteLine($"output    {output}\n");

    // Stage-batched by default: one model loaded per stage instead of a reload between
    // almost every call. --interleaved keeps the original schedule for comparison; both
    // produce identical dispositions, which the tests pin.
    if (!dryRun && !interleaved)
    {
        var criticRoles = new[] { Role.FaithfulnessCritic, Role.AdversarialCritic, Role.NaturalnessCritic }
            .Where(r => roleRouter!.Has(r)).Select(r => r.ToString()).ToList();
        var quota = new AcceptanceQuota(QuestionPolicyMix.FrozenRun1);

        // Replacement generation continues the deterministic index sequence past where the initial
        // build stopped, so a short bucket can be filled without re-rolling anything.
        var supply = new GeneratorScenarioSupply(generator, Curriculum.Families, counts);
        var staged = new StagedPipeline(
            source, CandidateStore.Open(Path.Combine(output, "candidates.jsonl")),
            store, new Deduplicator(), criticRoles, quota, supply);
        var sr = await staged.RunAsync(scenarios, new PipelineOptions
        {
            OutputDirectory = output, TargetsPerScenario = variants,
            TargetAccepted = targetAccepted, MaxUnits = maxUnits,
        }, batchSize);

        // Every scenario the run touched, including replacements. The export resolves families
        // and splits from this file, so a scenario that produced a row and is not written here is
        // a row that cannot be exported.
        var allScenarios = scenarios
            .Concat(supply.Built)
            .GroupBy(sc => sc.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
        File.WriteAllText(Path.Combine(output, "scenarios.jsonl"),
            string.Join(Environment.NewLine,
                allScenarios.Select(sc => JsonSerializer.Serialize(sc, Web())))
            + Environment.NewLine);

        Console.WriteLine($"stop reason     {sr.StopReason}   rounds {sr.Rounds}");
        Console.WriteLine($"replacements    {sr.ReplacementScenarios} scenarios generated to fill short quotas");
        Console.WriteLine($"scenarios       {scenarios.Count}  (unsatisfiable: {sr.Unsatisfiable})");
        Console.WriteLine($"units attempted {sr.UnitsAttempted}");
        Console.WriteLine($"model loads     {sr.ModelLoads}   writer calls {sr.WriterCalls}   critic calls {sr.CriticCalls}");
        var d = Math.Max(1, sr.UnitsAttempted);
        Console.WriteLine($"  deterministic pass  {sr.Accepted + sr.ManualReview}/{d} ({(sr.Accepted + sr.ManualReview) / (double)d:P1})");
        Console.WriteLine($"  critic accepted     {sr.Accepted}/{d} ({sr.Accepted / (double)d:P1})");
        Console.WriteLine($"  manual review       {sr.ManualReview}/{d} ({sr.ManualReview / (double)d:P1})");
        Console.WriteLine($"rejected        {sr.Rejected}");
        foreach (var drifted in sr.DriftDetected.Take(5))
            Console.WriteLine($"  DRIFT {drifted}");
        if (sr.RejectionCodes.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("rejection reasons");
            foreach (var (code, count) in sr.RejectionCodes.OrderByDescending(kv => kv.Value))
                Console.WriteLine($"  {count,6}  {code}");
        }

        ReportCorpus(store, allScenarios, quota);
        return sr.Accepted == 0 ? 1 : 0;
    }

    var result = await pipeline.RunAsync(scenarios, new PipelineOptions
    {
        OutputDirectory = output,
        TargetsPerScenario = variants,
        DryRun = dryRun,
        TargetAccepted = targetAccepted,
        MaxUnits = maxUnits,
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

    // Deliberately difficult forbidden-question cases go to the hard split rather than diluting
    // train/validation, and are reported apart from the production-weighted body.
    var hardFamilies = scenarios.Where(sc => sc.HardCase)
        .Select(sc => sc.ScenarioFamilyId)
        .ToHashSet(StringComparer.Ordinal);
    var split = FamilySplitter.Plan(scenarios, hardFamilies: hardFamilies);
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


/// <summary>
/// The freeze gate. Evaluate every condition declared before the run; export only if all hold.
///
/// A failing condition ends the command. Nothing is redesigned, nothing is re-tuned, and no
/// corpus is written — the failure IS the report. That rule exists because the two exploratory
/// pilots both ended in a judgement about whether "close enough" was close enough, and a freeze
/// candidate is precisely the run where that judgement is not available.
/// </summary>
async Task<int> FreezeAsync()
{
    var store = new RowStore(Path.Combine(output, "rows"));
    var accepted = store.ReadRows(Disposition.Accepted).ToList();
    var metadata = store.ReadMetadata(Disposition.Accepted).ToList();
    var manualReview = store.ReadRows(Disposition.ManualReview).Count();
    if (accepted.Count == 0)
    {
        Console.Error.WriteLine("no accepted rows; run the generation first");
        return 1;
    }

    var scenarioPath = Path.Combine(output, "scenarios.jsonl");
    var scenarios = File.Exists(scenarioPath)
        ? File.ReadLines(scenarioPath).Where(l => l.Length > 0)
            .Select(l => JsonSerializer.Deserialize<ScenarioTruth>(l, Web())!)
            .GroupBy(sc => sc.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal)
        : [];

    // The quota is recomputed from the corpus on disk rather than trusted from the run, so the
    // freeze gate measures the artifact it is about to hash.
    var quota = new AcceptanceQuota(QuestionPolicyMix.FrozenRun1);
    foreach (var meta in metadata)
        if (scenarios.TryGetValue(meta.ScenarioId, out var sc))
            quota.Record(sc);

    var coverage = CheckCoverage.Measure(scenarios.Values.ToList());

    var paired = accepted
        .Where(row => metadata.Any(m => m.Id == row.Id))
        .Select(row => (Row: row, Meta: metadata.First(m => m.Id == row.Id)))
        .ToList();
    var contamination = Contamination.Search(paired, PriorCorpusTargets());

    var checks = AcceptanceReport.Evaluate(
        accepted, metadata, scenarios, quota, coverage, contamination, manualReview,
        minimumRows: targetAccepted ?? 1500);

    Console.WriteLine();
    Console.WriteLine("DECLARED ACCEPTANCE CONDITIONS");
    Console.WriteLine();
    foreach (var check in checks)
        Console.WriteLine($"  {(check.Passed ? "PASS" : "FAIL"),-6}{check.Name,-46}{check.Detail}");

    var failed = checks.Where(c => !c.Passed).ToList();
    Console.WriteLine();
    if (failed.Count > 0)
    {
        Console.Error.WriteLine($"FREEZE REFUSED - {failed.Count} declared condition(s) failed:");
        foreach (var f in failed)
            Console.Error.WriteLine($"  {f.Name}: {f.Detail}");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Nothing was exported and nothing was frozen.");
        return 1;
    }

    Console.WriteLine("All declared conditions hold. Exporting.");
    return await ExportAsync();
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

/// <summary>Role -> configured model identifier, for the independence check.</summary>
Dictionary<Role, string> RoleModels()
{
    var map = new Dictionary<Role, string>();
    foreach (var (role, prefix) in new[]
             {
                 (Role.TargetWriter, "WRITER"), (Role.FaithfulnessCritic, "FAITHFULNESS"),
                 (Role.NaturalnessCritic, "NATURALNESS"), (Role.StyleCritic, "STYLE"),
                 (Role.AdversarialCritic, "ADVERSARIAL"),
             })
    {
        var model = Environment.GetEnvironmentVariable($"MOUTH_{prefix}_MODEL");
        if (!string.IsNullOrWhiteSpace(model))
            map[role] = model;
    }
    return map;
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
    Console.WriteLine($"stop reason     {result.StopReason}");
    Console.WriteLine($"scenarios       {result.ScenariosBuilt}  (unsatisfiable, not attempted: {result.Unsatisfiable})");
    Console.WriteLine($"units attempted {result.UnitsAttempted}");
    Console.WriteLine($"candidates      {result.CandidatesGenerated}");
    var denom = Math.Max(1, result.UnitsAttempted);
    Console.WriteLine($"  deterministic pass  {result.Accepted + result.ManualReview}/{denom}"
                      + $" ({(result.Accepted + result.ManualReview) / (double)denom:P1})");
    Console.WriteLine($"  critic accepted     {result.Accepted}/{denom} ({result.Accepted / (double)denom:P1})");
    Console.WriteLine($"  manual review       {result.ManualReview}/{denom} ({result.ManualReview / (double)denom:P1})");
    Console.WriteLine($"accepted        {result.Accepted}");
    Console.WriteLine($"rejected        {result.Rejected}");
    Console.WriteLine($"manual review   {result.ManualReview}");

    if (result.RejectionCodes.Count > 0)
    {
        Console.WriteLine("\nrejection reasons");
        foreach (var (code, count) in result.RejectionCodes.OrderByDescending(kv => kv.Value))
            Console.WriteLine($"  {count,6}  {code}");
    }

    // Hard cases are reported apart: they are deliberately difficult, and folding them into one
    // acceptance rate makes a production-weighted corpus look worse than it is.
    var hard = result.AcceptedRows.Count(a => a.Meta.HardCase);
    var hardScenarios = result.Scenarios.Count(sc => sc.HardCase);
    Console.WriteLine();
    var hardShare = result.Scenarios.Count == 0 ? 0d : (double)hardScenarios / result.Scenarios.Count;
    Console.WriteLine($"hard cases      {hardScenarios} scenarios ({hardShare:P1}), {hard} accepted");

    var policies = result.Scenarios.GroupBy(sc => sc.Question.Policy, StringComparer.Ordinal)
        .OrderByDescending(g => g.Count());
    Console.WriteLine("question policy mix");
    foreach (var g in policies)
        Console.WriteLine($"  {g.Count(),6}  {g.Key,-12}{(double)g.Count() / Math.Max(1, result.Scenarios.Count):P1}");

    if (result.AcceptedRows.Count > 0)
    {
        var distribution = Distribution.Build(result.AcceptedRows.Select(a => a.Meta).ToList());
        Console.WriteLine("\ncontext-length buckets");
        foreach (var (bucket, count) in distribution.ByContextBucket.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            Console.WriteLine($"  {count,6}  {bucket}");
        Console.WriteLine($"\nrepeated openings   {distribution.RepeatedOpeningShare:P1}");
    }
}

/// <summary>
/// What the ACCEPTED corpus actually looks like — the only distribution that gets trained on.
///
/// Each figure here answers a specific pilot finding: the question mix was steered at the
/// scenario level and delivered something else; 684 rejections were duplicate targets; and
/// openings were diverse across the corpus while converging hard inside each family.
/// </summary>
void ReportCorpus(RowStore store, List<ScenarioTruth> scenarios, AcceptanceQuota quota)
{
    var accepted = store.ReadRows(Disposition.Accepted).ToList();
    var meta = store.ReadMetadata(Disposition.Accepted).ToList();
    if (accepted.Count == 0)
        return;

    var byId = scenarios.ToDictionary(s => s.Id, StringComparer.Ordinal);

    Console.WriteLine();
    Console.WriteLine("accepted question policy   (target = frozen run-1)");
    foreach (var policy in new[] { "none", "must_ask", "may_ask" })
    {
        var n = quota.AcceptedIn(policy);
        Console.WriteLine($"  {policy,-10}{n,6}{n / (double)Math.Max(1, quota.Total),9:P1}"
                          + $"   target {quota.TargetShare(policy),7:P1}");
    }

    // Unique-target yield: how much of the accepted corpus is distinct text. A second variant
    // that reproduces the first is budget spent for nothing.
    var distinct = accepted.Select(a => Normalise(a.Target))
        .ToHashSet(StringComparer.Ordinal).Count;
    Console.WriteLine();
    Console.WriteLine($"unique-target yield        {distinct}/{accepted.Count} "
                      + $"({distinct / (double)accepted.Count:P1}) distinct accepted targets");
    var multi = meta.GroupBy(m => m.ScenarioId, StringComparer.Ordinal).Count(g => g.Count() > 1);
    Console.WriteLine($"  scenarios with >1 accepted target {multi}");

    // Within-family opening diversity. Corpus-wide diversity hid this in the pilot: 425 distinct
    // openings over 1,528 rows looked healthy while one family repeated a single opening 104 times.
    Console.WriteLine();
    Console.WriteLine("within-family opening diversity");
    var families = meta
        .GroupBy(m => m.FamilyId, StringComparer.Ordinal)
        .Select(g => new
        {
            Family = g.Key,
            Rows = g.Count(),
            Distinct = g.Select(m => m.Opening ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase).Count,
        })
        .Select(x => new { x.Family, x.Rows, x.Distinct, Ratio = x.Distinct / (double)x.Rows })
        .OrderBy(x => x.Ratio)
        .ToList();
    foreach (var f in families.Take(8))
        Console.WriteLine($"  {f.Family,-6}{f.Distinct,5}/{f.Rows,-6}{f.Ratio,8:P1} distinct openings");
    if (families.Count > 0)
        Console.WriteLine($"  {"median",-6}{families[families.Count / 2].Ratio,19:P1}");

    // Must-express density against the frozen anchor the generator draws from.
    var densities = meta
        .Select(m => byId.TryGetValue(m.ScenarioId, out var sc) ? sc : null)
        .Where(sc => sc is not null)
        .GroupBy(sc => sc!.ApprovedFacts.Count(f => f.Policy == FactPolicy.MustExpress))
        .ToDictionary(g => g.Key, g => g.Count());
    var totalDensity = densities.Values.Sum();
    Console.WriteLine();
    Console.WriteLine("must-express density       (target = frozen run-1)");
    foreach (var (count, share) in new[] { (0, 0.174), (1, 0.638), (2, 0.158), (3, 0.030) })
    {
        var n = densities.GetValueOrDefault(count);
        Console.WriteLine($"  {count} must  {n,6}{n / (double)Math.Max(1, totalDensity),9:P1}"
                          + $"   target {share,7:P1}");
    }

    var split = meta.GroupBy(m => m.Split ?? "(unassigned)", StringComparer.Ordinal)
        .OrderBy(g => g.Key, StringComparer.Ordinal);
    Console.WriteLine();
    Console.WriteLine("split assignment");
    foreach (var g in split)
        Console.WriteLine($"  {g.Key,-14}{g.Count(),6}");

    static string Normalise(string text)
        => string.Join(' ', text.ToLowerInvariant()
            .Split([' ', '\n', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries));
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

    public Task<CriticVerdict> CriticiseOneAsync(
        string role, ScenarioTruth scenario, string target, CancellationToken ct = default)
        => throw new InvalidOperationException("dry-run must not criticise");
}
