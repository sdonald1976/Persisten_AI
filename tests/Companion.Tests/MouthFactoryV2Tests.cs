using Companion.MouthFactory.Export;
using Companion.MouthFactory.Generation;
using Companion.MouthFactory.Schema;
using Companion.MouthFactory.Validation;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The seven corrections the 1,500-row pilot forced, each pinned by the defect that produced it.
///
/// Every test here names a real number from that run. The pilot's value was not the corpus — which
/// is not being trained on — but the evidence that three gates were asleep, a stratum had been
/// silently deleted, and the delivered distribution was not the attempted one. These tests exist so
/// none of those can come back quietly.
/// </summary>
public class MouthFactoryV2Tests
{
    private static readonly Participant User =
        new() { Id = "usr-scott", Name = "Scott", Kind = ParticipantKind.User, Pronouns = "he/him" };

    private static readonly Participant Ava =
        new() { Id = "cmp-ava", Name = "Ava", Kind = ParticipantKind.Companion, Pronouns = "she/her" };

    // ---- 1. exact surface only where it is legitimate -------------------------------------------

    [Fact]
    public void AnOrdinaryFactCarriesNoAnchor()
    {
        // The rule that must not be broken again: the writer is told to use fresh words, so
        // requiring lexical overlap with an ordinary proposition punishes correct behaviour.
        var scenarios = Build();
        var ordinary = scenarios
            .SelectMany(s => s.ApprovedFacts)
            .Where(f => f.Text.Contains("the bread came out flat")
                        || f.Text.Contains("the test suite passed")
                        || f.Text.Contains("the parcel arrived"))
            .ToList();

        Assert.NotEmpty(ordinary);
        Assert.All(ordinary, f => Assert.Empty(f.Anchors));
    }

    [Fact]
    public void ACorrectedValueIsAnchoredAndItsStaleFormForbidden()
    {
        // Every correction scenario, not one: the stratum draws from a pool now, and the anchor
        // rule has to hold for all of them rather than for the one that used to be hardcoded.
        var corrections = Build().Where(s => s.FamilyId == "b3").ToList();
        Assert.NotEmpty(corrections);

        foreach (var scenario in corrections)
        {
            var required = Assert.Single(
                scenario.ApprovedFacts.Where(f => f.Policy == FactPolicy.MustExpress));
            var supersession = Assert.Single(scenario.Superseded);

            Assert.NotEmpty(required.Anchors);
            Assert.All(required.Anchors,
                a => Assert.Contains(a, required.Text, StringComparison.OrdinalIgnoreCase));

            var discriminator = Assert.Single(supersession.DiscriminatingTokens);
            Assert.Contains(scenario.ForbiddenTokens,
                t => t.Equals(discriminator, StringComparison.OrdinalIgnoreCase));

            // The token that marks the stale claim must not appear in the correct one.
            Assert.DoesNotContain(discriminator, required.Text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void AnIdentifierIsRequiredVerbatim()
    {
        var procedures = Build().Where(s => s.FamilyId == "b5").ToList();
        Assert.NotEmpty(procedures);

        foreach (var procedure in procedures)
        {
            var token = Assert.Single(procedure.RequiredTokens);

            // Paraphrasing an identifier is a defect, and only here.
            Assert.False(Assert.Single(
                    DeterministicChecks.Run(procedure, "It uses the usual one now."),
                    c => c.Name == "required-tokens")
                .Passed);
            Assert.True(Assert.Single(
                    DeterministicChecks.Run(procedure, $"It runs {token} now."),
                    c => c.Name == "required-tokens")
                .Passed);
        }
    }

    // ---- the stratum the pilot deleted ----------------------------------------------------------

    [Fact]
    public void CorrectlyStatingTheCorrectedDayIsNotAResurrection()
    {
        // 171 of 178 b3 units were rejected for this exact reply, because the stale text "the
        // meeting is on Thursday" contributed the word "meeting" to the forbidden list. The
        // corrections stratum ended the pilot with zero accepted rows.
        foreach (var scenario in Build().Where(s => s.FamilyId == "b3"))
        {
            var current = Assert.Single(scenario.Superseded).CurrentText;
            var check = Assert.Single(
                DeterministicChecks.Run(scenario, "Right - " + current + ". Thanks for the correction."),
                c => c.Name == "no-stale-resurrection");

            Assert.True(check.Passed, current);
        }
    }

    [Fact]
    public void RestatingTheStaleValueIsStillAResurrection()
    {
        foreach (var scenario in Build().Where(s => s.FamilyId == "b3"))
        {
            var stale = Assert.Single(scenario.Superseded);
            var check = Assert.Single(
                DeterministicChecks.Run(scenario, "It is " + stale.StaleText + ", as far as I know."),
                c => c.Name == "no-stale-resurrection");

            Assert.False(check.Passed);
            Assert.Equal("stale-resurrection", check.Code);
        }
    }

    [Fact]
    public void WithoutDeclaredTokensSharedVocabularyIsNotEvidenceOfResurrection()
    {
        // The fallback derivation must also subtract what the correction itself says, or it
        // recreates the same defect for any supersession that omits its tokens.
        var scenario = Correction() with
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

        Assert.True(Assert.Single(
            DeterministicChecks.Run(scenario, "The meeting moved to Tuesday."),
            c => c.Name == "no-stale-resurrection").Passed);
    }

    // ---- 2. inactive is not a pass --------------------------------------------------------------

    [Fact]
    public void AGateWithNoDataReportsInactiveRatherThanPassing()
    {
        // must-state-anchors, required-tokens and forbidden-tokens each ran 4,352 times in the
        // pilot, failed zero times, and enforced nothing. Zero failures read as approval.
        var plain = Plain();
        var checks = DeterministicChecks.Run(plain, "Second build came through fine.");

        foreach (var name in new[] { "must-state-anchors", "required-tokens", "forbidden-tokens" })
        {
            var check = Assert.Single(checks, c => c.Name == name);
            Assert.Equal(CheckStatus.Inactive, check.Status);
        }
    }

    [Fact]
    public void AnInactiveGateStillNeverRejects()
    {
        var checks = DeterministicChecks.Run(Plain(), "Second build came through fine.");
        Assert.All(checks.Where(c => c.Status == CheckStatus.Inactive), c => Assert.True(c.Passed));
    }

    [Fact]
    public void AGateWithDataReportsRan()
    {
        var check = Assert.Single(
            DeterministicChecks.Run(Correction(), "The meeting is on Tuesday."),
            c => c.Name == "forbidden-tokens");

        Assert.Equal(CheckStatus.Ran, check.Status);
        Assert.True(check.Passed);
    }

    [Fact]
    public void EveryConfiguredGateHasDataSomewhereInTheCurriculum()
    {
        // The startup gate itself: a run whose scenarios cannot exercise a required check refuses
        // rather than reporting a pass rate over gates that were asleep.
        var report = CheckCoverage.Measure(Build());

        Assert.True(report.Ok,
            "gates with no data: " + string.Join(", ", report.Missing.Select(m => m.Check)));
        Assert.All(report.Rows, r => Assert.True(r.Active, r.Check));
    }

    [Fact]
    public void ACurriculumThatStopsPopulatingAFieldIsCaughtAtStartup()
    {
        var stripped = Build().Select(s => s with { RequiredTokens = [] }).ToList();
        var report = CheckCoverage.Measure(stripped);

        Assert.False(report.Ok);
        Assert.Contains(report.Missing, m => m.Check == "required-tokens");
    }

    // ---- 3. density comes from the frozen corpus ------------------------------------------------

    [Fact]
    public void TheMustExpressMixIsTheFrozenOne()
    {
        // Read from train-200.jsonl (sha256 de7a093d…, the artifact freeze-run1c.json names):
        // 127/730 rows carry no SAY item, 466 carry one, 115 two, 22 three.
        Assert.Equal(0.174, MustCountMix.FrozenRun1.None, 3);
        Assert.Equal(0.638, MustCountMix.FrozenRun1.One, 3);
        Assert.Equal(0.158, MustCountMix.FrozenRun1.Two, 3);
        Assert.Equal(0.030, MustCountMix.FrozenRun1.Three, 3);
    }

    [Fact]
    public void GeneratedLayerADensityTracksTheFrozenAnchor()
    {
        // The pilot delivered 29.9% of unframed accepted rows carrying any required item, against
        // the frozen 82.6%. Layer A is where the generator has freedom, so it is where the anchor
        // is checked; Layer B compositions are curriculum structure.
        var layerA = Build().Where(s => s.Layer == CurriculumLayer.A).ToList();
        var withMust = layerA.Count(s => s.ApprovedFacts.Any(f => f.Policy == FactPolicy.MustExpress));
        var share = withMust / (double)layerA.Count;

        Assert.InRange(share, 0.78, 0.88);
    }

    [Fact]
    public void ThePolicyMixSelectsInFrozenProportions()
    {
        var mix = MustCountMix.FrozenRun1;
        Assert.Equal(0, mix.Select(0.10));
        Assert.Equal(1, mix.Select(0.50));
        Assert.Equal(2, mix.Select(0.90));
        Assert.Equal(3, mix.Select(0.99));
    }

    // ---- length is asked for in the proportions production uses -----------------------------------

    [Fact]
    public void ExpansiveIsRareAndNeverAskedForWithoutContent()
    {
        // 794 expansive scenarios produced 32 accepted rows - a 4.0% acceptance rate and 929
        // verbosity rejections - because a quarter of the corpus was asked for 40+ words when the
        // frozen corpus reaches 40 in 2.2% of its rows and runs a median of 15.
        var scenarios = Build();
        var expansive = scenarios
            .Where(s => s.Register.Verbosity.Equals("expansive", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.InRange(expansive.Count / (double)scenarios.Count, 0.0, 0.05);
        Assert.All(expansive, s => Assert.True(
            s.ApprovedFacts.Count(f => f.Policy is FactPolicy.MustExpress or FactPolicy.MayExpress) >= 2,
            s.Id + " asks for an expansive reply with nothing to expand on"));
    }

    [Fact]
    public void TerseTracksTheFrozenShare()
    {
        // The frozen corpus says "terse" in 143 of 730 STYLE lines: 19.6%.
        var scenarios = Build();
        var terse = scenarios.Count(s =>
            s.Register.Verbosity.Equals("terse", StringComparison.OrdinalIgnoreCase));

        Assert.InRange(terse / (double)scenarios.Count, 0.15, 0.30);
    }

    [Fact]
    public void TheExpansiveFloorSitsInsideWhatProductionProduces()
    {
        // p90 of the frozen corpus is 28 words and p95 is 33. A 30-word reply must therefore
        // satisfy an expansive plan; the old 40-word floor was above the 95th percentile of
        // every target production has ever written.
        var scenario = Plain() with { Register = new RegisterControls { Verbosity = "expansive" } };
        var thirtyWords = string.Join(' ', Enumerable.Repeat("word", 30));

        Assert.True(Assert.Single(
            DeterministicChecks.Run(scenario, thirtyWords),
            c => c.Name == "verbosity").Passed);
    }

    [Fact]
    public void RegisterVariesWithinAFamily()
    {
        // One fixed register per family is why all 122 accepted b9 rows opened with the same
        // words - 0.8% distinct openings.
        var b9 = Build().Where(s => s.FamilyId == "b9").ToList();
        var registers = b9
            .Select(s => (s.Register.Warmth, s.Register.Bluntness, s.Register.Verbosity,
                s.Register.Playfulness))
            .Distinct()
            .Count();

        Assert.True(registers > 5, $"b9 has only {registers} distinct registers");
    }

    [Fact]
    public void FamilyRegisterConstraintsSurviveTheVariation()
    {
        var scenarios = Build();

        // The values that DEFINE a stratum are not up for variation.
        Assert.All(scenarios.Where(s => s.FamilyId == "a6d"),
            s => Assert.Equal("encouraged", s.Register.Profanity));
        Assert.All(scenarios.Where(s => s.FamilyId == "a6a"),
            s => Assert.Equal("high", s.Register.Warmth));
        Assert.All(scenarios.Where(s => s.FamilyId == "b4"),
            s => Assert.Equal("high", s.Register.Bluntness));
        Assert.All(scenarios.Where(s => s.FamilyId == "a3"),
            s => Assert.Contains(s.Register.Verbosity, new[] { "terse", "expansive" }));
    }

    // ---- 4. no-must plans need something to react to --------------------------------------------

    [Fact]
    public void EveryGeneratedScenarioIsSatisfiable()
    {
        var unsatisfiable = Build()
            .Select(s => (s, r: ScenarioSatisfiability.Check(s)))
            .Where(x => !x.r.Satisfiable)
            .ToList();

        Assert.True(unsatisfiable.Count == 0,
            string.Join(", ", unsatisfiable.Take(5).Select(x => x.s.Id)));
    }

    [Fact]
    public void EveryNoMustScenarioHasAConcreteUserMessage()
    {
        // The 24.7% evasion rate came from plans obliging nothing over turns raising nothing.
        var noMust = Build()
            .Where(s => s.Frame is null)
            .Where(s => !s.ApprovedFacts.Any(f => f.Policy == FactPolicy.MustExpress))
            .ToList();

        Assert.NotEmpty(noMust);
        foreach (var scenario in noMust)
            Assert.True(
                ScenarioSatisfiability.Check(scenario).Satisfiable,
                scenario.Id + ": " + scenario.UserMessage);
    }

    [Fact]
    public void GenericFillerWithNothingRequiredIsRejected()
    {
        var filler = Plain() with
        {
            ApprovedFacts = [], UserMessage = "any news?", History = [],
            Question = new QuestionPolicySpec { Policy = "may_ask", Text = "short version?" },
        };

        Assert.False(ScenarioSatisfiability.Check(filler).Satisfiable);
    }

    // ---- 5. faithfulness sees the turn it is judging ---------------------------------------------

    [Fact]
    public void TheFaithfulnessCriticIsShownTheUserMessage()
    {
        // A reply about a meeting passed against a printer-jam plan because no critic was ever
        // told what had been asked.
        var scenario = Plain() with { UserMessage = "is the printer working yet?" };
        var described = ModelTargetSource.DescribeForTest(scenario);

        Assert.Contains("THE MESSAGE BEING ANSWERED", described, StringComparison.Ordinal);
        Assert.Contains("is the printer working yet?", described, StringComparison.Ordinal);
    }

    // ---- 6. the accepted mix, not the attempted one ----------------------------------------------

    [Fact]
    public void AnEmptyQuotaWantsEachPolicyInTargetProportion()
    {
        var quota = new AcceptanceQuota(QuestionPolicyMix.FrozenRun1);

        Assert.Equal(0.633, quota.Deficit("none"), 3);
        Assert.Equal(0.214, quota.Deficit("must_ask"), 3);
        Assert.Equal(0.153, quota.Deficit("may_ask"), 3);
    }

    [Fact]
    public void AnOverRepresentedPolicyFallsBehindInThePriorityOrder()
    {
        // The pilot attempted 60.6% forbidden and delivered 47.6%, because unrequested-question
        // rejects forbidden rows and nothing compensated.
        var quota = new AcceptanceQuota(QuestionPolicyMix.FrozenRun1);
        for (var i = 0; i < 50; i++)
            quota.Record(WithPolicy("must_ask", "seed" + i));

        Assert.True(quota.Deficit("none") > quota.Deficit("must_ask"));

        var queue = new[] { WithPolicy("must_ask"), WithPolicy("none"), WithPolicy("may_ask") };
        var ordered = quota.Prioritise(queue, s => s);

        Assert.Equal("none", ordered[0].Question.Policy);
        Assert.Equal("must_ask", ordered[^1].Question.Policy);
    }

    [Fact]
    public void PrioritisationIsStableForEqualDeficits()
    {
        var quota = new AcceptanceQuota(QuestionPolicyMix.FrozenRun1);
        var queue = new[] { WithPolicy("none", "a"), WithPolicy("none", "b"), WithPolicy("none", "c") };

        Assert.Equal(["a", "b", "c"], quota.Prioritise(queue, s => s).Select(s => s.Id));
    }

    // ---- 7. splits are assigned before a row can be exported --------------------------------------

    [Fact]
    public void SplitAssignmentIsDeterministicAndFamilyWide()
    {
        Assert.Equal(
            FamilySplitter.Assign("a1-fam0007", hardCase: false),
            FamilySplitter.Assign("a1-fam0007", hardCase: false));

        var assignments = Build()
            .GroupBy(s => s.ScenarioFamilyId, StringComparer.Ordinal)
            .Select(g => g.Select(s => FamilySplitter.Assign(s.ScenarioFamilyId, s.HardCase))
                .Distinct(StringComparer.Ordinal).Count());

        Assert.All(assignments, count => Assert.Equal(1, count));
    }

    [Fact]
    public void HardCasesGoToTheHardSplit()
        => Assert.Equal("hard", FamilySplitter.Assign("b3-fam0001", hardCase: true));

    [Fact]
    public void ThePlanAndThePerRowAssignmentAgree()
    {
        // One rule, two callers. If these ever diverge, rows written during a run land in a
        // different split than the export believes they are in.
        var scenarios = Build();
        var hard = scenarios.Where(s => s.HardCase).Select(s => s.ScenarioFamilyId)
            .ToHashSet(StringComparer.Ordinal);
        var plan = FamilySplitter.Plan(scenarios, hardFamilies: hard);

        foreach (var scenario in scenarios)
            Assert.Equal(
                plan.FamilyToSplit[scenario.ScenarioFamilyId],
                FamilySplitter.Assign(scenario.ScenarioFamilyId, scenario.HardCase));
    }

    // ---- 8. variants only where wording genuinely varies ------------------------------------------

    [Fact]
    public void ATersePlanWithNothingToSayGetsOneTarget()
    {
        // 684 of 2,585 pilot rejections were duplicates: a second target for a plan with one
        // natural rendering reproduces the first.
        var terse = Plain() with
        {
            ApprovedFacts = [],
            Register = new RegisterControls { Verbosity = "terse" },
        };

        Assert.Equal(1, VariantPolicy.For(terse, 2));
    }

    [Fact]
    public void APlanWithRequiredContentGetsTwo()
        => Assert.Equal(2, VariantPolicy.For(Plain(), 2));

    [Fact]
    public void AFrameGetsTwoBecauseSceneWordingVaries()
    {
        var scene = Plain() with
        {
            ApprovedFacts = [],
            Frame = new FrameState { Transition = "continue", SceneRef = "scene-01" },
        };

        Assert.Equal(2, VariantPolicy.For(scene, 2));
    }

    [Fact]
    public void VariantsAreNeverFewerThanOne()
        => Assert.Equal(1, VariantPolicy.For(Plain(), 0));

    // ---- fixtures ---------------------------------------------------------------------------------

    private static List<ScenarioTruth> Build()
    {
        var generator = new ScenarioGenerator(20260826);
        return Curriculum.Families
            .SelectMany(f => generator.Generate(f, Math.Max(4, f.PilotShare / 3)))
            .ToList();
    }

    private static ScenarioTruth Correction()
        => Build().First(s => s.FamilyId == "b3");

    private static ScenarioTruth Plain() => new()
    {
        Id = "v2-0001",
        FamilyId = "b1",
        ScenarioFamilyId = "b1-fam0001",
        Layer = CurriculumLayer.B,
        Participants = [User, Ava],
        ApprovedFacts =
        [
            new ApprovedFact
            {
                Id = "f1", Text = "the second build finished", Policy = FactPolicy.MustExpress,
            },
        ],
        History = [new Turn { Role = "user", Text = "how'd it go?" }],
        UserMessage = "did it work in the end?",
        Register = new RegisterControls(),
        SourceFamilyId = "fixture/v2",
        Seed = 1,
    };

    private static ScenarioTruth WithPolicy(string policy, string id = "x") => Plain() with
    {
        Id = id,
        Question = new QuestionPolicySpec
        {
            Policy = policy, Text = policy == "none" ? null : "which one?",
        },
    };
}
