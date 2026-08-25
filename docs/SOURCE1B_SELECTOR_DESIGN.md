# Source 1b — amended selector design (supersedes §4 of the inspection)

The deterministic question bank is retained as **baseline and fallback only**.
It cannot identify an arbitrary open-domain object, and saying otherwise would
have made successful completion structurally impossible — the amendment below
adds explicit hypothesis state, a structured proposal selector, and a
final-guess lifecycle, with deterministic code authoritative throughout.

## A1. Runtime / strategy separation

The **runtime** owns persistence, lifecycle, identities, transactions, retries,
idempotency, isolation, and every authority decision. It knows nothing about
Twenty Questions. The **strategy** owns activity-specific state transitions and
move selection, and can never write to the ledger — it returns proposals and
results that the runtime applies.

```csharp
public interface IActivityStrategy
{
    string ActivityType { get; }                      // "twenty-questions"
    string Version { get; }                           // "1"

    StrategyState Initialize(ActivityDefinition definition);

    TransitionResult ApplyInput(ActivityInstance instance, ActivityInput input);

    SelectionOutcome SelectNext(ActivityInstance instance, StrategyState state);

    ValidationResult ValidateSelection(
        ActivityInstance instance, StrategyState state, ActivityMove proposedMove);

    CompletionResult EvaluateCompletion(ActivityInstance instance, StrategyState state);
}
```

Supporting types (final shapes):

```csharp
public sealed record ActivityDefinition(
    string ActivityType, string Version, Guid? ProcedureId,   // resolves a real Procedure row
    int QuestionLimit, string AskerParticipantId, string AnswererParticipantId);

/// Strategy-owned state, opaque to the runtime, persisted verbatim beside the ledger.
public sealed record StrategyState(
    IReadOnlyList<Hypothesis> Hypotheses,
    IReadOnlyList<EvidenceEntry> Evidence,
    IReadOnlyDictionary<string, string>? Extras = null);

public sealed record Hypothesis(string Label, double Confidence, bool Excluded = false,
    string? ExcludedByQuestionKey = null);

public sealed record EvidenceEntry(string QuestionKey, bool Answer,
    IReadOnlyList<string> Supports, IReadOnlyList<string> Excludes);

public sealed record ActivityInput(
    ActivityInputKind Kind,        // Answer | Correction | Abandon | GuessVerdict | Malformed
    string? BoundQuestionKey,      // stable identity, never "the most recent text"
    bool? BooleanAnswer,
    string? RawText,
    Guid MessageId,                // for idempotency
    DateTimeOffset At);

public sealed record TransitionResult(
    ActivityInstance Instance, StrategyState State,
    bool Applied, string? RejectionReason);          // e.g. "malformed-answer", "unknown-question-key"

public sealed record ActivityMove(
    ActivityMoveKind Kind,         // Question | Guess
    string StableKey, string Text,
    string? Rationale, IReadOnlyList<string>? Hypotheses, double? Confidence,
    MoveOrigin Origin);            // Deterministic | ModelProposal

public sealed record SelectionOutcome(
    ActivityMove? Move, string? FailureReason, IReadOnlyList<string> RejectedForRepeat);

public sealed record ValidationResult(bool Valid, string? Reason);

public sealed record CompletionResult(
    bool Complete, ActivityLifecycle? Lifecycle, string? Reason);
```

## A2. Deterministic authority (unchanged, now explicit)

Deterministic runtime code is authoritative over: question numbering, stable
question identities, answer-to-question binding, repeat rejection, participant
roles, question limit, lifecycle, malformed input, correction of previous
answers, completion, abandonment, and **whether a proposed question or guess is
legal**. No model mutates the ledger, binds an answer, increments a counter,
declares completion, or overrides authorization — a model only ever returns a
proposal that deterministic validation accepts or rejects.

## A3. The hybrid selector

**Deterministic baseline (`TwentyQuestionsBank`)** — typed coarse-to-fine bank
with stable keys. Provides reproducible tests, availability when the proposer
fails, a benchmark baseline, and guaranteed-legal questions. Its documented
limit, stated rather than hidden: **it narrows the physical/functional region
but cannot identify an arbitrary object and does not produce final guesses.**
It is a fallback, not the strategy.

**Structured proposal selector (`IActivityMoveProposer`)** — a reasoning model
returns exactly:

```json
{ "move": "question | guess", "stableKey": "material-primary",
  "text": "Is it primarily made of metal?",
  "rationale": "Splits the remaining physical-object hypotheses.",
  "hypotheses": ["a hand tool", "a kitchen implement"], "confidence": 0.63 }
```

The proposal is **untrusted**. `ValidateSelection` accepts or rejects it against
the same five checks plus proposal-specific ones (well-formed key, key not
already asked, guess only when the limit or confidence permits, hypotheses
consistent with recorded evidence). On rejection: bounded retries
(`MaxProposalRetries`, default 2), each rejection reason recorded, then the
deterministic fallback. The fallback's own failure is a diagnosed selection
failure, never an ordinary turn.

## A4. Hypothesis state and the final-guess lifecycle

`StrategyState` carries hypotheses (open-domain labels — *not* a finite
catalog), their confidence, exclusion with the question key that excluded them,
and per-answer evidence recording what each answer supported and excluded. Each
question carries its intended discrimination via the proposal's `hypotheses`
field, so "what was this question for" is recorded rather than inferred.

`ActivityMoveKind.Guess` is a first-class move: proposed, validated, rendered as
the activity's move, then confirmed by a `GuessVerdict` input.
`EvaluateCompletion` returns Completed on a correct guess or an exhausted limit,
Abandoned on an explicit abandon input. **"a dildo" is a reachable endpoint**:
hypotheses are model-proposed free-text labels validated for consistency, never
drawn from a hard-coded object list — the strategy represents them explicitly
instead of leaving them in conversational prose.

## A5. Selector trust boundary

```
ledger (authoritative, deterministic)
   │  minimum projection: asked keys + bound answers + live hypotheses + limit/number
   ▼
IActivityMoveProposer   ← separately configured endpoint/provider (NOT run-1c)
   │  untrusted structured proposal
   ▼
ValidateSelection (deterministic)  →  accept | reject(reason) | retry | fallback
   │  validated ActivityMove only
   ▼
runtime applies → ledger → V3 contributor → mouth gets the move + minimal frame
```

Boundary rules: run-1c is the **mouth** and never selects — the Language Organ
renders an already-selected move. The proposer is a separate interface with its
own model identity, prompt version, temperature, seed (when available), latency,
and raw structured proposal traced under retention rules. A future specialist
selector replaces the implementation without touching the runtime or the
ResponsePlan protocol. The projection handed to the proposer is minimal;
protected activity content never reaches a remote proposer without explicit
trusted-channel configuration (the existing `RendererTrustContext` rules apply).
Model-generated hypothesis text is `no_training` retention and never becomes
memory automatically.

## A6. Minimum model-call plan

| situation | model calls |
|---|---|
| deterministic-only mode (default in tests/CI) | **0** |
| normal shadow turn with proposer enabled | **1** proposal call per selection |
| proposal rejected | ≤ `MaxProposalRetries` (default 2) additional calls, then 0 (fallback) |
| answer binding, corrections, malformed input, completion | **0** — deterministic |
| replay of a recorded session | **0** — stored proposals are replayed |

Worst case per turn: 3 calls; expected: 1; replay and CI: 0.

## A7. Replay and evaluation

Deterministic replay means: stored inputs reproduce ledger transitions exactly;
recorded proposals reproduce validation decisions exactly; the deterministic
fallback reproduces selections exactly. It does **not** require the generative
proposer to reproduce identical wording — seeded determinism is not assumed and
is not claimed. Frozen tests use captured structured proposals plus adversarial
invalid ones (unknown key, repeated key, guess before it is legal, hypotheses
contradicting evidence, malformed JSON, injection-shaped text).

## A8. Remaining Source 1b deliverables after this design

Generic runtime; shadow-isolated store; explicit activation; transactional and
idempotent turn binding; the deterministic baseline; the proposal interface,
validator, and bounded-retry policy; hypothesis and final-guess state;
LifeRunner sessions; native V3 shadow assembly; diagnostics and forget support.
User-visible behavior unchanged; no CompactV3 reaches the mouth model.
