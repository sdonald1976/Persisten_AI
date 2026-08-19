using Companion.Eval;
using Companion.Tests.Fixtures;
using System.Net.Http.Headers;
using Xunit;

namespace Companion.Tests;

public class SyntheticAvaEvaluationTests
{
    [Fact]
    public async Task Synthetic_users_are_isolated_in_end_to_end_adapter()
    {
        var scenarios = SyntheticLife.Generate(new SyntheticRunRequest(1827, People: 2, TurnsPerPerson: 60, EventsPerPerson: 3));
        var seenUsers = new List<string>();
        var evaluator = new SyntheticAvaEvaluator(s =>
        {
            var userId = SyntheticUserSafety.UserIdFor(s);
            seenUsers.Add(userId);
            return new FakeAvaClient(userId, s, rememberEverything: false);
        });

        foreach (var scenario in scenarios)
            await evaluator.EvaluateAsync(scenario);

        Assert.Equal(2, seenUsers.Distinct().Count());
        Assert.All(seenUsers, u => Assert.StartsWith(SyntheticUserSafety.Prefix, u, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Api_bound_adapter_drives_real_chat_boundary_against_isolated_test_host()
    {
        var scenario = SyntheticLife.Generate(new SyntheticRunRequest(
            1827,
            People: 1,
            TurnsPerPerson: 24,
            EventsPerPerson: 2)).Single();
        var userId = SyntheticUserSafety.UserIdFor(scenario);
        using var factory = CompanionApiFactory.ForUser(userId);
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CompanionApiFactory.Token);
        var evaluator = new SyntheticAvaEvaluator(_ => new HttpAvaConversationClient(http, userId));

        var result = await evaluator.EvaluateAsync(scenario, new SyntheticAvaEvaluationOptions(ThroughTurn: 24));

        Assert.Equal(userId, SyntheticUserSafety.UserIdFor(scenario));
        Assert.NotEmpty(result.Turns);
        Assert.NotNull(result.Survival);
    }

    [Fact]
    public void Cleanup_guard_rejects_normal_users()
    {
        Assert.Throws<InvalidOperationException>(() => SyntheticUserSafety.EnsureSyntheticUser("demo-user"));
        SyntheticUserSafety.EnsureSyntheticUser("synthetic:1827:life-0001");
    }

    [Fact]
    public void Canonical_comparison_detects_missing_current_fact()
    {
        var row = Row("COEXIST");

        var result = SyntheticCanonicalComparer.Compare(row, Array.Empty<AvaMemoryRecord>(), Array.Empty<AvaRetrievedMemory>());

        Assert.Contains(result.Failures, f => f.Kind == "missing_current_fact");
    }

    [Fact]
    public void Canonical_comparison_detects_stale_superseded_fact()
    {
        var row = Row("SUPERSEDES");
        var stale = new AvaMemoryRecord("1", row.PreviousFact!.Value, "Active");

        var result = SyntheticCanonicalComparer.Compare(row, new[] { stale }, Array.Empty<AvaRetrievedMemory>());

        Assert.Contains(result.Failures, f => f.Kind == "stale_superseded_fact");
    }

    [Fact]
    public void Canonical_comparison_detects_another_person_contamination()
    {
        var row = Rows(new SyntheticRunRequest(
            1827,
            People: 4,
            TurnsPerPerson: 120,
            EventsPerPerson: 8,
            MinSemanticFamilies: new Dictionary<string, int> { ["COEXIST"] = 4 }))
            .First(r => r.ExpectedMemoryOperation == "STORE_OTHER_SUBJECT");
        var contaminated = new AvaMemoryRecord("1", $"The user likes {row.CurrentFact.Value}.", "Active");

        var result = SyntheticCanonicalComparer.Compare(row, new[] { contaminated }, Array.Empty<AvaRetrievedMemory>());

        Assert.Contains(result.Failures, f => f.Kind == "foreign_person_contamination" &&
            f.Stage == FailureStage.SubjectResolutionFailure);
    }

    [Fact]
    public void Canonical_comparison_detects_temporary_state_promotion()
    {
        var row = Rows(new SyntheticRunRequest(
            1827,
            People: 2,
            TurnsPerPerson: 120,
            EventsPerPerson: 8,
            MinSemanticFamilies: new Dictionary<string, int> { ["UNCERTAIN"] = 1 }))
            .First(r => r.ExpectedMemoryOperation == "DO_NOT_PROMOTE");
        var promoted = new AvaMemoryRecord("1", row.CurrentFact.Value, "Active");

        var result = SyntheticCanonicalComparer.Compare(row, new[] { promoted }, Array.Empty<AvaRetrievedMemory>());

        Assert.Contains(result.Failures, f => f.Kind == "temporary_state_promoted" &&
            f.Stage == FailureStage.TemporalInterpretationFailure);
    }

    [Fact]
    public void Correction_and_refinement_failures_are_distinguished()
    {
        var correction = Row("CORRECTS");
        var refinement = Row("REFINES");

        var correctionResult = SyntheticCanonicalComparer.Compare(
            correction,
            new[] { new AvaMemoryRecord("old", correction.PreviousFact!.Value, "Active") },
            Array.Empty<AvaRetrievedMemory>());
        var refinementResult = SyntheticCanonicalComparer.Compare(
            refinement,
            Array.Empty<AvaMemoryRecord>(),
            Array.Empty<AvaRetrievedMemory>());

        Assert.Contains(correctionResult.Failures, f => f.Kind == "correction_not_applied");
        Assert.Contains(refinementResult.Failures, f => f.Kind == "refinement_not_merged");
    }

    [Fact]
    public void Failure_stage_does_not_blame_classifier_when_retrieval_failed()
    {
        var row = Row("SUPERSEDES");

        var result = SyntheticCanonicalComparer.Compare(row, Array.Empty<AvaMemoryRecord>(), Array.Empty<AvaRetrievedMemory>());

        Assert.Contains(result.Failures, f => f.Kind == "previous_memory_not_retrieved" &&
            f.Stage == FailureStage.RetrievalFailure);
        Assert.DoesNotContain(result.Failures, f => f.Stage == FailureStage.SemanticClassificationFailure);
    }

    [Fact]
    public void Unavailable_provenance_remains_null_in_failure_artifacts()
    {
        var scenario = SyntheticLife.Generate(new SyntheticRunRequest(
            1827,
            People: 1,
            TurnsPerPerson: 120,
            EventsPerPerson: 8,
            MinSemanticFamilies: new Dictionary<string, int> { ["SUPERSEDES"] = 1 })).Single();
        var row = scenario.Examples.First(r => r.ExpectedLabel == "SUPERSEDES");
        var failure = new CanonicalFailure("previous_memory_not_retrieved", FailureStage.RetrievalFailure, row.CurrentFact, null);
        var turn = new SyntheticAvaTurnResult(row.Turn, row.Utterance, row.ScenarioId,
            new AvaTurnResult("", null, Array.Empty<AvaRetrievedMemory>()), null);

        var artifact = SyntheticFailureArtifact.From(scenario, turn, failure, Array.Empty<AvaMemoryRecord>());

        Assert.Null(artifact.TraceId);
    }

    [Fact]
    public void Failure_artifacts_serialize_and_round_trip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"synthetic-failures-{Guid.NewGuid():N}.jsonl");
        var scenario = SyntheticLife.Generate(new SyntheticRunRequest(1827, People: 1, TurnsPerPerson: 80, EventsPerPerson: 4)).Single();
        var row = scenario.Examples.First();
        var artifact = new SyntheticFailureArtifact(
            scenario.Person.Id, scenario.Provenance.Seed, row.Turn, row.ScenarioId, row.Family,
            row.Difficulty, row.CanonicalStateBefore, row.CanonicalStateAfter,
            row.CandidateFacts, row.AffectedFacts, row.Utterance, row.ExpectedLabel,
            row.ExpectedMemoryOperation, "missing_current_fact", FailureStage.UnknownPipelineFailure,
            Array.Empty<AvaMemoryRecord>(), null, row.Generator);

        try
        {
            SyntheticFailureArtifacts.WriteJsonl(path, new[] { artifact });
            var read = SyntheticFailureArtifacts.ReadJsonl(path);
            Assert.Single(read);
            Assert.Equal(artifact.ScenarioId, read[0].ScenarioId);
            Assert.Equal(artifact.FailureStage, read[0].FailureStage);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Naturalizer_cannot_modify_canonical_truth()
    {
        var row = Row("REFINES");
        var changed = row.ApplyVerbalization(new SyntheticVerbalization(
            row.Utterance,
            row.Utterance + " I mean it.",
            "test",
            "accepted",
            true));

        Assert.Equal(row.CanonicalStateBefore, changed.CanonicalStateBefore);
        Assert.Equal(row.CanonicalStateAfter, changed.CanonicalStateAfter);
        Assert.Equal(row.ExpectedLabel, changed.ExpectedLabel);
        Assert.Equal(row.ExpectedMemoryOperation, changed.ExpectedMemoryOperation);
    }

    [Fact]
    public void Paraphrases_remain_in_same_split_group_as_source()
    {
        var row = Row("REFINES");
        var paraphrase = row.ApplyVerbalization(new SyntheticVerbalization(
            row.Utterance,
            row.Utterance + " Paraphrased.",
            "test",
            "accepted",
            true));

        var split = SyntheticLife.Split(new[] { row, paraphrase }, "verbalization", seed: 5);

        Assert.Single(split.GroupAssignments);
        Assert.Equal(
            split.GroupAssignments[row.VerbalizationGroupId!],
            split.GroupAssignments[paraphrase.VerbalizationGroupId!]);
    }

    [Fact]
    public void Rejected_naturalizations_cannot_enter_trusted_training_output()
    {
        var row = Row("REFINES");
        var validator = new ConservativeSyntheticVerbalizationValidator();
        var verdict = validator.Validate(row, "Something unrelated about bananas.");
        var rejected = row.ApplyVerbalization(new SyntheticVerbalization(
            row.Utterance,
            "Something unrelated about bananas.",
            "test",
            verdict.Status,
            verdict.TrustedForTraining,
            verdict.Reason));

        Assert.False(rejected.TrustedForTraining);
        Assert.Empty(new[] { rejected }.TrustedTrainingRows());
    }

    [Fact]
    public async Task Deterministic_replay_through_turn_uses_same_synthetic_history()
    {
        var request = new SyntheticRunRequest(1827, People: 2, TurnsPerPerson: 120, EventsPerPerson: 5);
        var original = SyntheticLife.Generate(request).Single(s => s.Person.Id == "life-0001");
        var replay = SyntheticLife.Replay(1827, "life-0001", request);
        var through = original.Turns.First(t => t.Relevant).Turn;
        var evaluator = new SyntheticAvaEvaluator(s => new FakeAvaClient(SyntheticUserSafety.UserIdFor(s), s, rememberEverything: false));

        var result = await evaluator.EvaluateAsync(replay, new SyntheticAvaEvaluationOptions(ThroughTurn: through));

        Assert.Equal(original.Turns.Where(t => t.Turn <= through).Select(t => t.Utterance),
            replay.Turns.Where(t => t.Turn <= through).Select(t => t.Utterance));
        Assert.True(result.TurnsEvaluated >= through);
    }

    [Fact]
    public void Fast_deterministic_mode_still_requires_no_ava_or_llm()
    {
        var rows = SyntheticLife.GenerateRows(new SyntheticRunRequest(1827, People: 5, TurnsPerPerson: 80));

        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.Equal("deterministic-template", r.VerbalizerId));
    }

    [Fact]
    public async Task Naturalization_pipeline_tracks_groups_provenance_and_quarantine()
    {
        var rows = SyntheticLife.GenerateRows(new SyntheticRunRequest(1827, People: 12, TurnsPerPerson: 180, EventsPerPerson: 10));
        var result = await SyntheticNaturalizationPipeline.RunAsync(rows, new AlternatingVerbalizer(),
            new SyntheticNaturalizationRequest(91, MaxStructuredEvents: 20, ParaphrasesPerEvent: 2, MaxConcurrency: 2));

        Assert.Equal(20, result.StructuredEvents);
        Assert.Equal(40, result.Attempted);
        Assert.NotEmpty(result.Accepted);
        Assert.NotEmpty(result.Quarantined);
        Assert.All(result.Accepted.Concat(result.Quarantined), row =>
        {
            Assert.NotNull(row.DeterministicUtterance);
            Assert.NotNull(row.VerbalizationGroupId);
            Assert.NotNull(row.VerbalizationSeed);
            Assert.Equal("test-naturalizer", row.VerbalizerId);
        });
        Assert.Equal(2, result.Diagnostics.Overall.ParaphrasesPerStructuredEvent);
    }

    [Fact]
    public void Naturalization_selection_prioritizes_boundaries_and_caps_duplicates()
    {
        var rows = SyntheticLife.GenerateRows(new SyntheticRunRequest(1827, People: 60, TurnsPerPerson: 180, EventsPerPerson: 12));
        var selected = SyntheticNaturalizationPipeline.SelectStructuredEvents(rows,
            new SyntheticNaturalizationRequest(91, MaxStructuredEvents: 70));

        Assert.True(selected.Count(r => r.ExpectedLabel == "DUPLICATE") <= 5);
        Assert.True(selected.Count(r => r.ExpectedLabel is "COEXIST" or "SUPERSEDES") > selected.Count / 2);
    }

    private static SyntheticCorpusRow Row(string label)
        => Rows(new SyntheticRunRequest(
            1827,
            People: 6,
            TurnsPerPerson: 180,
            EventsPerPerson: 10,
            MinSemanticFamilies: new Dictionary<string, int> { [label] = 2 }))
        .First(r => r.ExpectedLabel == label && (label != "SUPERSEDES" || r.ExpectedMemoryOperation == "REPLACE"));

    private static IReadOnlyList<SyntheticCorpusRow> Rows(SyntheticRunRequest request)
        => SyntheticLife.GenerateRows(request);

    private sealed class AlternatingVerbalizer : ISyntheticUtteranceVerbalizer
    {
        public string Id => "test-naturalizer";

        public Task<SyntheticVerbalization> VerbalizeAsync(SyntheticCorpusRow row, CancellationToken ct = default)
        {
            var accepted = row.VerbalizationSeed % 2 == 0;
            return Task.FromResult(new SyntheticVerbalization(
                row.DeterministicUtterance ?? row.Utterance,
                $"{row.DeterministicUtterance ?? row.Utterance} phrasing {row.VerbalizationSeed}",
                Id,
                accepted ? "accepted" : "quarantined",
                accepted,
                accepted ? null : "test quarantine",
                "fixture-model",
                row.VerbalizationSeed));
        }
    }

    private sealed class FakeAvaClient : IAvaConversationClient
    {
        private readonly SyntheticScenario _scenario;
        private readonly bool _rememberEverything;
        private readonly List<AvaMemoryRecord> _memories = new();

        public FakeAvaClient(string userId, SyntheticScenario scenario, bool rememberEverything)
        {
            UserId = userId;
            _scenario = scenario;
            _rememberEverything = rememberEverything;
            SyntheticUserSafety.EnsureSyntheticUser(userId);
        }

        public string UserId { get; }

        public Task<string> StartConversationAsync(string title, CancellationToken ct = default)
            => Task.FromResult(Guid.NewGuid().ToString());

        public Task<AvaTurnResult> SendAsync(string conversationId, string message, CancellationToken ct = default)
        {
            if (_rememberEverything)
            {
                var row = _scenario.Examples.FirstOrDefault(r => r.Utterance == message);
                if (row is not null && row.CurrentFact.Active)
                    _memories.Add(new AvaMemoryRecord(row.ScenarioId, row.CurrentFact.Value, "Active"));
            }
            return Task.FromResult(new AvaTurnResult("ok", null, Array.Empty<AvaRetrievedMemory>()));
        }

        public Task<IReadOnlyList<AvaMemoryRecord>> MemoriesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AvaMemoryRecord>>(_memories.ToList());

        public Task<IReadOnlyList<AvaTraceRecord>> RecentTracesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AvaTraceRecord>>(Array.Empty<AvaTraceRecord>());

        public Task CleanupAsync(CancellationToken ct = default)
        {
            SyntheticUserSafety.EnsureSyntheticUser(UserId);
            _memories.Clear();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
