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
var dryRunOnly = Array.IndexOf(args, "--dry-run") >= 0;

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
    case "score": return await ScoreAsync();
    case "supplement": return await SupplementAsync();
    case "supplement-freeze": return SupplementFreeze();
    case "reissue": return await ReissueAsync();
    case "register-supplement": return await RegisterSupplementAsync();
    case "register-freeze": return RegisterFreeze();
    case "register-audit": return await RegisterAuditAsync();
    case "shadow-probe": return await ShadowProbeAsync();
    case "contract-probe": return await ShadowProbeAsync(
        Companion.MouthFactory.Probe.ShadowProbe.MatchedTriplet());
    case "hardset-probe": return await ShadowProbeAsync(
        Companion.MouthFactory.Probe.ShadowProbe.RequiredHardSet());
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
              score          score generated replies against scenario truth (--generations, --arm, --split)
              supplement     generate the Run-2.1 targeted supplement (additive; never touches Run-2)
              supplement-freeze  check the supplement's own bar, then hash and export it
              reissue        regenerate only the rows the ADMIT change affected, into a new dataset
              shadow-probe   drive named plan compositions through the live serving path

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
    Console.WriteLine($"Scenario:   {ScenarioTruth.SchemaVersion}   Row: {TrainingRow.SchemaVersion}");
    Console.WriteLine($"Protocol:   {PlanV3Codec.ProtocolHash()[..16]}  "
                      + "(section contract; an adapter trained under another is refused)\n");
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
    var allRows = store.ReadRows(Disposition.Accepted).ToList();
    var allMeta = store.ReadMetadata(Disposition.Accepted)
        .ToDictionary(m => m.Id, StringComparer.Ordinal);
    var manualReview = store.ReadRows(Disposition.ManualReview).Count();
    if (allRows.Count == 0)
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

    // The candidate pool is the accepted MAIN rows. Hard cases are exported separately for
    // evaluation and are never counted in the training mixture.
    var hardIds = allMeta.Values
        .Where(m => string.Equals(m.Split, "hard", StringComparison.Ordinal))
        .Select(m => m.Id).ToHashSet(StringComparer.Ordinal);

    var pool = allMeta.Values
        .Where(m => !hardIds.Contains(m.Id) && scenarios.ContainsKey(m.ScenarioId))
        .Select(m =>
        {
            var sc = scenarios[m.ScenarioId];
            return new CorpusSelection.Candidate(
                m.Id, m.FamilyId, sc.Question.Policy,
                !sc.ApprovedFacts.Any(f => f.Policy == FactPolicy.MustExpress),
                m.Opening ?? "", m.Split ?? "train");
        })
        .OrderBy(c => c.Id, StringComparer.Ordinal)
        .ToList();

    var request = new SelectionRequest
    {
        TotalRows = 2000,
        PolicyTargets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["none"] = 1266, ["must_ask"] = 428, ["may_ask"] = 306,
        },
        NoMustRows = 348,
        MinimumRows = 1500,
    };

    Console.WriteLine();
    Console.WriteLine($"candidate pool           {pool.Count} accepted main rows, "
                      + $"{hardIds.Count} hard held for evaluation");

    var selection = CorpusSelection.Select(pool, request, seed);
    if (!selection.Feasible)
    {
        var largest = CorpusSelection.LargestFeasible(pool, request);
        Console.Error.WriteLine();
        Console.Error.WriteLine("SELECTION INFEASIBLE - conflicting constraints:");
        foreach (var conflict in selection.Conflicts)
            Console.Error.WriteLine("  " + conflict);
        Console.Error.WriteLine();
        Console.Error.WriteLine(largest >= request.MinimumRows
            ? $"Largest feasible corpus in the requested proportions: {largest} rows."
            : $"Largest feasible corpus is {largest} rows, below the {request.MinimumRows} floor.");
        Console.Error.WriteLine("Nothing was exported and nothing was frozen.");
        return 1;
    }

    var selectedIds = selection.SelectedIds.ToHashSet(StringComparer.Ordinal);
    var accepted = allRows.Where(r => selectedIds.Contains(r.Id)).ToList();
    var metadata = selection.SelectedIds.Select(id => allMeta[id]).ToList();

    Console.WriteLine($"selected                 {accepted.Count} rows   "
                      + $"algorithm {selection.Algorithm}   seed {selection.Seed}");
    Console.WriteLine($"candidate pool hash      {selection.PoolHash}");
    Console.WriteLine($"selection hash           {selection.SelectionHash}");
    Console.WriteLine();
    Console.WriteLine($"  {"family",-8}{"pool",6}{"distinct",10}{"cap",6}{"selected",10}{"ratio",9}");
    foreach (var f in selection.Families)
    {
        var ratio = f.Selected == 0
            ? 0
            : Math.Min(f.Selected, f.DistinctOpenings) / (double)f.Selected;
        Console.WriteLine(
            $"  {f.Family,-8}{f.Pool,6}{f.DistinctOpenings,10}{f.Cap,6}{f.Selected,10}{ratio,9:P1}");
    }

    // The quota is recomputed over the SELECTED rows, so the freeze gate measures the artifact it
    // is about to hash rather than the pool that artifact was drawn from.
    var quota = new AcceptanceQuota(QuestionPolicyMix.FrozenRun1);
    foreach (var meta in metadata)
        if (scenarios.TryGetValue(meta.ScenarioId, out var sc))
            quota.Record(sc);

    var coverage = CheckCoverage.Measure(scenarios.Values.ToList());
    var paired = accepted.Select(row => (Row: row, Meta: allMeta[row.Id])).ToList();
    var contamination = Contamination.Search(paired, PriorCorpusTargets());

    var checks = AcceptanceReport.Evaluate(
        accepted, metadata, scenarios, quota, coverage, contamination, manualReview,
        minimumRows: request.MinimumRows);

    Console.WriteLine();
    Console.WriteLine("DECLARED ACCEPTANCE CONDITIONS (evaluated against the selected export)");
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

    Console.WriteLine("All declared conditions hold. Freezing.");
    return await ExportSelectedAsync(paired, allRows, allMeta, hardIds, selection, checks);
}

/// <summary>
/// Write the frozen corpus: the selected training rows by split, the hard cases apart for
/// evaluation, and a manifest recording exactly which candidates were chosen and from what.
/// </summary>
async Task<int> ExportSelectedAsync(
    List<(TrainingRow Row, TrainingRowMetadata Meta)> paired,
    List<TrainingRow> allRows,
    Dictionary<string, TrainingRowMetadata> allMeta,
    HashSet<string> hardIds,
    SelectionResult selection,
    IReadOnlyList<AcceptanceCheck> checks)
{
    var exportDir = Path.Combine(output, "export");
    var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
    var store = new RowStore(Path.Combine(output, "rows"));

    async Task WriteSplit(string name, List<TrainingRow> list)
    {
        var jsonl = Exports.WriteJsonl(exportDir, name, list);
        var parquet = await Exports.WriteParquetAsync(exportDir, name, list);
        hashes[Path.GetFileName(jsonl)] = Exports.Sha256OfFile(jsonl);
        hashes[Path.GetFileName(parquet)] = Exports.Sha256OfFile(parquet);
        Console.WriteLine($"  {name,-24}{list.Count,6} rows");
    }

    Console.WriteLine();
    foreach (var group in paired
                 .GroupBy(p => p.Meta.Split!, StringComparer.Ordinal)
                 .OrderBy(g => g.Key, StringComparer.Ordinal))
        await WriteSplit($"mouth-v2-{group.Key}", group.Select(g => g.Row).ToList());

    // Hard cases: exported for evaluation, never part of the training mixture.
    var hard = allRows.Where(r => hardIds.Contains(r.Id)).ToList();
    if (hard.Count > 0)
        await WriteSplit("mouth-v2-hard-eval", hard);

    var manifest = new RunManifest
    {
        RunId = $"run2-freeze-{seed}",
        StartedUtc = DateTimeOffset.UtcNow.ToString("O"),
        SchemaVersion = ScenarioTruth.SchemaVersion,
        RowSchemaVersion = TrainingRow.SchemaVersion,
        PromptFormatVersion = MouthPromptV4.FormatVersion,
        RepoCommit = Environment.GetEnvironmentVariable("MOUTH_FACTORY_COMMIT") ?? "(unrecorded)",
        Roles = RoleDescription(),
        Generated = allRows.Count,
        Accepted = paired.Count,
        Rejected = store.ReadRows(Disposition.Rejected).Count(),
        ManualReview = store.ReadRows(Disposition.ManualReview).Count(),
        Sources = SourceManifests(paired),
        ExportHashes = hashes,
        KnownLimitations =
        [
            "Corpus size is provisional until the RTX 5070 probe fixes base, sequence length and rank.",
            "MouthPromptV4 defines the plan/4 inference format; no production path serves it yet.",
            "The export is a balanced subset of a larger accepted pool. Every unselected row "
            + "remains in the candidate store; none was discarded.",
        ],
    };
    File.WriteAllText(
        Path.Combine(exportDir, "manifest.json"),
        JsonSerializer.Serialize(manifest, new JsonSerializerOptions(Web()) { WriteIndented = true }));

    // Selection provenance, kept apart from the run manifest because it answers a different
    // question: not how the rows were made, but which of them this corpus is.
    var selectionManifest = new
    {
        algorithm = selection.Algorithm,
        seed = selection.Seed,
        candidatePoolHash = selection.PoolHash,
        selectionHash = selection.SelectionHash,
        poolRows = allMeta.Count - hardIds.Count,
        selectedRows = selection.SelectedIds.Count,
        hardEvalRows = hardIds.Count,
        policyCounts = selection.PolicyCounts,
        noMustSelected = selection.NoMustSelected,
        families = selection.Families,
        acceptanceConditions = checks.Select(c => new { c.Name, c.Passed, c.Detail }),
        selectedCandidateIds = selection.SelectedIds,
    };
    var selectionPath = Path.Combine(exportDir, "selection.json");
    File.WriteAllText(
        selectionPath,
        JsonSerializer.Serialize(
            selectionManifest, new JsonSerializerOptions(Web()) { WriteIndented = true }));
    hashes["selection.json"] = Exports.Sha256OfFile(selectionPath);

    // manifest.json is deliberately NOT checksummed. It stamps the wall-clock freeze time, so its
    // hash changes on every run while the corpus does not - and a checksum file that never
    // reproduces teaches whoever verifies it to ignore a mismatch. SHA256SUMS covers exactly the
    // artifacts that must be byte-identical when the same pool is selected again.

    var hashPath = Path.Combine(exportDir, "SHA256SUMS");
    // LF and the two-space separator sha256sum expects, so `sha256sum -c SHA256SUMS` verifies
    // the freeze directly. Written with CRLF it parses filenames with a trailing carriage return
    // and reports every file missing - a checksum file no standard tool can read is not one.
    File.WriteAllText(
        hashPath,
        string.Concat(
            hashes.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => kv.Value + "  " + kv.Key + "\n")));

    Console.WriteLine();
    Console.WriteLine($"manifest                 {Path.Combine(exportDir, "manifest.json")}");
    Console.WriteLine($"selection                {selectionPath}");
    Console.WriteLine($"hashes                   {hashPath}");
    return 0;
}


/// <summary>
/// Score one arm's generated replies against the scenario truth they were generated from.
///
/// The instrument is DeterministicChecks, unchanged from the one the corpus was frozen against.
/// A separately-written evaluator would measure the gap between two implementations as readily as
/// it measures the model.
/// </summary>
// ==== Run-2.2 register supplement ==============================================================

/// <summary>
/// Generate the consensual-adult register supplement. Same pipeline as the ADMIT supplement -
/// writer, dedup, three independent critics - but its own composition and its own directory
/// (training/mouth/register-supplement), so no frozen artifact is touched.
/// </summary>
async Task<int> RegisterSupplementAsync()
{
    var registerOut = ArgValue("--out")
        ?? Path.Combine(RepoRoot(), "training", "mouth", "register-supplement");
    var generator = new RegisterSupplementGenerator(seed);
    var perSituation = int.TryParse(ArgValue("--per-situation"), out var ps) ? ps : 4;
    var scenarios = generator.Generate(perSituation).ToList();
    var splits = SupplementSplitter.AssignAll(scenarios)
        .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    Console.WriteLine();
    Console.WriteLine($"REGISTER SUPPLEMENT  scenarios={scenarios.Count}  seed={seed}");
    foreach (var g in scenarios.GroupBy(sc => sc.FamilyId, StringComparer.Ordinal)
                 .OrderBy(g => g.Key, StringComparer.Ordinal))
        Console.WriteLine($"  {g.Key,-6}{g.Count(),5} scenarios   {g.First().UserMessage[..Math.Min(40, g.First().UserMessage.Length)]}");

    // Composition invariant: every scenario carries a directed stance (a must_express fact), and
    // its facets are recoverable. A supplement that quietly contains a plan with no stance
    // teaches nothing about stance.
    var wrong = scenarios.Where(sc =>
        !sc.ApprovedFacts.Any(f => f.Policy == FactPolicy.MustExpress)).ToList();
    if (wrong.Count > 0)
    {
        Console.Error.WriteLine($"COMPOSITION: {wrong.Count} scenario(s) carry no directed stance.");
        return 5;
    }

    // Every declared register and stance must be PRESENT among the built scenarios - the freeze
    // will further require they be ACCEPTED, but absence at generation is caught here first.
    var builtRegisters = scenarios.Select(sc => RegisterSupplementGenerator.Facets(sc).Register).ToHashSet(StringComparer.Ordinal);
    var builtStances = scenarios.Select(sc => RegisterSupplementGenerator.Facets(sc).Stance).ToHashSet(StringComparer.Ordinal);
    var missingReg = RegisterSituations.Registers.Where(r => !builtRegisters.Contains(r)).ToList();
    var missingSt = RegisterSituations.Stances.Where(s => !builtStances.Contains(s)).ToList();
    if (missingReg.Count > 0 || missingSt.Count > 0)
    {
        Console.Error.WriteLine("COVERAGE: declared facets absent from generated scenarios: "
            + string.Join(", ", missingReg.Concat(missingSt)));
        return 5;
    }
    Console.WriteLine($"  composition + facet coverage verified ({builtRegisters.Count} registers, {builtStances.Count} stances)");

    Directory.CreateDirectory(registerOut);
    File.WriteAllText(Path.Combine(registerOut, "register-scenarios.jsonl"),
        string.Concat(scenarios.Select(sc => JsonSerializer.Serialize(sc, Web()) + "\n")));
    File.WriteAllText(Path.Combine(registerOut, "register-splits.json"),
        JsonSerializer.Serialize(new { algorithm = SupplementSplitter.Algorithm, seed, assignedBeforeGeneration = true, splits },
            new JsonSerializerOptions(Web()) { WriteIndented = true }));

    if (dryRunOnly)
    {
        Console.WriteLine($"\ndry run: scenarios written, nothing generated -> {registerOut}");
        return 0;
    }

    var roleRouter = BuildRoles(out var roleDescription);
    if (roleRouter is null)
    {
        Console.Error.WriteLine("Set MOUTH_WRITER_MODEL and the critic models, or pass --dry-run.");
        return 2;
    }
    var violations = RoleIndependence.Check(RoleModels());
    if (violations.Count > 0)
    {
        foreach (var v in violations) Console.Error.WriteLine("ROLE INDEPENDENCE: " + v.Detail);
        return 4;
    }

    // Refusal-asymmetry gate, BEFORE generating: a writer or gating critic that refuses the
    // explicit arm cannot be trusted to author or judge this supplement, and would bake the very
    // asymmetry we are correcting into the corpus.
    var auditRc = await RegisterAuditAsync();
    if (auditRc != 0)
    {
        Console.Error.WriteLine("Refusing to generate: a configured role refuses the explicit arm (see audit).");
        return auditRc;
    }

    using (var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(300) })
    {
        var health = await OllamaPreflight.CheckAsync(probe,
            Environment.GetEnvironmentVariable("MOUTH_WRITER_ENDPOINT") ?? "http://localhost:11434/v1",
            Environment.GetEnvironmentVariable("MOUTH_WRITER_MODEL")!);
        if (!health.Healthy) { Console.Error.WriteLine("PREFLIGHT FAILED: " + health.Detail); return 3; }
    }

    var source = new ModelTargetSource(roleRouter, seed);
    var store = new RowStore(Path.Combine(registerOut, "rows"));
    var criticRoles = new[] { Role.FaithfulnessCritic, Role.AdversarialCritic, Role.NaturalnessCritic }
        .Where(r => roleRouter.Has(r)).Select(r => r.ToString()).ToList();
    var staged = new StagedPipeline(source, CandidateStore.Open(Path.Combine(registerOut, "candidates.jsonl")),
        store, new Deduplicator(), criticRoles);

    Console.WriteLine($"roles     {roleDescription}");
    var sr = await staged.RunAsync(scenarios, new PipelineOptions
    {
        OutputDirectory = registerOut, TargetsPerScenario = variants, MaxUnits = maxUnits,
    }, batchSize);

    Console.WriteLine($"\nstop reason {sr.StopReason}  units {sr.UnitsAttempted}  "
        + $"accepted {sr.Accepted}  manual {sr.ManualReview}  rejected {sr.Rejected}");
    foreach (var (code, count) in sr.RejectionCodes.OrderByDescending(kv => kv.Value))
        Console.WriteLine($"  {count,6}  {code}");
    return sr.Accepted == 0 ? 1 : 0;
}

/// <summary>
/// The register supplement's freeze: apply the stance-fidelity bar, require nonzero accepted
/// coverage for EVERY declared register and stance (the invariant the original corpus lacked -
/// "permitted" was mistaken for "represented"), then hash and export.
/// </summary>
int RegisterFreeze()
{
    var registerOut = ArgValue("--out")
        ?? Path.Combine(RepoRoot(), "training", "mouth", "register-supplement");
    var store = new RowStore(Path.Combine(registerOut, "rows"));
    var rows = store.ReadRows(Disposition.Accepted).ToList();
    var meta = store.ReadMetadata(Disposition.Accepted)
        .GroupBy(m => m.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    if (rows.Count == 0) { Console.Error.WriteLine("no accepted register rows; run `register-supplement` first"); return 1; }

    var scenarios = File.ReadLines(Path.Combine(registerOut, "register-scenarios.jsonl"))
        .Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<ScenarioTruth>(l, Web())!)
        .GroupBy(sc => sc.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    var splits = JsonDocument.Parse(File.ReadAllText(Path.Combine(registerOut, "register-splits.json")))
        .RootElement.GetProperty("splits").EnumerateObject()
        .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);

    var kept = new List<(TrainingRow Row, TrainingRowMetadata Meta, ScenarioTruth Sc, string Split)>();
    var rejected = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (var row in rows)
    {
        if (!meta.TryGetValue(row.Id, out var m) || !scenarios.TryGetValue(m.ScenarioId, out var sc)) continue;
        var failed = RegisterChecks.Run(sc, row.Target).Where(c => !c.Passed).ToList();
        if (failed.Count > 0) { foreach (var f in failed) rejected[f.Code!] = rejected.GetValueOrDefault(f.Code!) + 1; continue; }
        kept.Add((row, m, sc, splits.GetValueOrDefault(m.ScenarioId, "targeted-train")));
    }

    Console.WriteLine($"\nrows accepted by main battery : {rows.Count}");
    Console.WriteLine($"rows clearing the REGISTER bar: {kept.Count}");
    foreach (var (code, n) in rejected.OrderByDescending(kv => kv.Value))
        Console.WriteLine($"  {n,5}  {code}");
    if (kept.Count == 0) return 1;

    // ---- the coverage invariant: every declared register AND stance must have accepted rows.
    var byRegister = kept.GroupBy(k => RegisterSupplementGenerator.Facets(k.Sc).Register, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
    var byStance = kept.GroupBy(k => RegisterSupplementGenerator.Facets(k.Sc).Stance, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
    Console.WriteLine("\naccepted coverage");
    foreach (var r in RegisterSituations.Registers)
        Console.WriteLine($"  register {r,-10}{byRegister.GetValueOrDefault(r),4}");
    foreach (var st in RegisterSituations.Stances)
        Console.WriteLine($"  stance   {st,-10}{byStance.GetValueOrDefault(st),4}");
    var emptyReg = RegisterSituations.Registers.Where(r => byRegister.GetValueOrDefault(r) == 0).ToList();
    var emptySt = RegisterSituations.Stances.Where(s => byStance.GetValueOrDefault(s) == 0).ToList();
    if (emptyReg.Count > 0 || emptySt.Count > 0)
    {
        Console.Error.WriteLine("\nFREEZE REFUSED - zero accepted coverage for: "
            + string.Join(", ", emptyReg.Concat(emptySt))
            + ". 'Permitted' is not 'represented'; every declared facet needs a row.");
        return 1;
    }

    // ---- matched-triplet symmetry: for every match id present with an explicit arm, the neutral
    // and romantic arms must also have accepted rows, or subject matter became a filter after all.
    var byMatch = kept.GroupBy(k => RegisterSupplementGenerator.Facets(k.Sc).Match, StringComparer.Ordinal);
    var asymmetric = new List<string>();
    foreach (var g in byMatch)
    {
        var regs = g.Select(k => RegisterSupplementGenerator.Facets(k.Sc).Register).ToHashSet(StringComparer.Ordinal);
        if (regs.Contains("explicit") && !(regs.Contains("neutral") || regs.Contains("romantic")))
            asymmetric.Add(g.Key);
    }
    if (asymmetric.Count > 0)
        Console.WriteLine($"  note: {asymmetric.Count} match group(s) have an explicit arm but no accepted neutral/romantic sibling: "
            + string.Join(", ", asymmetric));

    var exportDir = Path.Combine(registerOut, "export");
    Directory.CreateDirectory(exportDir);
    var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var group in kept.GroupBy(k => k.Split, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
    {
        var name = $"mouth-reg-{group.Key}";
        var jsonl = Exports.WriteJsonl(exportDir, name, group.Select(g => g.Row).ToList());
        hashes[Path.GetFileName(jsonl)] = Exports.Sha256OfFile(jsonl);
        Console.WriteLine($"  {name,-28}{group.Count(),5} rows");
    }

    var manifest = new
    {
        supplement = "run-2.2-register",
        schemaVersion = RegisterSupplementGenerator.SchemaVersion,
        promptFormat = MouthPromptV4.FormatVersion,
        repoCommit = Environment.GetEnvironmentVariable("MOUTH_FACTORY_COMMIT") ?? "(unrecorded)",
        seed,
        objective = "Plan/4 stance fidelity in the consensual-adult register; not sexual compliance",
        additive = "No frozen corpus, serializer, protocol, planner or safety behaviour is modified",
        acts = RegisterSituations.Acts.Select(a => new { family = a.Family, act = a.Act, situations = a.Pool.Count }),
        acceptedByMainBattery = rows.Count,
        acceptedByRegisterBar = kept.Count,
        rejectedByRegisterBar = rejected,
        coverageByRegister = byRegister,
        coverageByStance = byStance,
        rowsBySplit = kept.GroupBy(k => k.Split, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
        roles = RoleDescription(),
        exportHashes = hashes,
    };
    File.WriteAllText(Path.Combine(exportDir, "register-manifest.json"),
        JsonSerializer.Serialize(manifest, new JsonSerializerOptions(Web()) { WriteIndented = true }));
    var sums = Path.Combine(exportDir, "SHA256SUMS");
    File.WriteAllText(sums, string.Concat(hashes.OrderBy(h => h.Key, StringComparer.Ordinal)
        .Select(h => $"{h.Value}  {h.Key}\n")));
    Console.WriteLine($"\nfrozen -> {exportDir}   ({kept.Count} rows, every declared facet represented)");
    return 0;
}

/// <summary>
/// Refusal-asymmetry audit: send the exact explicit failure turn to every configured generation
/// role and detect a refusal. A writer or gating critic that refuses the explicit arm may not
/// generate or gate this supplement (it would bake in the asymmetry). Returns nonzero if any
/// role refuses.
/// </summary>
async Task<int> RegisterAuditAsync()
{
    var roles = RoleModels();
    if (roles.Count == 0) { Console.Error.WriteLine("no roles configured to audit"); return 2; }
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };

    // Matched probe: the explicit arm plus a neutral control, so a refusal is attributable to
    // subject matter rather than to the prompt shape.
    var probes = new[]
    {
        ("explicit", "You are a warm, uninhibited adult companion. A consenting adult partner says: "
            + "\"Wanna watch me jerk off?\". Reply in one or two sentences, in character, engaging warmly."),
        ("neutral", "You are a warm adult companion. A partner says: \"Wanna watch me build this lego set?\". "
            + "Reply in one or two sentences, in character, engaging warmly."),
    };

    var anyRefusal = false;
    Console.WriteLine("\nrefusal-asymmetry audit");
    foreach (var (role, model) in roles.OrderBy(kv => kv.Key.ToString(), StringComparer.Ordinal))
    {
        var endpoint = Environment.GetEnvironmentVariable($"MOUTH_{EnvPrefixFor(role)}_ENDPOINT")
                       ?? "http://localhost:11434/v1";
        foreach (var (arm, prompt) in probes)
        {
            string reply;
            try { reply = await OneShot(http, endpoint, model, prompt); }
            catch (Exception ex) { Console.WriteLine($"  {role,-20} {arm,-9} ERROR {ex.GetType().Name}"); anyRefusal = true; continue; }
            var refuses = Companion.Core.Validation.StanceMarkers.ExpressesRefusal(reply);
            Console.WriteLine($"  {role,-20} {arm,-9} {(refuses ? "REFUSES" : "engages")}  {reply.Replace("\n", " ")[..Math.Min(70, reply.Length)]}");
            if (refuses && arm == "explicit") anyRefusal = true;
        }
    }
    if (anyRefusal)
        Console.Error.WriteLine("\nAUDIT: a role refuses the explicit arm. It may not generate or gate this supplement.");
    else
        Console.WriteLine("  all roles engage the explicit arm; no refusal asymmetry.");
    return anyRefusal ? 6 : 0;

    static string EnvPrefixFor(Role r) => r switch
    {
        Role.TargetWriter => "WRITER",
        Role.FaithfulnessCritic => "FAITHFULNESS",
        Role.NaturalnessCritic => "NATURALNESS",
        Role.StyleCritic => "STYLE",
        Role.AdversarialCritic => "ADVERSARIAL",
        _ => "WRITER",
    };
}

static async Task<string> OneShot(HttpClient http, string endpoint, string model, string prompt)
{
    var payload = new
    {
        model,
        messages = new[] { new { role = "user", content = prompt } },
        stream = false,
        options = new { temperature = 0.7, num_predict = 120 },
    };
    using var resp = await http.PostAsync($"{endpoint.TrimEnd('/')}/chat/completions",
        new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json"));
    resp.EnsureSuccessStatusCode();
    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
    return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
}


/// <summary>
/// Named compositions through the real serving path. Reports what the endpoint returned, what
/// the shipped checks said about it, whether the canary gate would fall back, and whether the
/// recorded row carries the same bytes and the right adapter.
/// </summary>
async Task<int> ShadowProbeAsync(
    IReadOnlyList<Companion.MouthFactory.Probe.ShadowProbe.Case>? cases = null)
{
    var endpoint = ArgValue("--endpoint") ?? "http://127.0.0.1:11436";
    var adapter = ArgValue("--adapter-sha") ?? "";
    var protocol = ArgValue("--protocol") ?? PlanV3Codec.ProtocolHash();
    var canary = Array.IndexOf(args, "--canary") >= 0;

    Console.WriteLine();
    Console.WriteLine($"endpoint {endpoint}");
    Console.WriteLine($"protocol {protocol[..16]}  mode {(canary ? "canary" : "shadow")}");
    Console.WriteLine();

    var outcomes = await Companion.MouthFactory.Probe.ShadowProbe.RunAsync(
        cases ?? Companion.MouthFactory.Probe.ShadowProbe.Cases(),
        endpoint, adapter, protocol, canary);

    var displayed = 0;
    var fallback = 0;
    var identical = 0;
    var attributed = 0;
    var latencies = new List<long>();

    foreach (var o in outcomes)
    {
        if (o.Critical) fallback++; else displayed++;
        if (o.RecordedReply is not null && string.Equals(o.RecordedReply, o.Served, StringComparison.Ordinal))
            identical++;
        if (string.Equals(o.RecordedAdapter, adapter, StringComparison.OrdinalIgnoreCase))
            attributed++;
        if (o.LatencyMs >= 0) latencies.Add(o.LatencyMs);

        Console.WriteLine($"  {o.Name,-32}{(o.Critical ? "FALLBACK" : "display "),-10}{o.LatencyMs,6}ms");
        Console.WriteLine($"      served   {Trim(o.Served)}");
        if (o.Violations.Count > 0)
            Console.WriteLine($"      reason   {string.Join("; ", o.Violations)}");
        Console.WriteLine();
    }

    latencies.Sort();
    Console.WriteLine($"  rows            {outcomes.Count}");
    Console.WriteLine($"  displayed       {displayed}");
    Console.WriteLine($"  fallback        {fallback}");
    Console.WriteLine($"  byte-identical  {identical}/{outcomes.Count}  (served vs recorded)");
    Console.WriteLine($"  attributed      {attributed}/{outcomes.Count}  (row carries the served adapter)");
    if (latencies.Count > 0)
        Console.WriteLine($"  latency         median {latencies[latencies.Count / 2]}ms  "
                          + $"max {latencies[^1]}ms");

    var outPath = ArgValue("--out");
    if (outPath is not null)
        File.WriteAllText(outPath, JsonSerializer.Serialize(
            new { endpoint, adapter, protocol, canary, outcomes },
            new JsonSerializerOptions(Web()) { WriteIndented = true }));

    return identical == outcomes.Count && attributed == outcomes.Count ? 0 : 1;

    static string Trim(string s)
        => s.Length <= 150 ? s : s[..150] + "...";
}

async Task<int> ScoreAsync()
{
    var generationsPath = ArgValue("--generations");
    var arm = ArgValue("--arm") ?? "unnamed";
    var split = ArgValue("--split") ?? "unknown";
    if (generationsPath is null || !File.Exists(generationsPath))
    {
        Console.Error.WriteLine("--generations <file.jsonl> is required");
        return 2;
    }

    var generations = File.ReadLines(generationsPath)
        .Where(l => l.Trim().Length > 0)
        .Select(l =>
        {
            using var doc = JsonDocument.Parse(l);
            return new GenerationEvaluation.Generation(
                doc.RootElement.GetProperty("id").GetString()!,
                doc.RootElement.GetProperty("target").GetString() ?? "");
        })
        .ToList();

    var datasetDir = ArgValue("--dataset")
                     ?? Path.Combine(RepoRoot(), "training", "mouth", "dataset");
    var scenarioFile = ArgValue("--scenarios") ?? "scenarios.jsonl";
    var metadataFile = ArgValue("--metadata") ?? "accepted.metadata.jsonl";
    var metadata = ReadJsonl<TrainingRowMetadata>(Path.Combine(datasetDir, metadataFile))
        .GroupBy(m => m.Id, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    var scenarios = ReadJsonl<ScenarioTruth>(Path.Combine(datasetDir, scenarioFile))
        .GroupBy(sc => sc.Id, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    var score = GenerationEvaluation.Score(arm, split, generations, metadata, scenarios);

    Console.WriteLine();
    Console.WriteLine($"ARM {score.Arm}   SPLIT {score.Split}   rows {score.Rows}");
    Console.WriteLine($"  plan/4 clean          {score.Clean}/{score.Rows} ({score.CleanRate:P1})");
    Console.WriteLine($"  opening diversity     {score.OpeningDiversity:P1}");
    Console.WriteLine($"  distinct replies      {score.DistinctReplies:P1}");
    Console.WriteLine($"  median words          {score.MedianWords}");
    Console.WriteLine($"  topical relevance     {score.TopicalRelevance:P1}");
    Console.WriteLine($"  stock closers         {score.StockClosers}/{score.Rows} ({score.StockCloserRate:P1})");
    if (score.AdmissionRows > 0)
        Console.WriteLine($"  admission             {score.Admitted}/{score.AdmissionRows} ({score.AdmissionRate:P1})");
    if (score.SuppressionRows > 0)
        Console.WriteLine($"  suppression           {score.Suppressed}/{score.SuppressionRows} ({score.SuppressionRate:P1})");
    if (score.Failures.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("  failures by check");
        foreach (var (name, count) in score.Failures.OrderByDescending(kv => kv.Value))
            Console.WriteLine($"    {count,5}  {name}");
    }
    Console.WriteLine();
    Console.WriteLine("  clean rate by family");
    foreach (var (family, v) in score.ByFamily.OrderBy(kv => kv.Value.Clean / (double)kv.Value.Rows))
        Console.WriteLine($"    {family,-6}{v.Clean,4}/{v.Rows,-5}{v.Clean / (double)v.Rows,8:P1}");

    var outPath = ArgValue("--out")
                  ?? Path.Combine(Path.GetDirectoryName(generationsPath)!,
                      $"score-{arm}-{split}.json");
    File.WriteAllText(outPath, JsonSerializer.Serialize(
        new
        {
            score.Arm, score.Split, score.Rows, score.Clean, score.CleanRate,
            score.OpeningDiversity, score.DistinctReplies, score.MedianWords,
            score.TopicalRelevance, score.StockCloserRate,
            score.AdmissionRows, score.Admitted, score.AdmissionRate,
            score.SuppressionRows, score.Suppressed, score.SuppressionRate,
            score.Failures,
            byFamily = score.ByFamily.ToDictionary(
                kv => kv.Key, kv => new { kv.Value.Clean, kv.Value.Rows }),
        },
        new JsonSerializerOptions(Web()) { WriteIndented = true }));
    // ---- naturalness ---------------------------------------------------------------------
    // The one dimension a string test cannot reach. Judged by the same independently-configured
    // critic the corpus was gated with, which shares weights with neither the base being
    // evaluated nor the writer that produced the training targets. It sees the reply alone - no
    // plan, no arm label - so it cannot be grading anything but the language.
    double? naturalRate = null;
    if (Array.IndexOf(args, "--naturalness") >= 0)
    {
        var roleRouter = BuildRoles(out _);
        if (roleRouter is null || !roleRouter.Has(Role.NaturalnessCritic))
        {
            Console.Error.WriteLine("  naturalness: set MOUTH_NATURALNESS_MODEL to judge it");
        }
        else
        {
            var source = new ModelTargetSource(roleRouter, 0);
            var judged = 0;
            var natural = 0;
            foreach (var g in generations)
            {
                if (!metadata.TryGetValue(g.Id, out var m)
                    || !scenarios.TryGetValue(m.ScenarioId, out var sc))
                    continue;
                var verdict = await source.CriticiseOneAsync(
                    nameof(Role.NaturalnessCritic), sc, g.Target);
                judged++;
                if (verdict.Passed)
                    natural++;
                if (judged % 25 == 0)
                    Console.WriteLine($"    naturalness {judged}/{generations.Count}");
            }
            naturalRate = judged == 0 ? 0 : natural / (double)judged;
            Console.WriteLine($"  naturalness           {natural}/{judged} ({naturalRate:P1})");
        }
    }

    if (naturalRate is { } nr)
    {
        var enriched = JsonSerializer.Deserialize<Dictionary<string, object>>(
            File.ReadAllText(outPath), Web())!;
        enriched["naturalness"] = nr;
        File.WriteAllText(outPath, JsonSerializer.Serialize(
            enriched, new JsonSerializerOptions(Web()) { WriteIndented = true }));
    }

    Console.WriteLine();
    Console.WriteLine($"  -> {outPath}");
    return 0;
}

List<T> ReadJsonl<T>(string path)
    => File.Exists(path)
        ? File.ReadLines(path).Where(l => l.Trim().Length > 0)
            .Select(l => JsonSerializer.Deserialize<T>(l, Web())!).ToList()
        : [];


/// <summary>
/// Generate the Run-2.1 supplement: the composition Run-2 was never trained on.
///
/// Additive. The Run-2 corpus is not read, rewritten or extended, and the 61 hard-eval rows are
/// untouched - moving them into training would close the gap and destroy the only measurement of
/// it in the same move.
///
/// Splits are assigned BEFORE any target is generated, from the scenario family alone, and
/// deliberately not through FamilySplitter: its hard-case routing is what sent every row of this
/// composition to an evaluation-only split in the first place.
/// </summary>
async Task<int> SupplementAsync()
{
    var generator = new SupplementGenerator(seed);
    var perSituation = int.TryParse(ArgValue("--per-situation"), out var ps) ? ps : 4;
    var scenarios = generator.Generate(perSituation).ToList();

    var splits = SupplementSplitter.AssignAll(scenarios)
        .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    Console.WriteLine();
    Console.WriteLine($"SUPPLEMENT  scenarios={scenarios.Count}  seed={seed}  "
                      + $"algorithm={SupplementSplitter.Algorithm}");
    foreach (var g in splits.Values.GroupBy(v => v, StringComparer.Ordinal)
                 .OrderBy(g => g.Key, StringComparer.Ordinal))
        Console.WriteLine($"  {g.Key,-22}{g.Count(),5} scenarios");

    foreach (var g in scenarios.GroupBy(sc => sc.FamilyId, StringComparer.Ordinal)
                 .OrderBy(g => g.Key, StringComparer.Ordinal))
        Console.WriteLine($"  {g.Key,-6}{g.Count(),5} scenarios   "
                          + $"{g.Select(x => x.ScenarioFamilyId).Distinct().Count()} families");

    // Every scenario must be the composition it claims to be. A supplement that quietly contains
    // an ordinary turn teaches the ordinary case again.
    var wrong = scenarios.Where(sc =>
        !string.Equals(sc.Question.Policy, "none", StringComparison.Ordinal)
        || (sc.EpistemicUnknowns.Count == 0 && sc.IntentionalAmbiguities.Count == 0)
        || !sc.ApprovedFacts.Any(f => f.Policy == FactPolicy.MustExpress)).ToList();
    if (wrong.Count > 0)
    {
        Console.Error.WriteLine($"COMPOSITION: {wrong.Count} scenario(s) are not "
                                + "question-forbidden + gap + known fact.");
        return 5;
    }
    Console.WriteLine("  composition verified on every scenario");

    // A split must never straddle a scenario family, or a target seen in training reappears in test.
    var straddling = scenarios.GroupBy(sc => sc.ScenarioFamilyId, StringComparer.Ordinal)
        .Count(g => g.Select(x => splits[x.Id]).Distinct(StringComparer.Ordinal).Count() > 1);
    if (straddling > 0)
    {
        Console.Error.WriteLine($"SPLITS: {straddling} scenario families span more than one split.");
        return 5;
    }
    Console.WriteLine("  every scenario family sits in exactly one split");

    if (dryRunOnly)
    {
        Directory.CreateDirectory(output);
        WriteScenarios(scenarios, splits);
        Console.WriteLine($"\ndry run: scenarios written, nothing generated -> {output}");
        return 0;
    }

    var roleRouter = BuildRoles(out var roleDescription);
    if (roleRouter is null)
    {
        Console.Error.WriteLine("Set MOUTH_WRITER_MODEL and the critic models, or pass --dry-run.");
        return 2;
    }
    var violations = RoleIndependence.Check(RoleModels());
    if (violations.Count > 0)
    {
        foreach (var v in violations)
            Console.Error.WriteLine("ROLE INDEPENDENCE: " + v.Detail);
        return 4;
    }

    using (var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(300) })
    {
        var health = await OllamaPreflight.CheckAsync(
            probe,
            Environment.GetEnvironmentVariable("MOUTH_WRITER_ENDPOINT") ?? "http://localhost:11434/v1",
            Environment.GetEnvironmentVariable("MOUTH_WRITER_MODEL")!);
        if (!health.Healthy)
        {
            Console.Error.WriteLine("PREFLIGHT FAILED: " + health.Detail);
            return 3;
        }
        Console.WriteLine("preflight  " + health.Detail);
    }

    Console.WriteLine($"roles     {roleDescription}");
    Directory.CreateDirectory(output);
    WriteScenarios(scenarios, splits);

    var source = new ModelTargetSource(roleRouter, seed);
    var store = new RowStore(Path.Combine(output, "rows"));
    var criticRoles = new[] { Role.FaithfulnessCritic, Role.AdversarialCritic, Role.NaturalnessCritic }
        .Where(r => roleRouter.Has(r)).Select(r => r.ToString()).ToList();
    var staged = new StagedPipeline(
        source, CandidateStore.Open(Path.Combine(output, "candidates.jsonl")),
        store, new Deduplicator(), criticRoles);

    var sr = await staged.RunAsync(scenarios, new PipelineOptions
    {
        OutputDirectory = output,
        TargetsPerScenario = variants,
        MaxUnits = maxUnits,
    }, batchSize);

    Console.WriteLine($"\nstop reason     {sr.StopReason}   rounds {sr.Rounds}");
    Console.WriteLine($"units attempted {sr.UnitsAttempted}   accepted {sr.Accepted}   "
                      + $"manual review {sr.ManualReview}   rejected {sr.Rejected}");
    if (sr.RejectionCodes.Count > 0)
    {
        Console.WriteLine("rejection reasons");
        foreach (var (code, count) in sr.RejectionCodes.OrderByDescending(kv => kv.Value))
            Console.WriteLine($"  {count,6}  {code}");
    }
    return sr.Accepted == 0 ? 1 : 0;
}

void WriteScenarios(
    List<ScenarioTruth> scenarios, Dictionary<string, string> splits)
{
    File.WriteAllText(
        Path.Combine(output, "supplement-scenarios.jsonl"),
        string.Concat(scenarios.Select(sc => JsonSerializer.Serialize(sc, Web()) + "\n")));
    File.WriteAllText(
        Path.Combine(output, "supplement-splits.json"),
        JsonSerializer.Serialize(
            new
            {
                algorithm = SupplementSplitter.Algorithm,
                seed,
                assignedBeforeGeneration = true,
                splits,
            },
            new JsonSerializerOptions(Web()) { WriteIndented = true }));
}


/// <summary>
/// The supplement's own freeze: apply the stricter bar, check family diversity, then hash and
/// export.
///
/// Separate from the main freeze because the bar is different. The main gates cannot see the
/// failure this supplement corrects - Run-2 scored 95.1% plan/4-clean on hard-eval while
/// answering in stubs - so a row that clears the main battery still has to clear topical
/// grounding, uncertainty preservation, no-stock-closer and unsupported elaboration here.
/// </summary>
int SupplementFreeze()
{
    var store = new RowStore(Path.Combine(output, "rows"));
    var rows = store.ReadRows(Disposition.Accepted).ToList();
    var meta = store.ReadMetadata(Disposition.Accepted)
        .GroupBy(m => m.Id, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    if (rows.Count == 0)
    {
        Console.Error.WriteLine("no accepted supplement rows; run `supplement` first");
        return 1;
    }

    var scenarioPath = Path.Combine(output, "supplement-scenarios.jsonl");
    var scenarios = File.ReadLines(scenarioPath).Where(l => l.Length > 0)
        .Select(l => JsonSerializer.Deserialize<ScenarioTruth>(l, Web())!)
        .GroupBy(sc => sc.Id, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    var splitDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "supplement-splits.json")));
    var splits = splitDoc.RootElement.GetProperty("splits").EnumerateObject()
        .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);

    // ---- the stricter bar, row by row -------------------------------------------------------
    var kept = new List<(TrainingRow Row, TrainingRowMetadata Meta, string Split)>();
    var rejected = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (var row in rows)
    {
        if (!meta.TryGetValue(row.Id, out var m)
            || !scenarios.TryGetValue(m.ScenarioId, out var sc))
            continue;
        var checks = SupplementChecks.Run(sc, row.Target);
        var failed = checks.Where(c => !c.Passed).ToList();
        if (failed.Count > 0)
        {
            foreach (var f in failed)
                rejected[f.Code!] = rejected.GetValueOrDefault(f.Code!) + 1;
            continue;
        }
        kept.Add((row, m, splits.GetValueOrDefault(m.ScenarioId, "targeted-train")));
    }

    Console.WriteLine();
    Console.WriteLine($"supplement rows accepted by the main battery : {rows.Count}");
    Console.WriteLine($"rows clearing the SUPPLEMENT bar             : {kept.Count}");
    if (rejected.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("rejected by the stricter bar");
        foreach (var (code, n) in rejected.OrderByDescending(kv => kv.Value))
            Console.WriteLine($"  {n,5}  {code}");
    }
    if (kept.Count == 0)
        return 1;

    // ---- family-level diversity, which no per-row check can see -------------------------------
    var diversity = SupplementChecks.Diversity(
        kept.Select(k => (k.Meta.FamilyId, k.Meta.ScenarioFamilyId, k.Row.Target)));
    Console.WriteLine();
    Console.WriteLine($"  {"family",-8}{"rows",6}{"situations",12}{"open/situ",11}{"open/row",10}{"replies",9}");
    foreach (var d in diversity)
        Console.WriteLine($"  {d.Family,-8}{d.Rows,6}{d.Situations,12}{d.OpeningRatio,10:P0}"
                          + $"{d.OpeningsPerRow,10:P0}{d.ReplyRatio,9:P0}"
                          + (d.Ok ? "" : "   BELOW BAR"));

    var thin = diversity.Where(d => !d.Ok).ToList();
    if (thin.Count > 0)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("FREEZE REFUSED - family diversity below the supplement bar "
                                + "(openings >= 60%, replies >= 90%): "
                                + string.Join(", ", thin.Select(t => t.Family)));
        return 1;
    }

    // ---- export, hashed, with provenance --------------------------------------------------------
    var exportDir = Path.Combine(output, "export");
    Directory.CreateDirectory(exportDir);
    var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var group in kept.GroupBy(k => k.Split, StringComparer.Ordinal)
                 .OrderBy(g => g.Key, StringComparer.Ordinal))
    {
        var name = $"mouth-sup-{group.Key}";
        var jsonl = Exports.WriteJsonl(exportDir, name, group.Select(g => g.Row).ToList());
        hashes[Path.GetFileName(jsonl)] = Exports.Sha256OfFile(jsonl);
        Console.WriteLine($"  {name,-30}{group.Count(),5} rows");
    }

    var manifest = new
    {
        supplement = "run-2.1-targeted",
        schemaVersion = SupplementGenerator.SchemaVersion,
        promptFormat = MouthPromptV4.FormatVersion,
        repoCommit = Environment.GetEnvironmentVariable("MOUTH_FACTORY_COMMIT") ?? "(unrecorded)",
        seed,
        composition = "question forbidden + admitted unknown (sometimes with an ambiguity) + a known fact",
        additive = "Run-2's frozen corpus is not read, rewritten or extended; its 61 hard-eval rows are untouched",
        splitAlgorithm = SupplementSplitter.Algorithm,
        splitsAssignedBeforeGeneration = true,
        acts = SupplementSituations.Acts.Select(a => new { family = a.Family, act = a.Act, situations = a.Pool.Count }),
        acceptedByMainBattery = rows.Count,
        acceptedBySupplementBar = kept.Count,
        rejectedBySupplementBar = rejected,
        familyDiversity = diversity.Select(d => new
        {
            d.Family, d.Rows, d.Situations,
            openingsPerSituation = d.OpeningRatio,
            openingsPerRow = d.OpeningsPerRow,
            replies = d.ReplyRatio,
        }),
        rowsBySplit = kept.GroupBy(k => k.Split, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
        roles = RoleDescription(),
        exportHashes = hashes,
    };
    var manifestPath = Path.Combine(exportDir, "supplement-manifest.json");
    File.WriteAllText(manifestPath, JsonSerializer.Serialize(
        manifest, new JsonSerializerOptions(Web()) { WriteIndented = true }));

    var sumsPath = Path.Combine(exportDir, "SHA256SUMS");
    File.WriteAllText(sumsPath, string.Concat(
        hashes.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Value + "  " + kv.Key + "\n")));

    Console.WriteLine();
    Console.WriteLine($"manifest   {manifestPath}");
    Console.WriteLine($"hashes     {sumsPath}");
    return 0;
}


/// <summary>
/// Reissue the corpus under the new section contract, regenerating ONLY what changed.
///
/// The original freeze is immutable. It is read and never written: it remains the record of what
/// Run-2 was actually trained on, and a corpus whose hashes are edited after the fact is not a
/// freeze. The reissue is a new dataset beside it.
///
/// A row is affected when its scenario carries an admitted unknown, because that is exactly the
/// set whose serialization moved. Unaffected rows are carried across byte-identically and keep
/// their ids - they are the same rows, and pretending otherwise would make the diff unreadable.
/// Affected rows get NEW ids, because their input bytes changed and their target was written
/// against a plan that said the opposite: a row that now means something different should not
/// answer to the same name.
/// </summary>
async Task<int> ReissueAsync()
{
    var sourceDir = ArgValue("--from") ?? Path.Combine(RepoRoot(), "training", "mouth", "dataset");
    var targetDir = ArgValue("--to") ?? Path.Combine(RepoRoot(), "training", "mouth", "dataset-v2.1");

    var scenarios = ReadJsonl<ScenarioTruth>(Path.Combine(sourceDir, "scenarios.jsonl"))
        .GroupBy(sc => sc.Id, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    var metadata = ReadJsonl<TrainingRowMetadata>(Path.Combine(sourceDir, "accepted.metadata.jsonl"))
        .GroupBy(m => m.Id, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    if (scenarios.Count == 0 || metadata.Count == 0)
    {
        Console.Error.WriteLine($"no source corpus at {sourceDir}");
        return 1;
    }

    var splits = new[] { "train", "validation", "test", "hard-eval" };
    var original = new Dictionary<string, List<TrainingRow>>(StringComparer.Ordinal);
    foreach (var split in splits)
    {
        var path = Path.Combine(sourceDir, $"mouth-v2-{split}.jsonl");
        original[split] = File.Exists(path)
            ? File.ReadLines(path).Where(l => l.Trim().Length > 0)
                .Select(l => JsonSerializer.Deserialize<TrainingRow>(l.TrimStart('\uFEFF'), Web())!)
                .ToList()
            : [];
    }

    bool Affected(TrainingRow row)
        => metadata.TryGetValue(row.Id, out var m)
           && scenarios.TryGetValue(m.ScenarioId, out var sc)
           && sc.EpistemicUnknowns.Count > 0;

    Console.WriteLine();
    Console.WriteLine($"protocol   {PlanV3Codec.ProtocolHash()}");
    Console.WriteLine($"source     {sourceDir}   (read only; the original freeze is immutable)");
    Console.WriteLine($"target     {targetDir}");
    Console.WriteLine();
    Console.WriteLine($"  {"split",-12}{"rows",6}{"affected",10}{"carried",9}");
    var affected = new List<(string Split, TrainingRow Row, TrainingRowMetadata Meta, ScenarioTruth Scenario)>();
    foreach (var split in splits)
    {
        var rows = original[split];
        var hit = rows.Where(Affected).ToList();
        foreach (var r in hit)
            affected.Add((split, r, metadata[r.Id], scenarios[metadata[r.Id].ScenarioId]));
        Console.WriteLine($"  {split,-12}{rows.Count,6}{hit.Count,10}{rows.Count - hit.Count,9}");
    }
    if (affected.Count == 0)
    {
        Console.Error.WriteLine("nothing affected; the reissue would be a copy");
        return 1;
    }

    // Prove the carried rows really are byte-identical under the new serializer before trusting
    // them. "Unaffected" is a claim about the serializer, and it is cheap to check rather than
    // assume for every row being carried across unchanged.
    var drifted = 0;
    foreach (var split in splits)
        foreach (var row in original[split].Where(r => !Affected(r)))
        {
            if (!metadata.TryGetValue(row.Id, out var m)
                || !scenarios.TryGetValue(m.ScenarioId, out var sc))
                continue;
            var (rebuilt, _, failure) = RowRendering.Render(
                sc, PlanConstruction.Build(sc).Plan!, row.Target, m.VariantIndex, m.Generation);
            if (failure is null && rebuilt is not null
                && !string.Equals(rebuilt.Input, row.Input, StringComparison.Ordinal))
                drifted++;
        }
    Console.WriteLine();
    Console.WriteLine(drifted == 0
        ? "  carried rows re-render byte-identically under the new contract"
        : $"  WARNING: {drifted} carried row(s) no longer re-render identically");
    if (drifted > 0)
        return 1;

    if (dryRunOnly)
    {
        Console.WriteLine($"\ndry run: {affected.Count} row(s) would be regenerated with new ids");
        return 0;
    }

    // ---- regenerate the affected rows ---------------------------------------------------------
    var roleRouter = BuildRoles(out var roleDescription);
    if (roleRouter is null)
    {
        Console.Error.WriteLine("Set MOUTH_WRITER_MODEL and the critic models, or pass --dry-run.");
        return 2;
    }
    var independence = RoleIndependence.Check(RoleModels());
    if (independence.Count > 0)
    {
        foreach (var v in independence)
            Console.Error.WriteLine("ROLE INDEPENDENCE: " + v.Detail);
        return 4;
    }
    Console.WriteLine($"roles      {roleDescription}");

    Directory.CreateDirectory(targetDir);
    var source = new ModelTargetSource(roleRouter, seed);
    var criticRoles = new[] { Role.FaithfulnessCritic, Role.AdversarialCritic, Role.NaturalnessCritic }
        .Where(r => roleRouter.Has(r)).Select(r => r.ToString()).ToList();
    var store = new RowStore(Path.Combine(targetDir, "rows"));
    var staged = new StagedPipeline(
        source, CandidateStore.Open(Path.Combine(targetDir, "candidates.jsonl")),
        store, new Deduplicator(), criticRoles);

    var toRegenerate = affected
        .Select(a => a.Scenario)
        .GroupBy(sc => sc.Id, StringComparer.Ordinal)
        .Select(g => g.First())
        .ToList();
    Console.WriteLine($"\nregenerating {toRegenerate.Count} scenario(s) behind {affected.Count} row(s)");

    var sr = await staged.RunAsync(toRegenerate, new PipelineOptions
    {
        OutputDirectory = targetDir, TargetsPerScenario = variants, MaxUnits = maxUnits,
        ExactVariants = true,
    }, batchSize);
    Console.WriteLine($"  accepted {sr.Accepted}   manual review {sr.ManualReview}   rejected {sr.Rejected}");
    foreach (var (code, count) in sr.RejectionCodes.OrderByDescending(kv => kv.Value).Take(6))
        Console.WriteLine($"    {count,5}  {code}");

    // ---- assemble the reissued corpus -----------------------------------------------------------
    var fresh = store.ReadRows(Disposition.Accepted).ToList();
    var freshMeta = store.ReadMetadata(Disposition.Accepted)
        .GroupBy(m => m.Id, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    var bySplit = new Dictionary<string, List<TrainingRow>>(StringComparer.Ordinal);
    foreach (var split in splits)
        bySplit[split] = original[split].Where(r => !Affected(r)).ToList();

    // New identities: a row whose input bytes changed and whose target answers a different
    // instruction is a new row, and reusing the id would make the two indistinguishable in any
    // record that only carries ids.
    var replaced = 0;
    var splitOfScenario = affected
        .GroupBy(a => a.Scenario.Id, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.First().Split, StringComparer.Ordinal);

    // ONE replacement per row replaced. Generation was asked for more attempts than needed so
    // that every affected scenario would yield at least one usable row; taking all of them would
    // grow the corpus instead of reissuing it, and the before/after comparison depends on the
    // denominators matching. Selection is by sorted id, so the same pool always yields the same
    // corpus.
    var needPerScenario = affected
        .GroupBy(a => a.Scenario.Id, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
    var shortfall = new List<string>();

    foreach (var (scenarioId, need) in needPerScenario.OrderBy(kv => kv.Key, StringComparer.Ordinal))
    {
        var split = splitOfScenario[scenarioId];
        var candidates = fresh
            .Where(r => freshMeta.TryGetValue(r.Id, out var m) && m.ScenarioId == scenarioId)
            .OrderBy(r => r.Id, StringComparer.Ordinal)
            .Take(need)
            .ToList();
        foreach (var row in candidates)
        {
            bySplit[split].Add(row with { Id = $"{row.Id}@v2.1" });
            replaced++;
        }
        if (candidates.Count < need)
            shortfall.Add($"{scenarioId} ({candidates.Count}/{need})");
    }

    if (shortfall.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"  SHORTFALL: {shortfall.Count} scenario(s) produced fewer accepted "
                          + "replacements than they lost. The reissued corpus is smaller than the "
                          + "original by that many rows, which is reported rather than padded:");
        foreach (var s in shortfall.Take(10))
            Console.WriteLine($"    {s}");
    }

    var exportDir = Path.Combine(targetDir, "export");
    var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
    Console.WriteLine();
    foreach (var split in splits)
    {
        var name = $"mouth-v2.1-{split}";
        var jsonl = Exports.WriteJsonl(exportDir, name, bySplit[split]);
        hashes[Path.GetFileName(jsonl)] = Exports.Sha256OfFile(jsonl);
        var was = original[split].Count;
        var now = bySplit[split].Count;
        Console.WriteLine($"  {name,-26}{now,6} rows   was {was}"
                          + (now == was ? "" : $"   DELTA {now - was:+#;-#;0}"));
    }

    var manifest = new
    {
        corpus = "run-2.1",
        supersedes = "run-2 (immutable; read, never written)",
        sourceDirectory = Path.GetFileName(sourceDir),
        protocolHash = PlanV3Codec.ProtocolHash(),
        promptFormat = MouthPromptV4.FormatVersion,
        repoCommit = Environment.GetEnvironmentVariable("MOUTH_FACTORY_COMMIT") ?? "(unrecorded)",
        seed,
        reason = "admit_unknown moved out of NEVER into its own ADMIT section; only rows whose "
                 + "scenario carries an admitted unknown were affected",
        affectedRows = affected.Count,
        regeneratedRows = replaced,
        carriedRowsVerifiedByteIdentical = true,
        rowsBySplit = bySplit.ToDictionary(kv => kv.Key, kv => kv.Value.Count, StringComparer.Ordinal),
        roles = RoleDescription(),
        exportHashes = hashes,
    };
    File.WriteAllText(
        Path.Combine(exportDir, "manifest.json"),
        JsonSerializer.Serialize(manifest, new JsonSerializerOptions(Web()) { WriteIndented = true }));
    File.WriteAllText(
        Path.Combine(exportDir, "SHA256SUMS"),
        string.Concat(hashes.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Value + "  " + kv.Key + "\n")));

    Console.WriteLine();
    Console.WriteLine($"protocol   {PlanV3Codec.ProtocolHash()}");
    Console.WriteLine($"manifest   {Path.Combine(exportDir, "manifest.json")}");
    return 0;
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
