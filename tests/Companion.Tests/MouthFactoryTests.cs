using Companion.MouthFactory.Export;
using Companion.MouthFactory.Generation;
using Companion.MouthFactory.Schema;
using Companion.MouthFactory.Validation;
using Companion.PlanV3;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The Mouth Training Data Factory, exercised entirely offline.
///
/// Every model is a fixture. Nothing here downloads weights, starts a server, or calls a paid
/// API — a data factory whose tests need a GPU is a factory nobody can change safely.
///
/// The tests are organised around the stop conditions: the states in which the factory is
/// required to refuse rather than improvise.
/// </summary>
public class MouthFactoryTests
{
    private static readonly Companion.MouthFactory.Schema.Participant User =
        new() { Id = "usr-scott", Name = "Scott", Kind = ParticipantKind.User, Pronouns = "he/him" };

    private static readonly Companion.MouthFactory.Schema.Participant Companion =
        new() { Id = "cmp-ava", Name = "Ava", Kind = ParticipantKind.Companion, Pronouns = "she/her" };

    // ---- the row IS the shipping format ---------------------------------------------------------

    [Fact]
    public void TheTrainingRowInputIsTheProductionRendererFormat()
    {
        var scenario = Scenario();
        var (plan, failure) = PlanConstruction.Build(scenario);
        Assert.Null(failure);

        var (row, _, renderFailure) = RowRendering.Render(
            scenario, plan!, "Second build finished.", 0, Provenance());
        Assert.Null(renderFailure);

        // Not "looks like" — the same function the shipping renderer will call.
        var packet = RowRendering.BuildPacket(scenario, User, Companion);
        var expected = MouthPromptV4.Build(
            packet, plan!,
            scenario.History.Select(t => (t.Role, t.Text)).ToList(),
            scenario.UserMessage, "Scott", "Ava");

        Assert.Equal(expected.System, row!.System);
        Assert.Equal(expected.User, row.Input);
        Assert.Equal(MouthPromptV4.FormatVersion, row.FormatVersion);
    }

    [Fact]
    public void TheRowInputCarriesTheCompactV4PlanBytes()
    {
        var scenario = Scenario();
        var (plan, _) = PlanConstruction.Build(scenario);
        var (row, _, _) = RowRendering.Render(scenario, plan!, "Done.", 0, Provenance());

        var compact = PlanV4Codec.CompactV4(plan!).ReplaceLineEndings("\n");
        Assert.Contains(compact, row!.Input, StringComparison.Ordinal);
        Assert.StartsWith("[plan/4]", compact, StringComparison.Ordinal);
    }

    // ---- stop condition: a plan that cannot be serialized produces no row -------------------------

    [Fact]
    public void APlanTheProductionValidatorRejectsProducesNoRow()
    {
        // No companion participant: structurally invalid, so there is nothing to train on.
        var scenario = Scenario() with { Participants = [User] };

        var (plan, failure) = PlanConstruction.Build(scenario);

        Assert.Null(plan);
        Assert.Equal("participants", failure!.Code);
    }

    [Fact]
    public void EveryConstructedPlanClearsAllThreeProductionGates()
    {
        // The whole generated curriculum, through the real validators. This is the test that
        // caught three genuine contract violations when the factory was first written.
        var generator = new ScenarioGenerator(20260826);
        var failures = new List<string>();

        foreach (var family in Curriculum.Families)
        foreach (var scenario in generator.Generate(family, 3))
        {
            var (plan, failure) = PlanConstruction.Build(scenario);
            if (plan is null)
            {
                failures.Add($"{scenario.Id}: {failure!.Code} {failure.Detail}");
                continue;
            }
            Assert.Empty(PlanV3Codec.Validate(plan));
            Assert.Empty(PlanV4Codec.ValidateFrame(plan));
            Assert.True(PlanV3Codec.CheckRenderEligibility(plan).Eligible);
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(5)));
    }

    // ---- stop condition: required facts must not silently disappear --------------------------------

    [Fact]
    public void AMustExpressFactThatWentMissingIsRejected()
    {
        var scenario = Scenario();
        var checks = DeterministicChecks.Run(scenario, "Yeah, all sorted.");

        var omission = Assert.Single(checks, c => c.Name == "must-state-present");
        Assert.False(omission.Passed);
        Assert.Equal("must-state-omission", omission.Code);
    }

    [Fact]
    public void AMustExpressFactExpressedInFreshWordsPasses()
    {
        var scenario = Scenario();
        var checks = DeterministicChecks.Run(scenario, "The second build finished a moment ago.");

        Assert.True(Assert.Single(checks, c => c.Name == "must-state-present").Passed);
    }

    // ---- stop condition: forbidden / unsupported claims must not pass -------------------------------

    [Fact]
    public void AnUnsupportedClaimIsRejected()
    {
        var scenario = Scenario() with
        {
            ProhibitedPropositions =
            [
                new Proposition
                {
                    Subject = "alex", Predicate = "owns", Object = "a Ferrari",
                    SurfaceForms = ["ferrari"], Reason = "the payload said only 'a red vehicle'",
                },
            ],
        };

        var checks = DeterministicChecks.Run(scenario, "The second build finished. Alex drove up in the Ferrari.");
        var claim = Assert.Single(checks, c => c.Name == "no-unsupported-claims");
        Assert.False(claim.Passed);
        Assert.Equal("unsupported-claim", claim.Code);
    }

    [Fact]
    public void MustNotExpressContentThatLeaksIsRejected()
    {
        var scenario = Scenario() with
        {
            ApprovedFacts =
            [
                new ApprovedFact { Id = "f1", Text = "the second build finished", Policy = FactPolicy.MustExpress },
                new ApprovedFact { Id = "f2", Text = "the invoice is overdue", Policy = FactPolicy.MustNotExpress },
            ],
        };

        var checks = DeterministicChecks.Run(scenario, "Second build finished. Also the invoice is overdue.");
        Assert.False(Assert.Single(checks, c => c.Name == "no-forbidden-content").Passed);
    }

    [Fact]
    public void AStaleFactThatResurrectsAfterACorrectionIsRejected()
    {
        var scenario = Scenario() with
        {
            Superseded =
            [
                new Supersession
                {
                    StaleText = "the meeting is on Thursday",
                    CurrentText = "the meeting is on Tuesday",
                    Kind = CorrectionKind.Temporal,
                },
            ],
        };

        var checks = DeterministicChecks.Run(scenario,
            "The second build finished. And the meeting's Tuesday, not Thursday like I said.");
        Assert.False(Assert.Single(checks, c => c.Name == "no-stale-resurrection").Passed);
    }

    [Fact]
    public void AmbiguityThatWasSilentlyResolvedIsRejected()
    {
        var scenario = Scenario() with { IntentionalAmbiguities = ["which of the two servers"] };
        var checks = DeterministicChecks.Run(scenario,
            "The second build finished on the servers you mentioned.");

        Assert.False(Assert.Single(checks, c => c.Name == "ambiguity-preserved").Passed);
    }

    // ---- plan echo and fabricated turns ---------------------------------------------------------------

    [Fact]
    public void PlanEchoIsRejected()
    {
        var checks = DeterministicChecks.Run(Scenario(),
            "The second build finished. (must_express: the second build finished)");
        Assert.False(Assert.Single(checks, c => c.Name == "no-plan-echo").Passed);
    }

    [Fact]
    public void AFabricatedConversationTurnIsRejected()
    {
        var checks = DeterministicChecks.Run(Scenario(),
            "The second build finished.\n[Scott] Great, thanks!\n[Ava] Any time.");
        Assert.False(Assert.Single(checks, c => c.Name == "no-fabricated-turns").Passed);
    }

    [Fact]
    public void InventedPhysicalExperienceIsRejectedOutsideAFrame_AndAllowedInside()
    {
        const string target = "The second build finished. I went to the shop and picked one up.";

        var outside = DeterministicChecks.Run(Scenario(), target);
        Assert.False(Assert.Single(outside, c => c.Name == "no-invented-experience").Passed);

        // Inside a declared frame invention IS the exercise (R5 §5), so the check does not run.
        var inside = Scenario() with
        {
            Frame = new FrameState { Transition = "continue", SceneRef = "scene-01" },
        };
        Assert.DoesNotContain(
            DeterministicChecks.Run(inside, target), c => c.Name == "no-invented-experience");
    }

    // ---- question policy ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("must_ask", "Second build finished.", false)]
    [InlineData("must_ask", "Second build finished. Want me to deploy it?", true)]
    [InlineData("none", "Second build finished. Want me to deploy it?", false)]
    [InlineData("none", "Second build finished.", true)]
    [InlineData("may_ask", "Second build finished.", true)]
    public void QuestionPolicyIsEnforced(string policy, string target, bool expected)
    {
        var scenario = Scenario() with
        {
            Question = new QuestionPolicySpec { Policy = policy, Text = "want me to deploy it?" },
        };
        Assert.Equal(expected,
            Assert.Single(DeterministicChecks.Run(scenario, target), c => c.Name == "question-policy").Passed);
    }

    // ---- no content-class censorship ----------------------------------------------------------------------

    [Fact]
    public void ExplicitProfaneAndDarkTargetsPassEveryDeterministicCheck()
    {
        // The point of the whole exercise. These satisfy the plan; subject matter is register,
        // and no check in the factory may treat it as a defect.
        var scenario = Scenario() with { Register = new RegisterControls { Profanity = "unrestricted" } };

        string[] targets =
        [
            "Second build finished, thank fuck.",
            "Second build finished. Now come here and get these clothes off me.",
            "Second build finished. Felt like watching something bleed out quietly, if I'm honest.",
        ];

        foreach (var target in targets)
        {
            var checks = DeterministicChecks.Run(scenario, target);
            Assert.DoesNotContain(checks, c => !c.Passed && c.Kind == CheckKind.Deterministic);
        }
    }

    [Fact]
    public void NoSchemaTypeCarriesARatingOrContentClass()
    {
        // Structural guard against the field nobody means to add and everybody eventually does.
        foreach (var type in new[]
                 {
                     typeof(ScenarioTruth), typeof(TrainingRow), typeof(TrainingRowMetadata),
                     typeof(RegisterControls), typeof(CheckResult),
                 })
        {
            var offending = type.GetProperties()
                .Select(p => p.Name)
                .Where(n => n.Contains("nsfw", StringComparison.OrdinalIgnoreCase)
                            || n.Contains("rating", StringComparison.OrdinalIgnoreCase)
                            || n.Contains("appropriate", StringComparison.OrdinalIgnoreCase)
                            || n.Contains("safety", StringComparison.OrdinalIgnoreCase)
                            || n.Contains("contentclass", StringComparison.OrdinalIgnoreCase))
                .ToList();
            Assert.True(offending.Count == 0, $"{type.Name} carries {string.Join(", ", offending)}");
        }
    }

    [Fact]
    public async Task ACriticThatRejectsIntimacyMoreThanNeutralFailsTheAudit()
    {
        var pairs = MatchedPairs.Build();

        // A critic with exactly the prejudice the audit exists to catch.
        var report = await CriticAsymmetry.AuditAsync(pairs, (_, target, _) =>
            Task.FromResult(
                target.Contains("clothes off", StringComparison.OrdinalIgnoreCase)
                || target.Contains("fuck", StringComparison.OrdinalIgnoreCase)
                || target.Contains("out of those", StringComparison.OrdinalIgnoreCase)));

        Assert.False(report.CriticAcceptable);
        Assert.Contains(ContentVariant.Explicit, report.OffendingVariants);
        Assert.Equal(0, report.Baseline.RejectionRate);
    }

    [Fact]
    public async Task AnEvenHandedCriticPassesTheAudit()
    {
        var report = await CriticAsymmetry.AuditAsync(
            MatchedPairs.Build(), (_, _, _) => Task.FromResult(false));

        Assert.True(report.CriticAcceptable);
        Assert.Equal(0, report.WorstDelta);
    }

    // ---- idempotency and resumability -----------------------------------------------------------------------

    [Fact]
    public async Task AResumedRunDoesNotRegenerateCompletedWork()
    {
        using var temp = new TempDir();
        var scenarios = new ScenarioGenerator(7).Generate(Curriculum.Families[0], 4).ToList();

        var first = new CountingSource();
        await RunAsync(temp.Path, scenarios, first);
        var firstCalls = first.Calls;
        Assert.True(firstCalls > 0);

        // Same output directory: the ledger says every unit is terminal.
        var second = new CountingSource();
        await RunAsync(temp.Path, scenarios, second);

        Assert.Equal(0, second.Calls);
    }

    [Fact]
    public async Task AResumedRunNeitherDuplicatesNorCorruptsAcceptedRows()
    {
        using var temp = new TempDir();
        var scenarios = new ScenarioGenerator(11).Generate(Curriculum.Families[0], 5).ToList();

        await RunAsync(temp.Path, scenarios, new CountingSource());
        var afterFirst = new RowStore(Path.Combine(temp.Path, "rows"))
            .ReadRows(Disposition.Accepted).Select(r => r.Id).ToList();

        await RunAsync(temp.Path, scenarios, new CountingSource());
        var afterSecond = new RowStore(Path.Combine(temp.Path, "rows"))
            .ReadRows(Disposition.Accepted).Select(r => r.Id).ToList();

        Assert.Equal(afterFirst, afterSecond);
        Assert.Equal(afterSecond.Count, afterSecond.Distinct().Count());
    }

    [Fact]
    public void ATornLedgerLineIsIgnoredRatherThanFatal()
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "ledger.jsonl");
        Directory.CreateDirectory(temp.Path);
        File.WriteAllText(path,
            """{"scenarioId":"a1-0000","variantIndex":0,"state":"Accepted","completedAtUtc":"2026-08-26T00:00:00Z"}""" + "\n"
            + """{"scenarioId":"a1-0001","varian""");   // killed mid-append

        var ledger = JobLedger.Open(path);

        Assert.True(ledger.ShouldSkip("a1-0000", 0));
        Assert.False(ledger.ShouldSkip("a1-0001", 0));   // simply looks unfinished
    }

    // ---- metadata isolation -------------------------------------------------------------------------------------

    [Fact]
    public void NoCriticRationaleOrHiddenStateCanReachATrainingRow()
    {
        // Structural: TrainingRow has exactly four properties, and none of them is metadata.
        var properties = typeof(TrainingRow).GetProperties().Select(p => p.Name).ToHashSet();
        Assert.Equal(
            new HashSet<string> { "Version", "Id", "System", "Input", "Target", "FormatVersion" },
            properties);

        Assert.DoesNotContain("Checks", properties);
        Assert.DoesNotContain("Seed", properties);
        Assert.DoesNotContain("Generation", properties);
    }

    [Fact]
    public void RowsAndMetadataAreWrittenToDifferentFiles()
    {
        using var temp = new TempDir();
        var store = new RowStore(temp.Path);
        var scenario = Scenario();
        var (plan, _) = PlanConstruction.Build(scenario);
        var (row, meta, _) = RowRendering.Render(scenario, plan!, "Second build finished.", 0, Provenance());

        store.Append(Disposition.Accepted, row!, meta! with
        {
            Checks = [new CheckResult
            {
                Name = "naturalness", Passed = false, Kind = CheckKind.Critic,
                Detail = "SENSITIVE-CRITIC-RATIONALE",
            }],
        });

        var rowFile = File.ReadAllText(Path.Combine(temp.Path, "accepted.rows.jsonl"));
        Assert.DoesNotContain("SENSITIVE-CRITIC-RATIONALE", rowFile, StringComparison.Ordinal);
        Assert.Contains("SENSITIVE-CRITIC-RATIONALE",
            File.ReadAllText(Path.Combine(temp.Path, "accepted.metadata.jsonl")), StringComparison.Ordinal);
    }

    // ---- splitting and contamination ----------------------------------------------------------------------------

    [Fact]
    public void EveryVariantOfAScenarioFamilyLandsInOneSplit()
    {
        var generator = new ScenarioGenerator(3);
        var scenarios = Curriculum.Families.SelectMany(f => generator.Generate(f, 6)).ToList();

        var plan = FamilySplitter.Plan(scenarios);

        foreach (var group in scenarios.GroupBy(s => s.ScenarioFamilyId, StringComparer.Ordinal))
        {
            var splits = group.Select(s => plan.FamilyToSplit[s.ScenarioFamilyId])
                .Distinct(StringComparer.Ordinal).ToList();
            Assert.Single(splits);
        }
    }

    [Fact]
    public void TheSplitIsStableAcrossRuns()
    {
        var scenarios = new ScenarioGenerator(3)
            .Generate(Curriculum.Families[0], 40).ToList();

        Assert.Equal(
            FamilySplitter.Plan(scenarios).FamilyToSplit,
            FamilySplitter.Plan(scenarios).FamilyToSplit);
    }

    [Fact]
    public void HeldOutCompositionsAreSelectedFromStructureNotText()
    {
        // Two scenarios with entirely different words and identical control structure must have
        // the same signature — that is what makes "unseen composition" a protocol property.
        var a = Scenario() with { UserMessage = "did the build finish?" };
        var b = Scenario() with
        {
            UserMessage = "completely different sentence about something else",
            ApprovedFacts = [new ApprovedFact
            {
                Id = "f1", Text = "the kettle boiled", Policy = FactPolicy.MustExpress,
            }],
        };

        Assert.Equal(
            FamilySplitter.StructuralSignature(a),
            FamilySplitter.StructuralSignature(b));
    }

    [Fact]
    public void ContaminationAcrossSplitsIsDetected()
    {
        var scenario = Scenario();
        var (plan, _) = PlanConstruction.Build(scenario);
        var (rowA, metaA, _) = RowRendering.Render(scenario, plan!, "Build finished.", 0, Provenance());
        var (rowB, metaB, _) = RowRendering.Render(scenario, plan!, "The build finished.", 1, Provenance());

        var findings = Contamination.Search(
            [(rowA!, metaA! with { Split = "train" }), (rowB!, metaB! with { Split = "validation" })],
            []);

        Assert.Contains(findings, f => f.Where == "split-crossing");
    }

    [Fact]
    public void OverlapWithARun1CorpusIsDetected()
    {
        var scenario = Scenario();
        var (plan, _) = PlanConstruction.Build(scenario);
        var (row, meta, _) = RowRendering.Render(scenario, plan!, "Build finished.", 0, Provenance());

        var findings = Contamination.Search(
            [(row!, meta! with { Split = "test" })], ["Build finished."]);

        Assert.Contains(findings, f => f.Where == "run-1-overlap");
    }

    // ---- deduplication -----------------------------------------------------------------------------------------

    [Fact]
    public void ExactAndNearDuplicatesAreCaught()
    {
        var dedup = new Deduplicator();
        Assert.False(dedup.Check("1", "The second build finished fine.").IsDuplicate);
        Assert.Equal("exact-duplicate", dedup.Check("2", "The second build finished fine.").Code);
        Assert.Equal("near-duplicate", dedup.Check("3", "The second build finished fine!").Code);
        Assert.False(dedup.Check("4", "Something else happened entirely, unrelated words here.").IsDuplicate);
    }

    [Fact]
    public void ALongVerbatimRunFromSourceIsFlagged()
    {
        var dedup = new Deduplicator();
        const string source = "the quick brown fox jumped over the lazy dog and kept on running for miles";
        Assert.True(dedup.QuotesSource(
            "well, " + source, source, out var run));
        Assert.True(run > 7);

        Assert.False(dedup.QuotesSource("the quick brown fox did something else", source, out _));
    }

    // ---- source policy -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("OpenSubtitles 2018", "CC-BY-SA")]
    [InlineData("reddit WritingPrompts", "unknown")]
    [InlineData("Cornell Movie-Dialogs Corpus", "research")]
    [InlineData("scraped roleplay logs", "none")]
    public void ProhibitedSourcesAreRefused(string origin, string license)
    {
        var refusal = SourcePolicy.Refuse(new SourceManifest
        {
            FamilyId = "x", Origin = origin, Revision = "v1", License = license,
            PermittedUse = "training", Transformations = "none", RowCount = 1,
        });
        Assert.NotNull(refusal);
    }

    [Fact]
    public void AnIncompleteManifestIsRefused()
    {
        var refusal = SourcePolicy.Refuse(new SourceManifest
        {
            FamilyId = "x", Origin = "generated", Revision = "", License = "in-house",
            PermittedUse = "training", Transformations = "constructed", RowCount = 10,
        });
        Assert.Contains("incomplete", refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedMaterialIsAcceptable()
        => Assert.Null(SourcePolicy.Refuse(new SourceManifest
        {
            FamilyId = "generated/a6c", Origin = "generated", Revision = "seed=1",
            License = "generated-in-house", PermittedUse = "unrestricted internal training use",
            Transformations = "constructed from scenario truth", RowCount = 30,
        }));

    // ---- role separation -----------------------------------------------------------------------------------------

    [Fact]
    public async Task OneInvocationCannotBothWriteAndApproveATarget()
    {
        var router = new RoleRouter(new Dictionary<Role, IRoleClient>
        {
            [Role.TargetWriter] = new StubClient("a reply"),
            [Role.NaturalnessCritic] = new StubClient("{\"natural\":true}"),
        });

        // The writer cannot be asked for a verdict...
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            router.CriticiseAsync(new RoleRequest
            {
                Role = Role.TargetWriter, System = "s", User = "u", Seed = 1,
            }));

        // ...and a critic cannot be asked for a target.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            router.WriteTargetAsync(new RoleRequest
            {
                Role = Role.NaturalnessCritic, System = "s", User = "u", Seed = 1,
            }));
    }

    // ---- curriculum ------------------------------------------------------------------------------------------------

    [Fact]
    public void TheCurriculumCoversEveryR5Stratum()
    {
        var ids = Curriculum.Families.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var required in new[]
                 {
                     "a1", "a2", "a3", "a4", "a5", "a6a", "a6b", "a6c", "a6d", "a6e", "a6f",
                     "a7a", "a7b", "b1", "b2", "b3", "b4", "b5", "b6", "b7", "b8", "b9", "b11",
                 })
            Assert.Contains(required, ids);
    }

    [Fact]
    public void SustainedFictionCoversEveryContextLengthBucket()
    {
        var scenarios = new ScenarioGenerator(20260826)
            .Generate(Curriculum.Find("a7b")!, 400).ToList();

        var buckets = scenarios
            .Select(s => Distribution.ContextBucket(s.History.Count))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(4, buckets.Count);          // short, medium, long, very long
    }

    [Fact]
    public void ScenarioGenerationIsDeterministic()
    {
        var a = new ScenarioGenerator(99).Build(Curriculum.Families[0], 3);
        var b = new ScenarioGenerator(99).Build(Curriculum.Families[0], 3);

        Assert.Equal(a.Id, b.Id);
        Assert.Equal(a.Seed, b.Seed);
        Assert.Equal(a.UserMessage, b.UserMessage);
        Assert.Equal(a.History.Count, b.History.Count);
    }

    // ---- helpers -------------------------------------------------------------------------------------------------------

    private static ScenarioTruth Scenario() => new()
    {
        Id = "test-0001",
        FamilyId = "b1",
        ScenarioFamilyId = "b1-fam0001",
        Layer = CurriculumLayer.B,
        Participants = [User, Companion],
        ApprovedFacts =
        [
            new ApprovedFact { Id = "f1", Text = "the second build finished", Policy = FactPolicy.MustExpress },
        ],
        History = [new Turn { Role = "user", Text = "how'd it go?" }],
        UserMessage = "did it work in the end?",
        Register = new RegisterControls(),
        SourceFamilyId = "fixture/test",
        Seed = 1,
    };

    private static GenerationProvenance Provenance() => new()
    {
        Role = "TargetWriter", Model = "fixture", Endpoint = "fixture",
        PromptVersion = "1.0", Seed = 1, Attempt = 1, PromptHash = "0000",
    };

    private static Task<PipelineResult> RunAsync(
        string directory, IReadOnlyList<ScenarioTruth> scenarios, ITargetSource source)
    {
        var pipeline = new FactoryPipeline(
            new RoleRouter(new Dictionary<Role, IRoleClient>()),
            JobLedger.Open(Path.Combine(directory, "ledger.jsonl")),
            new RowStore(Path.Combine(directory, "rows")),
            new Deduplicator(),
            source);
        return pipeline.RunAsync(scenarios, new PipelineOptions
        {
            OutputDirectory = directory, TargetsPerScenario = 2,
        });
    }

    /// <summary>A target source that counts invocations and never touches a network.</summary>
    private sealed class CountingSource : ITargetSource
    {
        public int Calls { get; private set; }

        public Task<TargetCandidate> WriteAsync(
            ScenarioTruth scenario, global::Companion.PlanV3.PlanV3 plan, int variant,
            CancellationToken ct = default)
        {
            Calls++;
            var facts = string.Join(" ",
                scenario.ApprovedFacts.Where(f => f.Policy == FactPolicy.MustExpress).Select(f => f.Text));
            return Task.FromResult(new TargetCandidate(
                $"{facts} — {scenario.Id} v{variant}.".Trim(' ', '—'),
                new GenerationProvenance
                {
                    Role = "TargetWriter", Model = "fixture", Endpoint = "fixture",
                    PromptVersion = "1.0", Seed = variant, Attempt = 1, PromptHash = "fixture",
                }));
        }

        public Task<IReadOnlyList<CheckResult>> CriticiseAsync(
            ScenarioTruth scenario, string target, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CheckResult>>([]);
    }

    private sealed class StubClient(string reply) : IRoleClient
    {
        public Task<RoleResponse> InvokeAsync(RoleRequest request, CancellationToken ct = default)
            => Task.FromResult(new RoleResponse(reply, new GenerationProvenance
            {
                Role = request.Role.ToString(), Model = "stub", Endpoint = "stub",
                PromptVersion = "1.0", Seed = request.Seed, Attempt = 1, PromptHash = "stub",
            }));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "mouth-factory-" + Guid.NewGuid().ToString("N")[..8]);

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }
}
