namespace Companion.Core.Domain;

/// <summary>
/// The persisted shadow activity branch (Source 1b §4). SHADOW-ISOLATED: this table is new
/// and its migration touches no existing production procedure row. Nothing here affects
/// displayed replies, memory, tools, or V2 state.
///
/// Retention (§5) is explicit per row: hypotheses, guesses, answers, and activation text
/// can carry protected information, so a branch classified volatile-turn-only persists
/// its METADATA and deliberately loses its content on restart — restart-resume is
/// diagnosed as unavailable rather than silently weakening retention.
/// </summary>
public class ActivityBranchRecord
{
    public Guid Id { get; set; }

    // ---- isolation and identity ----
    public string UserId { get; set; } = default!;
    public Guid ConversationId { get; set; }
    public string InstanceId { get; set; } = default!;
    public string BranchId { get; set; } = default!;
    public string? ParentBranchId { get; set; }
    public int? BranchPointQuestionNumber { get; set; }

    /// <summary>ProductionObserved | CounterfactualNative | Simulated.</summary>
    public string BranchKind { get; set; } = default!;

    /// <summary>simulated | natural-observed | natural-counterfactual.</summary>
    public string Label { get; set; } = default!;

    // ---- definition and strategy ----
    /// <summary>The real Procedure row activation resolved to, when one exists.</summary>
    public Guid? ProcedureDefinitionId { get; set; }
    public string ActivityType { get; set; } = default!;
    public string StrategyVersion { get; set; } = default!;

    // ---- lifecycle and concurrency ----
    public string Lifecycle { get; set; } = default!;
    /// <summary>Optimistic concurrency: a conflicting write loses rather than overwrites.</summary>
    public int Version { get; set; }
    public int QuestionLimit { get; set; }
    public int CurrentQuestionNumber { get; set; }

    // ---- moves and bindings ----
    /// <summary>JSON: the selected/displayed moves with dispositions and renderer identity.</summary>
    public string MovesJson { get; set; } = "[]";
    /// <summary>JSON: answers bound to stable question keys, with their message ids.</summary>
    public string AnswerBindingsJson { get; set; } = "[]";
    /// <summary>JSON: hypotheses and exclusions — protected content when retention says so.</summary>
    public string? HypothesesJson { get; set; }
    public string? FinalGuess { get; set; }
    public bool? FinalGuessCorrect { get; set; }

    // ---- idempotency ----
    /// <summary>JSON array of applied idempotency keys; a duplicate returns the existing
    /// transition instead of applying twice.</summary>
    public string AppliedKeysJson { get; set; } = "[]";

    // ---- provenance, privacy, lifecycle timestamps ----
    public string? ActivationEvidence { get; set; }
    /// <summary>full | no_training | no_telemetry_text | volatile_turn_only.</summary>
    public string Retention { get; set; } = "no_training";
    /// <summary>True when content was withheld from persistence to honor retention.</summary>
    public bool ContentWithheld { get; set; }
    public DateTimeOffset ActivatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>Set when the branch reached a terminal state; drives age-based cleanup.</summary>
    public DateTimeOffset? TerminalAt { get; set; }
}
