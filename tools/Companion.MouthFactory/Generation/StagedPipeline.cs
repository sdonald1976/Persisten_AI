using Companion.MouthFactory.Schema;
using Companion.MouthFactory.Validation;

namespace Companion.MouthFactory.Generation;

public sealed record StagedResult
{
    public required int Unsatisfiable { get; init; }
    public required int UnitsAttempted { get; init; }
    public required int Generated { get; init; }
    public required int Accepted { get; init; }
    public required int Rejected { get; init; }
    public required int ManualReview { get; init; }
    public required int Rounds { get; init; }
    public required string StopReason { get; init; }
    public required IReadOnlyDictionary<string, int> RejectionCodes { get; init; }

    /// <summary>Model loads: one per (round x role) actually exercised. The point of batching.</summary>
    public required int ModelLoads { get; init; }

    public required int WriterCalls { get; init; }
    public required int CriticCalls { get; init; }
    public required IReadOnlyList<string> DriftDetected { get; init; }
}

/// <summary>
/// Stage-batched generation: write a bounded batch with one model, then judge that batch with
/// each critic in turn.
///
/// The interleaved pipeline alternated writer and critics per candidate, which on a 12 GB card
/// meant unloading and reloading a model between almost every call: measured at 2.5x for two
/// models and ~9x per call with four. Batching changes ONLY the order in which the same calls
/// happen. Every model-identity rule still applies — the writer is still barred from judging,
/// and the critics are still independently configured. Scheduling is not a licence.
///
/// The reason this is infrastructure rather than a speed hack: a candidate now outlives the
/// process that wrote it, so it must be durable, identity-checked and resumable at every
/// boundary. That is what <see cref="CandidateStore"/> exists for.
/// </summary>
public sealed class StagedPipeline(
    ITargetSource targets,
    CandidateStore candidates,
    RowStore rows,
    Deduplicator dedup,
    IReadOnlyList<string> requiredCritics)
{
    public async Task<StagedResult> RunAsync(
        IReadOnlyList<ScenarioTruth> scenarios, PipelineOptions options,
        int batchSize = 64, CancellationToken ct = default)
    {
        var byId = scenarios.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var rejectionCodes = new Dictionary<string, int>(StringComparer.Ordinal);
        var drift = new List<string>();
        int unsatisfiable = 0, units = 0, generated = 0, writerCalls = 0, criticCalls = 0;
        int loads = 0, rounds = 0;
        var stopReason = "complete";

        void Reject(string code) => rejectionCodes[code] = rejectionCodes.GetValueOrDefault(code) + 1;

        // Work list: every (scenario, variant) not already durably known.
        var queue = new List<(ScenarioTruth Scenario, int Variant)>();
        foreach (var scenario in scenarios)
        {
            var sat = ScenarioSatisfiability.Check(scenario);
            if (!sat.Satisfiable)
            {
                unsatisfiable++;
                continue;
            }
            for (var v = 0; v < options.TargetsPerScenario; v++)
            {
                var existing = candidates.Find($"{scenario.Id}#{v}");
                if (existing is null)
                    queue.Add((scenario, v));
                // A durably stored candidate is NEVER regenerated - pending or terminal.
            }
        }

        var next = 0;
        while (true)
        {
            if (options.TargetAccepted is { } target
                && candidates.Count(CandidateState.Accepted) >= target)
            {
                stopReason = "target-reached";
                break;
            }
            if (options.MaxUnits is { } ceiling && units >= ceiling)
            {
                stopReason = "unit-ceiling";
                break;
            }

            var pending = candidates.Pending();
            var haveWork = next < queue.Count;
            if (!haveWork && pending.Count == 0)
                break;

            rounds++;
            var before = candidates.Count(CandidateState.GeneratedPendingCritics)
                         + units;

            // ---- STAGE 1: write a bounded batch, one model loaded once ------------------------
            if (haveWork)
            {
                var room = options.MaxUnits is { } cap ? Math.Max(0, cap - units) : int.MaxValue;
                var take = Math.Min(Math.Min(batchSize, queue.Count - next), room);
                if (take > 0)
                {
                    loads++;
                    for (var i = 0; i < take; i++)
                    {
                        var (scenario, variant) = queue[next++];
                        units++;
                        var outcome = await GenerateAsync(scenario, variant, ct);
                        writerCalls += outcome.Calls;
                        generated++;
                        if (outcome.Code is { } code)
                            Reject(code);
                    }
                }
            }

            // ---- STAGE 2: each critic over the batch awaiting it -----------------------------
            foreach (var role in requiredCritics)
            {
                var awaiting = candidates.AwaitingCritic(role);
                if (awaiting.Count == 0)
                    continue;
                loads++;
                foreach (var candidate in awaiting)
                {
                    if (!byId.TryGetValue(candidate.ScenarioId, out var scenario))
                    {
                        // Terminal, not skipped. Leaving it pending would keep the round
                        // loop alive with work it can never finish.
                        drift.Add($"{candidate.Id}: scenario no longer in this run");
                        candidates.Write(candidate with
                        {
                            State = CandidateState.Failed,
                            TerminalCode = "scenario-missing", UpdatedUtc = Now(),
                        });
                        continue;
                    }
                    if (CandidateStore.HashScenario(scenario) != candidate.ScenarioHash)
                    {
                        // The truth moved under a stored candidate. Judging it now would attach
                        // verdicts to a plan nobody wrote.
                        drift.Add($"{candidate.Id}: scenario truth changed since generation");
                        candidates.Write(candidate with
                        {
                            State = CandidateState.Failed, TerminalCode = "scenario-drift",
                            UpdatedUtc = Now(),
                        });
                        continue;
                    }

                    var verdicts = await targets.CriticiseOneAsync(role, scenario, candidate.Row.Target, ct);
                    criticCalls++;
                    // Persisted immediately: a crash inside a critic batch loses one verdict.
                    candidates.Write(candidate with
                    {
                        Verdicts = [.. candidate.Verdicts, verdicts],
                        UpdatedUtc = Now(),
                    });
                }
            }

            // A round that neither generated nor settled anything cannot make progress on
            // the next one either; ending is correct, spinning is not.
            var stalled = !haveWork
                          && candidates.Pending().All(c => c.MissingCritics.Count > 0)
                          && criticCalls == 0;
            _ = before;

            // ---- STAGE 3: combine verdicts deterministically ----------------------------------
            if (stalled)
            {
                stopReason = "stalled";
                break;
            }

            foreach (var candidate in candidates.Pending())
            {
                if (candidate.MissingCritics.Count > 0)
                    continue;
                var failed = candidate.Verdicts.Where(v => !v.Passed).ToList();
                var state = failed.Count == 0 ? CandidateState.Accepted : CandidateState.ManualReview;
                var settled = candidate with
                {
                    State = state,
                    TerminalCode = failed.Count == 0 ? null : failed[0].Code,
                    UpdatedUtc = Now(),
                };
                candidates.Write(settled);
                rows.Append(
                    state == CandidateState.Accepted ? Disposition.Accepted : Disposition.ManualReview,
                    settled.Row,
                    settled.Metadata with { Checks = ToChecks(settled) });
            }
        }

        return new StagedResult
        {
            Unsatisfiable = unsatisfiable,
            UnitsAttempted = units,
            Generated = generated,
            Accepted = candidates.Count(CandidateState.Accepted),
            Rejected = candidates.Count(CandidateState.Rejected) + candidates.Count(CandidateState.Failed),
            ManualReview = candidates.Count(CandidateState.ManualReview),
            Rounds = rounds,
            StopReason = stopReason,
            RejectionCodes = rejectionCodes,
            ModelLoads = loads,
            WriterCalls = writerCalls,
            CriticCalls = criticCalls,
            DriftDetected = drift,
        };
    }

    private sealed record GenOutcome(int Calls, string? Code);

    /// <summary>
    /// Write one candidate and store it durably BEFORE any critic runs. Deterministic checks
    /// still gate first and can terminate it without a single critic call.
    /// </summary>
    private async Task<GenOutcome> GenerateAsync(
        ScenarioTruth scenario, int variant, CancellationToken ct)
    {
        var id = $"{scenario.Id}#{variant}";
        var (plan, failure) = PlanConstruction.Build(scenario);
        if (plan is null)
        {
            candidates.Write(Terminal(id, scenario, variant, CandidateState.Failed, failure!.Code));
            return new GenOutcome(0, failure.Code);
        }

        TargetCandidate candidate = default!;
        List<CheckResult> checks = [];
        var attempts = 0;
        while (attempts < Math.Max(1, 3))
        {
            attempts++;
            candidate = await targets.WriteAsync(scenario, plan, variant * 97 + attempts, ct);
            if (candidate.Text is null)
                continue;
            checks = DeterministicChecks.Run(scenario, candidate.Text).ToList();
            if (!checks.Any(c => !c.Passed))
                break;
        }

        if (candidate.Text is null)
        {
            var code = candidate.FailureCode ?? "generation-failed";
            candidates.Write(Terminal(id, scenario, variant, CandidateState.Failed, code));
            return new GenOutcome(attempts, code);
        }

        var (row, metadata, renderFailure) = RowRendering.Render(
            scenario, plan, candidate.Text, variant, candidate.Provenance);
        if (row is null)
        {
            candidates.Write(Terminal(id, scenario, variant, CandidateState.Failed, "render-failed"));
            _ = renderFailure;
            return new GenOutcome(attempts, "render-failed");
        }

        var duplicate = dedup.Check(row.Id, candidate.Text);
        if (duplicate.IsDuplicate)
            checks.Add(new CheckResult
            {
                Name = "deduplication", Passed = false, Code = duplicate.Code,
                Detail = duplicate.Against, Kind = CheckKind.Deterministic,
            });

        var hard = checks.Where(c => c.Kind == CheckKind.Deterministic && !c.Passed).ToList();
        var withChecks = metadata! with { Checks = checks };

        if (hard.Count > 0)
        {
            var stored = Base(id, scenario, variant, row, withChecks) with
            {
                State = CandidateState.Rejected, TerminalCode = hard[0].Code, UpdatedUtc = Now(),
            };
            candidates.Write(stored);
            rows.Append(Disposition.Rejected, row, withChecks);
            return new GenOutcome(attempts, hard[0].Code);
        }

        // Durable, atomic, and BEFORE criticism. This is what makes stage two resumable.
        candidates.Write(Base(id, scenario, variant, row, withChecks) with
        {
            State = CandidateState.GeneratedPendingCritics, UpdatedUtc = Now(),
        });
        return new GenOutcome(attempts, null);
    }

    private PendingCandidate Base(
        string id, ScenarioTruth scenario, int variant,
        TrainingRow row, TrainingRowMetadata metadata)
        => new()
        {
            Id = id,
            ScenarioId = scenario.Id,
            ScenarioFamilyId = scenario.ScenarioFamilyId,
            FamilyId = scenario.FamilyId,
            VariantIndex = variant,
            ScenarioHash = CandidateStore.HashScenario(scenario),
            InputHash = CandidateStore.HashInput(row.Input),
            Row = row,
            Metadata = metadata,
            RequiredCritics = requiredCritics,
            State = CandidateState.GeneratedPendingCritics,
            UpdatedUtc = Now(),
        };

    private PendingCandidate Terminal(
        string id, ScenarioTruth scenario, int variant, CandidateState state, string? code)
        => new()
        {
            Id = id, ScenarioId = scenario.Id, ScenarioFamilyId = scenario.ScenarioFamilyId,
            FamilyId = scenario.FamilyId, VariantIndex = variant,
            ScenarioHash = CandidateStore.HashScenario(scenario), InputHash = "",
            Row = new TrainingRow
            {
                Id = id, System = "", Input = "", Target = "",
                FormatVersion = global::Companion.PlanV3.MouthPromptV4.FormatVersion,
            },
            Metadata = new TrainingRowMetadata
            {
                Id = id, ScenarioId = scenario.Id, ScenarioFamilyId = scenario.ScenarioFamilyId,
                FamilyId = scenario.FamilyId, Layer = scenario.Layer,
                SourceFamilyId = scenario.SourceFamilyId,
                Generation = new GenerationProvenance
                {
                    Role = "TargetWriter", Model = "-", Endpoint = "-", PromptVersion = "-",
                    Seed = scenario.Seed, Attempt = 0, PromptHash = "-",
                },
            },
            RequiredCritics = requiredCritics,
            State = state, TerminalCode = code, UpdatedUtc = Now(),
        };

    private static IReadOnlyList<CheckResult> ToChecks(PendingCandidate c)
        => [.. c.Metadata.Checks, .. c.Verdicts.Select(v => new CheckResult
        {
            Name = v.Role, Passed = v.Passed, Code = v.Code, Detail = v.Detail,
            Kind = CheckKind.Critic,
        })];

    private static string Now() => DateTimeOffset.UtcNow.ToString("O");
}
