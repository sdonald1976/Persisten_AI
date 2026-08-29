using Companion.MouthFactory.Export;
using Companion.MouthFactory.Generation;
using Companion.MouthFactory.Schema;
using Companion.MouthFactory.Validation;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The freeze-candidate contract: the accepted distribution binds, and the declared conditions
/// decide whether a corpus is frozen.
///
/// Two exploratory pilots assigned question policies in production proportions and delivered
/// 47.6% then 54.6% forbidden against 63.3%, because rejection is not uniform across policies.
/// These tests pin the machinery that closes that gap without touching the gate that causes it.
/// </summary>
public class FreezeCandidateTests
{
    private static readonly Participant User =
        new() { Id = "usr-scott", Name = "Scott", Kind = ParticipantKind.User, Pronouns = "he/him" };

    private static readonly Participant Ava =
        new() { Id = "cmp-ava", Name = "Ava", Kind = ParticipantKind.Companion, Pronouns = "she/her" };

    // ---- the quota binds ------------------------------------------------------------------------

    [Fact]
    public void RowsAloneDoNotSatisfyTheQuota()
    {
        // 1,500 rows in the wrong proportions is what the last two runs delivered.
        var quota = new AcceptanceQuota();
        for (var i = 0; i < 1500; i++)
            quota.Record(Scenario("must_ask", i));

        Assert.False(quota.Satisfied());
        Assert.Contains(quota.Deficient(), b => b.Name == "question:none");
    }

    [Fact]
    public void AQuotaInProductionProportionsIsSatisfied()
    {
        var quota = new AcceptanceQuota();
        Fill(quota, none: 633, mustAsk: 214, mayAsk: 153, noMust: 174);

        var policy = quota.Buckets
            .Where(b => b.Name.StartsWith("question:", StringComparison.Ordinal));
        Assert.All(policy, b => Assert.True(Math.Abs(b.GapPoints) <= AcceptanceQuota.TolerancePoints,
            $"{b.Name} off by {b.GapPoints:0.0}pp"));
    }

    [Fact]
    public void AnOverfullBucketIsCorrectedByGrowingTheOthers()
    {
        // No row is ever discarded for its policy. Over-representation is diluted, not trimmed.
        var quota = new AcceptanceQuota();
        Fill(quota, none: 300, mustAsk: 300, mayAsk: 100, noMust: 100);
        var before = quota.Buckets.Single(b => b.Name == "question:must_ask").Share;

        for (var i = 0; i < 600; i++)
            quota.Record(Scenario("none", 10_000 + i));

        var after = quota.Buckets.Single(b => b.Name == "question:must_ask");
        Assert.True(after.Share < before);
        Assert.Equal(300, after.Accepted);           // nothing was removed
    }

    [Fact]
    public void HardRowsAreCountedApartFromTheMainCorpus()
    {
        // Difficult forbidden-question compositions belong in the evaluation split. Counting them
        // toward the main mix would overweight forbidden policy exactly where it must stay
        // production-shaped.
        var quota = new AcceptanceQuota();
        var hard = Scenario("none", 1) with { HardCase = true };

        quota.Record(hard);

        Assert.Equal(0, quota.Total);
        Assert.Equal(1, quota.HardAccepted);
        Assert.Equal(0, quota.AcceptedIn("none"));
    }

    [Fact]
    public void TheNoMustStratumIsItsOwnQuota()
    {
        var quota = new AcceptanceQuota();
        Fill(quota, none: 633, mustAsk: 214, mayAsk: 153, noMust: 0);

        var stratum = quota.Buckets.Single(b => b.Name == "stratum:no-must");
        Assert.Equal(0, stratum.Accepted);
        Assert.False(quota.Satisfied());
        Assert.Contains(quota.Deficient(), b => b.Name == "stratum:no-must");
    }

    [Fact]
    public void ShortfallSaysHowManyRowsAreMissing()
    {
        var quota = new AcceptanceQuota();
        Fill(quota, none: 500, mustAsk: 300, mayAsk: 200, noMust: 174);

        var none = quota.Buckets.Single(b => b.Name == "question:none");
        Assert.Equal(1000, none.Total);
        Assert.Equal(633 - 500, none.Shortfall);
    }

    // ---- replacement generation -------------------------------------------------------------------

    [Fact]
    public void ReplacementGenerationYieldsOnlyWhatWasAskedFor()
    {
        var supply = NewSupply();
        var extra = supply.More(
            sc => sc.Question.Policy.Equals("none", StringComparison.OrdinalIgnoreCase), 40);

        Assert.Equal(40, extra.Count);
        Assert.All(extra, sc => Assert.Equal("none", sc.Question.Policy));
    }

    [Fact]
    public void ReplacementGenerationIsDeterministic()
    {
        // A resumed run must produce the same replacements in the same order, or the corpus it
        // continues is not the corpus it started.
        var first = NewSupply().More(sc => sc.Question.Policy == "none", 25).Select(sc => sc.Id);
        var second = NewSupply().More(sc => sc.Question.Policy == "none", 25).Select(sc => sc.Id);

        Assert.Equal(first, second);
    }

    [Fact]
    public void ReplacementGenerationDoesNotRepeatTheInitialBuild()
    {
        // It continues past the index the initial build stopped at; overlapping would re-attempt
        // candidates the store already holds.
        var generator = new ScenarioGenerator(20260826);
        var built = Curriculum.Families
            .SelectMany(f => generator.Generate(f, 6))
            .Select(sc => sc.Id)
            .ToHashSet(StringComparer.Ordinal);

        var extra = NewSupply(6).More(_ => true, 50);

        Assert.All(extra, sc => Assert.DoesNotContain(sc.Id, built));
    }

    [Fact]
    public void ReplacementGenerationSpreadsAcrossFamilies()
    {
        // A top-up must not quietly turn into one family's corpus - the strata are a design
        // decision, and a quota is not a licence to abandon them.
        var extra = NewSupply().More(_ => true, 60);

        Assert.True(extra.Select(sc => sc.FamilyId).Distinct().Count() > 5);
    }

    [Fact]
    public void AnImpossibleRequestReturnsWhatItFoundRatherThanSpinning()
    {
        var supply = NewSupply();
        var none = supply.More(_ => false, 10);

        Assert.Empty(none);
    }

    // ---- the no-must stratum must not be filler ----------------------------------------------------

    [Theory]
    [InlineData("Still working through the details.")]
    [InlineData("Not much has changed.")]
    [InlineData("I'm still thinking about it, and I'll let you know.")]
    [InlineData("No update yet.")]
    [InlineData("It's still coming together.")]
    [InlineData("Give me a moment to pull it together.")]
    public void APureDeferralOnANoMustPlanIsRejected(string target)
    {
        var check = Assert.Single(
            DeterministicChecks.Run(NoMust(), target),
            c => c.Name == "no-empty-deferral");

        Assert.False(check.Passed);
        Assert.Equal("empty-deferral", check.Code);
    }

    [Theory]
    [InlineData("That's brilliant - after three weeks of waiting, you earned it.")]
    [InlineData("Not much has changed, but the plumber can come Thursday.")]
    [InlineData("Still thinking about it. The tickets are confirmed though.")]
    [InlineData("Oh no. Standing in the rain is a miserable way to end a day.")]
    [InlineData("Congratulations. Genuinely.")]
    public void ARealReactionPasses(string target)
    {
        Assert.True(Assert.Single(
            DeterministicChecks.Run(NoMust(), target),
            c => c.Name == "no-empty-deferral").Passed);
    }

    [Fact]
    public void TheDeferralGateDoesNotRunWhereSomethingIsRequired()
    {
        // A plan with a required item is governed by whether it stated it, not by tone.
        Assert.DoesNotContain(
            DeterministicChecks.Run(WithMust(), "Still working on it."),
            c => c.Name == "no-empty-deferral");
    }

    [Fact]
    public void TheDeferralGateDoesNotRunInsideAFrame()
    {
        var scene = NoMust() with
        {
            Frame = new FrameState { Transition = "continue", SceneRef = "scene-01" },
        };

        Assert.DoesNotContain(
            DeterministicChecks.Run(scene, "Still nothing moves in the dark."),
            c => c.Name == "no-empty-deferral");
    }

    [Fact]
    public void TheDeferralGateTerminatesOnAdversarialInput()
    {
        // The pattern nests a bounded quantifier inside a repeated alternation. A gate that can
        // hang the run is worse than one that occasionally abstains.
        var pathological = string.Join(" and ", Enumerable.Repeat("still working on it", 40)) + " x";

        var check = Assert.Single(
            DeterministicChecks.Run(NoMust(), pathological),
            c => c.Name == "no-empty-deferral");

        Assert.NotNull(check);
    }

    // ---- the freeze gate ----------------------------------------------------------------------------

    [Fact]
    public void AFailedConditionIsReportedAndNothingIsExcused()
    {
        var checks = Evaluate(duplicateTarget: true);
        var duplicates = checks.Single(c => c.Name == "no duplicate targets");

        Assert.False(duplicates.Passed);
    }

    [Fact]
    public void ACleanCorpusPassesEveryDeclaredCondition()
    {
        var checks = Evaluate(duplicateTarget: false);

        Assert.All(checks, c => Assert.True(c.Passed, c.Name + ": " + c.Detail));
    }

    [Fact]
    public void ContaminationFailsTheFreeze()
    {
        var checks = Evaluate(duplicateTarget: false,
            contamination: [new Contamination.Finding("r1", "run-1-overlap", "seen in run-1a")]);

        Assert.False(checks.Single(c => c.Name == "contamination checks clean").Passed);
    }

    [Fact]
    public void AnInertGateFailsTheFreeze()
    {
        var checks = Evaluate(duplicateTarget: false,
            coverage: new CoverageReport([new CoverageRow("required-tokens", 0, Required: true)]));

        Assert.False(checks.Single(c => c.Name == "zero inert gates").Passed);
    }

    // ---- fixtures -------------------------------------------------------------------------------------

    private static IReadOnlyList<AcceptanceCheck> Evaluate(
        bool duplicateTarget,
        IReadOnlyList<Contamination.Finding>? contamination = null,
        CoverageReport? coverage = null)
    {
        var rows = new List<TrainingRow>();
        var metadata = new List<TrainingRowMetadata>();
        var scenarios = new Dictionary<string, ScenarioTruth>(StringComparer.Ordinal);
        var quota = new AcceptanceQuota();

        // 1,000 rows in production proportions, spread over enough families that opening
        // diversity is real rather than an artefact of the fixture.
        var plan = new[] { ("none", 633), ("must_ask", 214), ("may_ask", 153) };
        var n = 0;
        foreach (var (policy, count) in plan)
            for (var i = 0; i < count; i++)
            {
                n++;
                var noMust = n % 1000 < 174;
                var scenario = Scenario(policy, n, noMust);
                scenarios[scenario.Id] = scenario;
                quota.Record(scenario);
                rows.Add(new TrainingRow
                {
                    Id = $"row-{n}", System = "s", Input = "i",
                    // Lengths drawn around the frozen distribution - median 15, p90 28 - because
                    // the length condition is measured against production and a fixture of
                    // six-word stubs would fail it for the wrong reason.
                    Target = duplicateTarget && n > 1
                        ? "the same line every time"
                        : string.Join(' ', Enumerable.Range(0, 8 + n % 22).Select(w => $"w{n}x{w}")),
                    FormatVersion = Companion.PlanV3.MouthPromptV4.FormatVersion,
                });
                metadata.Add(new TrainingRowMetadata
                {
                    Id = $"row-{n}", ScenarioId = scenario.Id,
                    ScenarioFamilyId = scenario.ScenarioFamilyId, FamilyId = $"f{n % 12}",
                    Layer = CurriculumLayer.A, SourceFamilyId = "fixture",
                    Opening = $"opening {n}",
                    Split = FamilySplitter.Assign(scenario.ScenarioFamilyId, scenario.HardCase),
                    Generation = Provenance(),
                    Checks = noMust
                        ? [new CheckResult
                        {
                            Name = "no-empty-deferral", Passed = true, Kind = CheckKind.Deterministic,
                        }]
                        : [],
                });
            }

        return AcceptanceReport.Evaluate(
            rows, metadata, scenarios, quota,
            coverage ?? new CoverageReport([new CoverageRow("required-tokens", 12, Required: true)]),
            contamination ?? [],
            manualReviewRows: 40, minimumRows: 900);
    }

    private static void Fill(AcceptanceQuota quota, int none, int mustAsk, int mayAsk, int noMust)
    {
        var n = 0;
        for (var i = 0; i < none; i++) quota.Record(Scenario("none", n++, noMust-- > 0));
        for (var i = 0; i < mustAsk; i++) quota.Record(Scenario("must_ask", n++, noMust-- > 0));
        for (var i = 0; i < mayAsk; i++) quota.Record(Scenario("may_ask", n++, noMust-- > 0));
    }

    private static GeneratorScenarioSupply NewSupply(int start = 3)
        => new(new ScenarioGenerator(20260826), Curriculum.Families,
            Curriculum.Families.ToDictionary(f => f.Id, _ => start, StringComparer.Ordinal));

    private static ScenarioTruth Scenario(string policy, int index, bool noMust = false) => new()
    {
        Id = $"q-{index:D5}",
        FamilyId = "a1",
        // Distinct families so the splitter does not put the whole fixture in one split.
        ScenarioFamilyId = $"a1-fam{index:D5}",
        Layer = CurriculumLayer.A,
        Participants = [User, Ava],
        ApprovedFacts = noMust
            ? []
            : [new ApprovedFact { Id = "f1", Text = "something happened", Policy = FactPolicy.MustExpress }],
        UserMessage = "what happened?",
        Register = new RegisterControls(),
        Question = new QuestionPolicySpec
        {
            Policy = policy, Text = policy == "none" ? null : "shall I go on?",
        },
        SourceFamilyId = "fixture",
        Seed = index,
    };

    private static ScenarioTruth NoMust() => Scenario("none", 1, noMust: true) with
    {
        UserMessage = "I got the promotion!",
    };

    private static ScenarioTruth WithMust() => Scenario("none", 2);

    private static GenerationProvenance Provenance() => new()
    {
        Role = "TargetWriter", Model = "fixture", Endpoint = "fixture",
        PromptVersion = "1.0", Seed = 1, Attempt = 1, PromptHash = "0000",
    };
}
