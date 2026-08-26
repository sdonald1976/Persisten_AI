using Companion.MouthFactory.Export;
using Companion.MouthFactory.Schema;
using Companion.MouthFactory.Validation;

namespace Companion.MouthFactory.Generation;

public sealed record PipelineOptions
{
    public required string OutputDirectory { get; init; }
    public int TargetsPerScenario { get; init; } = 2;
    public int MaxAttempts { get; init; } = 3;
    public bool DryRun { get; init; }

    /// <summary>Rows per family for this run. Corpus size is configuration, never architecture.</summary>
    public IReadOnlyDictionary<string, int>? FamilyCounts { get; init; }
}

public sealed record PipelineResult
{
    public required int ScenariosBuilt { get; init; }
    public required int CandidatesGenerated { get; init; }
    public required int Accepted { get; init; }
    public required int Rejected { get; init; }
    public required int ManualReview { get; init; }
    public required IReadOnlyList<ScenarioTruth> Scenarios { get; init; }
    public required IReadOnlyList<(TrainingRow Row, TrainingRowMetadata Meta)> AcceptedRows { get; init; }
    public required IReadOnlyDictionary<string, int> RejectionCodes { get; init; }
}

/// <summary>
/// Scenario → plan → candidate targets → checks → disposition, resumably.
///
/// The evaluation ORDER is the design. Deterministic checks run first and can reject alone;
/// critics run last and, alone, only route to manual review. That ordering is what keeps a
/// language model's taste from quietly deciding which registers survive — the structural facts
/// about a row are settled before any opinion is asked for.
/// </summary>
public sealed class FactoryPipeline(
    RoleRouter roles,
    JobLedger ledger,
    RowStore store,
    Deduplicator dedup,
    ITargetSource targets)
{
    public async Task<PipelineResult> RunAsync(
        IReadOnlyList<ScenarioTruth> scenarios, PipelineOptions options, CancellationToken ct = default)
    {
        var accepted = new List<(TrainingRow, TrainingRowMetadata)>();
        var rejectionCodes = new Dictionary<string, int>(StringComparer.Ordinal);
        int generated = 0, acceptedCount = 0, rejectedCount = 0, manualCount = 0;

        void CountRejection(string code)
            => rejectionCodes[code] = rejectionCodes.GetValueOrDefault(code) + 1;

        foreach (var scenario in scenarios)
        {
            // 1. schema + Plan/4 + audience + render eligibility, before anything is generated.
            var (plan, failure) = PlanConstruction.Build(scenario);
            if (plan is null)
            {
                CountRejection(failure!.Code);
                rejectedCount++;
                ledger.Record(new LedgerEntry
                {
                    ScenarioId = scenario.Id, VariantIndex = 0, State = LedgerState.Failed,
                    FailureCode = failure.Code, Detail = failure.Detail,
                    CompletedAtUtc = Timestamp(),
                });
                continue;
            }

            for (var variant = 0; variant < options.TargetsPerScenario; variant++)
            {
                if (ledger.ShouldSkip(scenario.Id, variant))
                    continue;                                    // resumed: already terminal

                if (options.DryRun)
                {
                    generated++;
                    continue;
                }

                // Bounded retry: a teacher that broke the plan is asked again with a different
                // seed, up to MaxAttempts. Retrying with the SAME seed would reproduce the same
                // mistake, so the attempt number varies the draw. Deterministic checks decide
                // when to stop, which keeps the retry honest: it never lowers the bar.
                TargetCandidate candidate = default!;
                List<CheckResult> checks = [];
                DuplicateVerdict duplicate = new(false, null, null);
                var attempts = 0;
                while (attempts < Math.Max(1, options.MaxAttempts))
                {
                    attempts++;
                    candidate = await targets.WriteAsync(scenario, plan, variant * 97 + attempts, ct);
                    generated++;
                    if (candidate.Text is null)
                        continue;
                    checks = DeterministicChecks.Run(scenario, candidate.Text).ToList();
                    if (!checks.Any(c => !c.Passed))
                        break;
                }

                if (candidate.Text is null)
                {
                    CountRejection(candidate.FailureCode ?? "generation-failed");
                    rejectedCount++;
                    ledger.Record(Terminal(scenario, variant, LedgerState.Failed, candidate.FailureCode));
                    continue;
                }

                var (row, metadata, renderFailure) = RowRendering.Render(
                    scenario, plan, candidate.Text, variant, candidate.Provenance);
                if (row is null)
                {
                    CountRejection("render-failed");
                    rejectedCount++;
                    ledger.Record(Terminal(scenario, variant, LedgerState.Failed, "render-failed", renderFailure));
                    continue;
                }

                // 2-7. dedup runs once, on whatever survived the retry loop above.
                duplicate = dedup.Check(row.Id, candidate.Text);
                if (duplicate.IsDuplicate)
                    checks.Add(new CheckResult
                    {
                        Name = "deduplication", Passed = false, Code = duplicate.Code,
                        Detail = duplicate.Against, Kind = CheckKind.Deterministic,
                    });

                var hardFailures = checks
                    .Where(c => c.Kind == CheckKind.Deterministic && !c.Passed)
                    .ToList();

                // 8. critics — only for rows that already survived the mechanical pass.
                if (hardFailures.Count == 0)
                    checks.AddRange(await targets.CriticiseAsync(scenario, candidate.Text, ct));

                var criticFailures = checks
                    .Where(c => c.Kind == CheckKind.Critic && !c.Passed)
                    .ToList();

                var withChecks = metadata! with { Checks = checks };

                if (hardFailures.Count > 0)
                {
                    foreach (var f in hardFailures)
                        CountRejection(f.Code ?? f.Name);
                    rejectedCount++;
                    store.Append(Disposition.Rejected, row, withChecks);
                    ledger.Record(Terminal(scenario, variant, LedgerState.Rejected,
                        hardFailures[0].Code, hardFailures[0].Detail));
                }
                else if (criticFailures.Count > 0)
                {
                    // 9. A critic alone never discards a structurally sound row. It routes.
                    manualCount++;
                    store.Append(Disposition.ManualReview, row, withChecks);
                    ledger.Record(Terminal(scenario, variant, LedgerState.ManualReview,
                        criticFailures[0].Code, "critic disagreement"));
                }
                else
                {
                    acceptedCount++;
                    accepted.Add((row, withChecks));
                    store.Append(Disposition.Accepted, row, withChecks);
                    ledger.Record(Terminal(scenario, variant, LedgerState.Accepted, null));
                }
            }
        }

        return new PipelineResult
        {
            ScenariosBuilt = scenarios.Count,
            CandidatesGenerated = generated,
            Accepted = acceptedCount,
            Rejected = rejectedCount,
            ManualReview = manualCount,
            Scenarios = scenarios,
            AcceptedRows = accepted,
            RejectionCodes = rejectionCodes,
        };
    }

    private static LedgerEntry Terminal(
        ScenarioTruth scenario, int variant, LedgerState state, string? code, string? detail = null)
        => new()
        {
            ScenarioId = scenario.Id, VariantIndex = variant, State = state,
            FailureCode = code, Detail = detail, CompletedAtUtc = Timestamp(),
        };

    private static string Timestamp() => DateTimeOffset.UtcNow.ToString("O");
}

public sealed record TargetCandidate(
    string? Text, GenerationProvenance Provenance, string? FailureCode = null);

/// <summary>
/// Where targets and critic verdicts come from. Behind an interface so the whole pipeline runs
/// offline against fixtures — the tests never call a model and never download one.
/// </summary>
public interface ITargetSource
{
    Task<TargetCandidate> WriteAsync(
        ScenarioTruth scenario, global::Companion.PlanV3.PlanV3 plan, int variant,
        CancellationToken ct = default);

    Task<IReadOnlyList<CheckResult>> CriticiseAsync(
        ScenarioTruth scenario, string target, CancellationToken ct = default);
}
