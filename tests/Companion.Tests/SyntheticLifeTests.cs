using Companion.Eval;
using System.Text.Json;
using Xunit;

namespace Companion.Tests;

public class SyntheticLifeTests
{
    [Fact]
    public void Generation_is_deterministic_for_seed()
    {
        var request = new SyntheticRunRequest(1827, People: 2, TurnsPerPerson: 120);

        var first = SyntheticLife.GenerateRows(request);
        var second = SyntheticLife.GenerateRows(request);

        Assert.Equal(Fingerprint(first), Fingerprint(second));
    }

    [Fact]
    public void Hidden_state_evolves_before_dialogue_is_evaluated()
    {
        var row = SyntheticLife.GenerateRows(RequestWith("SUPERSEDES", 1))
            .First(r => r.ExpectedLabel == "SUPERSEDES" && r.ExpectedMemoryOperation == "REPLACE");

        Assert.Contains(row.CanonicalStateBefore.Facts,
            f => f.Key == "preference.coffee" && f.Active && f.Value != row.CurrentFact.Value);
        Assert.Contains(row.CanonicalStateAfter.Facts,
            f => f.Key == "preference.coffee" && f.Active && f.Value == row.CurrentFact.Value);
        Assert.DoesNotContain(row.CanonicalStateAfter.Facts,
            f => f.Key == "preference.coffee" && f.Active && f.Value == row.PreviousFact?.Value);
    }

    [Fact]
    public void Generated_dialogue_does_not_decide_ground_truth()
    {
        var row = SyntheticLife.GenerateRows(RequestWith("CORRECTS", 1))
            .First(r => r.ExpectedLabel == "CORRECTS");

        Assert.Equal("CORRECTS", row.ExpectedLabel);
        Assert.Equal("REPLACE", row.ExpectedMemoryOperation);
        Assert.NotEmpty(row.Utterance);
    }

    [Fact]
    public void Long_distance_events_survive_unrelated_turns()
    {
        var scenario = SyntheticLife.Generate(RequestWith("SUPERSEDES", 1)).Single();
        var supersession = scenario.Examples.First(r => r.ExpectedLabel == "SUPERSEDES");

        Assert.True(supersession.EventDistance > 0);
        Assert.Contains(supersession.EventDistanceBucket, supersession.Difficulty);
        Assert.Contains(scenario.Turns, t => !t.Relevant);
    }

    [Fact]
    public void Correction_refinement_and_supersession_are_distinct()
    {
        var rows = SyntheticLife.GenerateRows(new SyntheticRunRequest(
            1827,
            People: 4,
            TurnsPerPerson: 160,
            EventsPerPerson: 8,
            MinSemanticFamilies: new Dictionary<string, int>
            {
                ["CORRECTS"] = 1,
                ["REFINES"] = 1,
                ["SUPERSEDES"] = 1,
            }));

        Assert.Equal("REPLACE", rows.First(r => r.ExpectedLabel == "CORRECTS").ExpectedMemoryOperation);
        Assert.Equal("MERGE", rows.First(r => r.ExpectedLabel == "REFINES").ExpectedMemoryOperation);
        Assert.Equal("REPLACE", rows.First(r => r.ExpectedLabel == "SUPERSEDES").ExpectedMemoryOperation);
    }

    [Fact]
    public void Multiple_synthetic_users_remain_isolated()
    {
        var scenarios = SyntheticLife.Generate(new SyntheticRunRequest(1827, People: 2, TurnsPerPerson: 120));

        Assert.Equal(2, scenarios.Select(s => s.Person.Id).Distinct().Count());
        foreach (var scenario in scenarios)
        {
            Assert.All(scenario.Examples,
                row => Assert.StartsWith(scenario.Person.Id, row.PersonId, StringComparison.Ordinal));
            Assert.DoesNotContain(scenario.Examples,
                row => row.CanonicalStateAfter.Facts.Any(f =>
                    f.SubjectId.StartsWith("life-", StringComparison.Ordinal) &&
                    f.SubjectId != scenario.Person.Id));
        }
    }

    [Fact]
    public void Dataset_serialization_round_trips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"synthetic-life-{Guid.NewGuid():N}.jsonl");
        var rows = SyntheticLife.GenerateRows(new SyntheticRunRequest(1827, TurnsPerPerson: 120));

        try
        {
            SyntheticLife.WriteJsonl(path, rows);
            var read = SyntheticLife.ReadJsonl(path);
            Assert.Equal(Fingerprint(rows), Fingerprint(read));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Replay_reproduces_original_scenario()
    {
        var request = new SyntheticRunRequest(1827, People: 2, TurnsPerPerson: 120);
        var original = SyntheticLife.Generate(request).Single(s => s.Person.Id == "life-0001");

        var replay = SyntheticLife.Replay(1827, "life-0001", request);

        Assert.Equal(
            original.Turns.Select(t => (t.Turn, t.PersonId, t.Utterance, t.Relevant, t.ScenarioId)),
            replay.Turns.Select(t => (t.Turn, t.PersonId, t.Utterance, t.Relevant, t.ScenarioId)));
        Assert.Equal(Fingerprint(original.Examples), Fingerprint(replay.Examples));
    }

    [Fact]
    public void Evaluation_marks_failures_and_exports_disagreements()
    {
        var rows = SyntheticLife.GenerateRows(new SyntheticRunRequest(1827, TurnsPerPerson: 120));

        var evaluated = SyntheticEvaluation.Evaluate(rows, new KeywordProbe(), new ExpectedOperationProbe());
        var failures = SyntheticEvaluation.FailuresAndDisagreements(evaluated);

        Assert.NotEmpty(failures);
        Assert.Contains(failures, r => r.DisagreementCategory == "incumbent-failure");
    }

    [Fact]
    public void Deterministic_tests_require_no_external_llm()
    {
        var rows = SyntheticLife.GenerateRows(new SyntheticRunRequest(1827, TurnsPerPerson: 120));

        Assert.All(rows, row => Assert.Equal(SyntheticLife.GeneratorVersion, row.Generator));
        Assert.All(rows, row => Assert.Null(row.ModelVersion));
    }

    [Fact]
    public void Different_seeds_produce_meaningful_variation()
    {
        var first = SyntheticLife.Generate(new SyntheticRunRequest(100, People: 3, TurnsPerPerson: 160));
        var second = SyntheticLife.Generate(new SyntheticRunRequest(200, People: 3, TurnsPerPerson: 160));

        Assert.NotEqual(
            first.SelectMany(s => s.Examples).Select(r => r.StructureKey),
            second.SelectMany(s => s.Examples).Select(r => r.StructureKey));
        Assert.NotEqual(
            first.Select(s => s.Person.Style.Register),
            second.Select(s => s.Person.Style.Register));
    }

    [Fact]
    public void Event_ordering_and_spacing_vary()
    {
        var scenarios = SyntheticLife.Generate(new SyntheticRunRequest(1827, People: 8, TurnsPerPerson: 180));

        Assert.True(scenarios.Select(s => string.Join(",", s.Examples.Select(r => r.Family))).Distinct().Count() > 1);
        Assert.True(scenarios.SelectMany(s => s.Examples).Select(r => r.EventDistanceBucket).Distinct().Count() > 2);
    }

    [Fact]
    public void One_fact_can_evolve_multiple_times()
    {
        var rows = SyntheticLife.GenerateRows(new SyntheticRunRequest(
            1827,
            People: 3,
            TurnsPerPerson: 220,
            EventsPerPerson: 10,
            MinSemanticFamilies: new Dictionary<string, int> { ["SUPERSEDES"] = 4 }));

        var coffeeChanges = rows
            .Where(r => r.CurrentFact.Key == "preference.coffee" && r.ExpectedMemoryOperation == "REPLACE")
            .ToList();

        Assert.True(coffeeChanges.Count >= 3);
        Assert.True(coffeeChanges.Select(r => r.CurrentFact.Value).Distinct().Count() >= 2);
    }

    [Fact]
    public void Temporary_state_does_not_automatically_overwrite_permanent_state()
    {
        var row = SyntheticLife.GenerateRows(new SyntheticRunRequest(
            1827,
            People: 2,
            TurnsPerPerson: 160,
            EventsPerPerson: 8,
            MinSemanticFamilies: new Dictionary<string, int> { ["UNCERTAIN"] = 1 }))
            .First(r => r.ExpectedMemoryOperation == "DO_NOT_PROMOTE");

        Assert.False(row.CurrentFact.Active);
        Assert.False(row.Permanent);
        Assert.DoesNotContain(row.CanonicalStateAfter.Facts,
            f => f.Key == row.CurrentFact.Key && f.Active && f.Value == row.CurrentFact.Value);
    }

    [Fact]
    public void Correction_of_correction_is_represented()
    {
        var rows = SyntheticLife.GenerateRows(new SyntheticRunRequest(1827, People: 20, TurnsPerPerson: 180));

        Assert.Contains(rows, r => r.Family == "correction-of-correction" &&
            r.ExpectedLabel == "CORRECTS" &&
            r.Difficulty.Contains("correction-of-correction"));
    }

    [Fact]
    public void Another_person_state_remains_isolated()
    {
        var row = SyntheticLife.GenerateRows(new SyntheticRunRequest(
            1827,
            People: 4,
            TurnsPerPerson: 160,
            EventsPerPerson: 8,
            MinSemanticFamilies: new Dictionary<string, int> { ["COEXIST"] = 4 }))
            .First(r => r.ExpectedMemoryOperation == "STORE_OTHER_SUBJECT");

        Assert.StartsWith("other:", row.CurrentFact.SubjectId, StringComparison.Ordinal);
        Assert.DoesNotContain(row.AffectedFacts, f => f.SubjectId == row.PersonId);
    }

    [Fact]
    public void Multiple_candidate_memories_are_represented()
    {
        var rows = SyntheticLife.GenerateRows(new SyntheticRunRequest(
            1827,
            People: 8,
            TurnsPerPerson: 220,
            EventsPerPerson: 12,
            MinSemanticFamilies: new Dictionary<string, int> { ["SUPERSEDES"] = 4 }));

        Assert.Contains(rows, r => r.Difficulty.Contains("multiple-candidate-memories") &&
            r.CandidateFacts.Count > 1);
    }

    [Fact]
    public void Scenario_family_coverage_constraints_work()
    {
        var request = new SyntheticRunRequest(
            1827,
            People: 8,
            TurnsPerPerson: 160,
            EventsPerPerson: 8,
            MinSemanticFamilies: new Dictionary<string, int>
            {
                ["SUPERSEDES"] = 4,
                ["CORRECTS"] = 3,
            });
        var scenarios = SyntheticLife.Generate(request);
        var report = SyntheticLife.Report(scenarios, request);

        Assert.True(report.BySemanticFamily["SUPERSEDES"] >= 4);
        Assert.True(report.BySemanticFamily["CORRECTS"] >= 3);
        Assert.DoesNotContain(report.Warnings, w => w.Contains("missing requested semantic family", StringComparison.Ordinal));
    }

    [Fact]
    public void Deterministic_grouped_splits_prevent_life_leakage()
    {
        var rows = SyntheticLife.GenerateRows(new SyntheticRunRequest(1827, People: 20, TurnsPerPerson: 120));
        var split = SyntheticLife.Split(rows, "life", seed: 99);

        var train = split.Train.Select(r => r.LifeId).ToHashSet();
        var validation = split.Validation.Select(r => r.LifeId).ToHashSet();
        var test = split.Test.Select(r => r.LifeId).ToHashSet();

        Assert.Empty(train.Intersect(validation));
        Assert.Empty(train.Intersect(test));
        Assert.Empty(validation.Intersect(test));
        Assert.Equal(split.GroupAssignments, SyntheticLife.Split(rows, "life", seed: 99).GroupAssignments);
    }

    [Fact]
    public void Duplicate_detection_reports_repeated_language()
    {
        var rows = SyntheticLife.GenerateRows(new SyntheticRunRequest(1827, People: 1, TurnsPerPerson: 120)).ToList();
        rows.Add(rows[0] with { ScenarioId = rows[0].ScenarioId + "-copy" });
        var scenario = new SyntheticScenario(
            "dup-test",
            new Provenance(SyntheticLife.GeneratorVersion, "dup-test", Difficulty.Obvious, 1827),
            SyntheticLife.Generate(new SyntheticRunRequest(1827)).Single().Person,
            Array.Empty<SyntheticTurn>(),
            rows);

        var report = SyntheticLife.Report(new[] { scenario });

        Assert.True(report.ExactDuplicateUtterances > 0);
        Assert.True(report.NormalizedDuplicateUtterances > 0);
    }

    private static IReadOnlyList<string> Fingerprint(IEnumerable<SyntheticCorpusRow> rows)
        => rows.Select(row => JsonSerializer.Serialize(row, new JsonSerializerOptions(JsonSerializerDefaults.Web))).ToList();

    private static SyntheticRunRequest RequestWith(string label, int count)
        => new(
            1827,
            People: Math.Max(1, count),
            TurnsPerPerson: 180,
            EventsPerPerson: 8,
            MinSemanticFamilies: new Dictionary<string, int> { [label] = count });
}
