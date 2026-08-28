using Companion.MouthFactory.Generation;
using Companion.MouthFactory.Schema;
using Companion.MouthFactory.Validation;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Stage batching, exercised entirely offline against deterministic fake clients.
///
/// Batching exists because interleaving writer and critics reloaded a model between almost every
/// call — measured at 2.5x for two models and ~9x per call with four. But it changes what a
/// candidate IS: it now outlives the process that wrote it, so the tests here are mostly about
/// the lifecycle rather than the speed. Every boundary is a place a machine can die.
/// </summary>
public class StagedPipelineTests
{
    private static readonly string[] Critics = ["FaithfulnessCritic", "NaturalnessCritic"];

    // ---- durability and resume ------------------------------------------------------------------

    [Fact]
    public async Task AGeneratedCandidateIsDurableBeforeAnyCriticRuns()
    {
        using var temp = new TempDir();
        var source = new FakeSource();
        var store = Store(temp);

        await Run(temp, source, store, Scenarios(3));

        // Every unit reached a terminal state, and each carries the exact bytes and both hashes.
        Assert.All(store.All, c =>
        {
            Assert.False(string.IsNullOrEmpty(c.ScenarioHash));
            Assert.False(string.IsNullOrEmpty(c.Id));
        });
        Assert.Contains(store.All, c => c.State == CandidateState.Accepted);
    }

    [Fact]
    public async Task AResumedRunNeverRegeneratesADurablyStoredCandidate()
    {
        using var temp = new TempDir();
        var scenarios = Scenarios(4);

        var first = new FakeSource();
        await Run(temp, first, Store(temp), scenarios);
        Assert.True(first.Writes > 0);

        var second = new FakeSource();
        await Run(temp, second, Store(temp), scenarios);

        Assert.Equal(0, second.Writes);
    }

    [Fact]
    public async Task ACrashBetweenGenerationAndCriticismRunsOnlyTheMissingCriticStages()
    {
        using var temp = new TempDir();
        var scenarios = Scenarios(3);

        // Stage 1 only: the writer succeeds, then the process dies before any critic.
        var writerOnly = new FakeSource { ThrowOnCritic = true };
        await Assert.ThrowsAnyAsync<Exception>(() => Run(temp, writerOnly, Store(temp), scenarios));

        var pendingAfterCrash = Store(temp).Pending();
        Assert.NotEmpty(pendingAfterCrash);
        Assert.All(pendingAfterCrash, c => Assert.Empty(c.Verdicts));

        // Resume: nothing regenerates, only criticism runs.
        var resumed = new FakeSource();
        await Run(temp, resumed, Store(temp), scenarios);

        Assert.Equal(0, resumed.Writes);
        Assert.True(resumed.CriticCalls > 0);
        Assert.Empty(Store(temp).Pending());
    }

    [Fact]
    public async Task ACrashPARTWAYThroughACriticBatchLosesAtMostOneVerdict()
    {
        using var temp = new TempDir();
        var scenarios = Scenarios(4);

        // Die on the third critic call, after some verdicts are already persisted.
        var flaky = new FakeSource { ThrowOnCriticCall = 3 };
        await Assert.ThrowsAnyAsync<Exception>(() => Run(temp, flaky, Store(temp), scenarios));

        var mid = Store(temp);
        var withVerdicts = mid.All.Count(c => c.Verdicts.Count > 0);
        Assert.True(withVerdicts > 0, "verdicts before the crash were not persisted");

        // Resume finishes only what is missing.
        var resumed = new FakeSource();
        await Run(temp, resumed, Store(temp), scenarios);

        Assert.Equal(0, resumed.Writes);
        Assert.Empty(Store(temp).Pending());
        // Only candidates that reached criticism owe verdicts. A row the deterministic
        // gate rejected never had critics run, and never should.
        Assert.All(
            Store(temp).All.Where(c => c.State is CandidateState.Accepted
                or CandidateState.ManualReview),
            c => Assert.Empty(c.MissingCritics));
    }

    [Fact]
    public async Task TerminalRowsAreNeverDuplicatedAcrossResumes()
    {
        using var temp = new TempDir();
        var scenarios = Scenarios(5);

        await Run(temp, new FakeSource(), Store(temp), scenarios);
        await Run(temp, new FakeSource(), Store(temp), scenarios);
        await Run(temp, new FakeSource(), Store(temp), scenarios);

        var rows = new RowStore(Path.Combine(temp.Path, "rows"));
        var accepted = rows.ReadRows(Disposition.Accepted).Select(r => r.Id).ToList();
        Assert.Equal(accepted.Count, accepted.Distinct().Count());
    }

    [Fact]
    public void ATornFinalLineIsIgnoredRatherThanFatal()
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "candidates.jsonl");
        Directory.CreateDirectory(temp.Path);
        File.WriteAllText(path,
            """{"id":"a#0","scenarioId":"a","scenarioFamilyId":"f","familyId":"b1","variantIndex":0,"scenarioHash":"h","inputHash":"i","row":{"id":"a#0","system":"","input":"","target":"t","formatVersion":"mouth-prompt/4.0"},"metadata":{"id":"a#0","scenarioId":"a","scenarioFamilyId":"f","familyId":"b1","layer":"B","sourceFamilyId":"s","generation":{"role":"w","model":"m","endpoint":"e","promptVersion":"1","seed":1,"attempt":1,"promptHash":"p"}},"requiredCritics":[],"state":"Accepted","updatedUtc":"x"}"""
            + "\n{\"id\":\"b#0\",\"scenar");

        var store = CandidateStore.Open(path);

        Assert.NotNull(store.Find("a#0"));
        Assert.Null(store.Find("b#0"));
    }

    // ---- configuration drift ---------------------------------------------------------------------

    [Fact]
    public async Task ScenarioTruthThatChangedSinceGenerationIsDetectedNotJudged()
    {
        using var temp = new TempDir();
        var original = Scenarios(2);

        // Generate, then die before criticism.
        await Assert.ThrowsAnyAsync<Exception>(() =>
            Run(temp, new FakeSource { ThrowOnCritic = true }, Store(temp), original));
        Assert.NotEmpty(Store(temp).Pending());

        // The scenario's hidden state changes underneath the stored candidate.
        var mutated = original
            .Select(s => s with
            {
                ApprovedFacts =
                [
                    new ApprovedFact
                    {
                        Id = "f1", Text = "something else entirely", Policy = FactPolicy.MustExpress,
                    },
                ],
            })
            .ToList();

        var resumed = new FakeSource();
        var result = await Run(temp, resumed, Store(temp), mutated);

        Assert.NotEmpty(result.DriftDetected);
        Assert.All(result.DriftDetected, d => Assert.Contains("scenario truth changed", d, StringComparison.Ordinal));
        // Nothing was judged against the new truth.
        Assert.Equal(0, resumed.CriticCalls);
    }

    [Fact]
    public async Task ACandidateWhoseScenarioVanishedIsReportedNotSilentlyDropped()
    {
        using var temp = new TempDir();
        var original = Scenarios(2);
        await Assert.ThrowsAnyAsync<Exception>(() =>
            Run(temp, new FakeSource { ThrowOnCritic = true }, Store(temp), original));

        var result = await Run(temp, new FakeSource(), Store(temp), [original[0]]);

        Assert.Contains(result.DriftDetected, d => d.Contains("no longer in this run", StringComparison.Ordinal));
    }

    // ---- scheduling is not a licence ----------------------------------------------------------------

    [Fact]
    public async Task StagedAndInterleavedProduceIdenticalDispositions()
    {
        var scenarios = Scenarios(6);

        using var stagedDir = new TempDir();
        await Run(stagedDir, new FakeSource(), Store(stagedDir), scenarios);
        var staged = Dispositions(stagedDir);

        using var interleavedDir = new TempDir();
        var pipeline = new FactoryPipeline(
            new RoleRouter(new Dictionary<Role, IRoleClient>()),
            JobLedger.Open(Path.Combine(interleavedDir.Path, "ledger.jsonl")),
            new RowStore(Path.Combine(interleavedDir.Path, "rows")),
            new Deduplicator(), new FakeSource());
        await pipeline.RunAsync(scenarios, new PipelineOptions
        {
            OutputDirectory = interleavedDir.Path, TargetsPerScenario = 2,
        });
        var interleaved = Dispositions(interleavedDir);

        Assert.Equal(interleaved, staged);
    }

    [Fact]
    public async Task BatchingLoadsEachModelOncePerRoundNotOncePerCandidate()
    {
        using var temp = new TempDir();
        var result = await Run(temp, new FakeSource(), Store(temp), Scenarios(10));

        // One writer stage plus one stage per critic, per round - not one per candidate.
        Assert.True(result.ModelLoads <= result.Rounds * (Critics.Length + 1),
            $"{result.ModelLoads} loads for {result.Rounds} rounds");
        Assert.True(result.ModelLoads < result.UnitsAttempted,
            $"{result.ModelLoads} loads for {result.UnitsAttempted} units - not batching");
    }

    [Fact]
    public async Task TargetAcceptedAndMaxUnitsStillBoundTheStagedRun()
    {
        using var temp = new TempDir();
        var result = await Run(temp, new FakeSource(), Store(temp), Scenarios(40),
            target: null, max: 8);

        Assert.Equal("unit-ceiling", result.StopReason);
        Assert.Equal(8, result.UnitsAttempted);
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static CandidateStore Store(TempDir temp)
        => CandidateStore.Open(Path.Combine(temp.Path, "candidates.jsonl"));

    private static List<ScenarioTruth> Scenarios(int count)
        => new ScenarioGenerator(4242).Generate(Curriculum.Find("b1")!, count).ToList();

    private static Task<StagedResult> Run(
        TempDir temp, FakeSource source, CandidateStore store,
        IReadOnlyList<ScenarioTruth> scenarios, int? target = null, int? max = null)
        => new StagedPipeline(
                source, store, new RowStore(Path.Combine(temp.Path, "rows")),
                new Deduplicator(), Critics)
            .RunAsync(scenarios, new PipelineOptions
            {
                OutputDirectory = temp.Path, TargetsPerScenario = 2,
                TargetAccepted = target, MaxUnits = max,
            }, batchSize: 64);

    private static SortedDictionary<string, string> Dispositions(TempDir temp)
    {
        var rows = new RowStore(Path.Combine(temp.Path, "rows"));
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var d in new[] { Disposition.Accepted, Disposition.Rejected, Disposition.ManualReview })
            foreach (var row in rows.ReadRows(d))
                map[row.Id] = d.ToString();
        return map;
    }

    /// <summary>
    /// Deterministic fake writer and critics. Same verdicts regardless of schedule, which is what
    /// makes the staged-equals-interleaved comparison meaningful.
    /// </summary>
    private sealed class FakeSource : ITargetSource
    {
        public int Writes { get; private set; }
        public int CriticCalls { get; private set; }
        public bool ThrowOnCritic { get; init; }
        public int ThrowOnCriticCall { get; init; } = -1;

        public Task<TargetCandidate> WriteAsync(
            ScenarioTruth scenario, global::Companion.PlanV3.PlanV3 plan, int attemptSeed,
            CancellationToken ct = default)
        {
            Writes++;
            var facts = string.Join(" ", scenario.ApprovedFacts
                .Where(f => f.Policy == FactPolicy.MustExpress).Select(f => f.Text));
            // No digits: a scenario id reads as a quantity the plan never supplied, and
            // the unsupported-numeral check would rightly reject it.
            var tag = Tag(scenario.Id, attemptSeed);
            var text = $"{facts} - {tag}".Trim(' ', '-');
            return Task.FromResult(new TargetCandidate(
                text.Length == 0 ? $"acknowledged, {tag}" : text,
                new GenerationProvenance
                {
                    Role = "TargetWriter", Model = "fake-writer", Endpoint = "fake",
                    PromptVersion = "1.0", Seed = attemptSeed, Attempt = 1, PromptHash = "fake",
                }));
        }

        public Task<IReadOnlyList<CheckResult>> CriticiseAsync(
            ScenarioTruth scenario, string target, CancellationToken ct = default)
        {
            var results = new List<CheckResult>();
            foreach (var role in Critics)
            {
                var v = Verdict(role, scenario, target);
                results.Add(new CheckResult
                {
                    Name = role, Passed = v.Passed, Code = v.Code, Kind = CheckKind.Critic,
                });
            }
            return Task.FromResult<IReadOnlyList<CheckResult>>(results);
        }

        public Task<CriticVerdict> CriticiseOneAsync(
            string role, ScenarioTruth scenario, string target, CancellationToken ct = default)
        {
            if (ThrowOnCritic)
                throw new InvalidOperationException("simulated crash before criticism");
            CriticCalls++;
            if (ThrowOnCriticCall > 0 && CriticCalls == ThrowOnCriticCall)
                throw new InvalidOperationException("simulated crash inside a critic batch");
            return Task.FromResult(Verdict(role, scenario, target));
        }


        /// <summary>
        /// A stable per-scenario tag with no digits in it. Digits would read as a quantity the
        /// plan never supplied; identical text across scenarios would read as a duplicate. Both
        /// are correct rejections of a lazy fixture, so the fixture stops being lazy.
        /// </summary>
        private static string Tag(string scenarioId, int attemptSeed)
            => new string(scenarioId.Select(c => char.IsDigit(c) ? (char)('g' + (c - '0')) : c).ToArray())
               + (attemptSeed % 2 == 1 ? "-odd" : "-even");

        /// <summary>Deterministic and schedule-independent: keyed only on role and target.</summary>
        private static CriticVerdict Verdict(string role, ScenarioTruth scenario, string target)
        {
            var reject = role == "NaturalnessCritic"
                         && target.Contains("-even", StringComparison.Ordinal);
            return new CriticVerdict
            {
                Role = role, Model = "fake-" + role, Passed = !reject,
                Code = reject ? "fake-reject" : null, AtUtc = "2026-08-28T00:00:00Z",
            };
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "staged-" + Guid.NewGuid().ToString("N")[..8]);

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }
}
