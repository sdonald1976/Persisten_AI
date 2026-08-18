# Synthetic Life and Evaluation

`tools/Companion.Eval` contains a deterministic synthetic-life generator for memory and conversation examples. It does not call Ava's production pipeline and does not train or replace any model. Its job is to create reproducible lives with hidden structured ground truth.

## Architecture

The generator has three separate layers:

1. `SyntheticState` stores canonical hidden facts and a timeline of active, inactive, temporary, historical, and other-person facts.
2. `ScenarioFamily` instances mutate that state. Each family defines the semantic label, expected memory operation, temporal scope, affected fact keys, and difficulty tags before language exists.
3. `TemplateRegistry` verbalizes the structured event using deterministic templates and a seeded conversational persona. The utterance keeps a `templateFamilyId` and can never change ground truth.

The provisional labels remain string data:

`COEXIST`, `SUPERSEDES`, `CORRECTS`, `REFINES`, `DUPLICATE`, `CONTRADICTS`, `UNCERTAIN`

That keeps this branch away from the specialized-model taxonomy seam.

## Scenario Composition

Each synthetic life chooses a seeded subset and order of reusable families rather than running a fixed progression. Current families include:

- establishing and adding compatible facts;
- single and multiple supersessions;
- correction and correction-of-correction;
- refinement and duplicate/paraphrase;
- contradiction;
- temporary state, expiry, temporary-becomes-permanent, and return-to-previous-state;
- other-person facts, self/other comparisons, ambiguous references, and delayed clarification;
- quoted speech and hypothetical questions.

Events are scheduled with seeded adjacent, short, medium, long, and very-long gaps. Noise turns mention existing synthetic state when possible.

## CLI

Generate a corpus:

```powershell
dotnet run --project tools/Companion.Eval -- --only synthetic --seed 1827 --people 1000 --turns 180 --out artifacts/synthetic-life.jsonl
```

Request minimum label coverage:

```powershell
dotnet run --project tools/Companion.Eval -- --only synthetic --seed 1827 --people 200 --events 10 --min-family SUPERSEDES=100 --min-family CORRECTS=50 --out artifacts/synthetic-life.jsonl
```

Request minimum difficulty coverage for reporting:

```powershell
dotnet run --project tools/Companion.Eval -- --only synthetic --seed 1827 --people 200 --min-difficulty multiple-candidate-memories=50 --min-difficulty another-person-contamination=50
```

Replay a person:

```powershell
dotnet run --project tools/Companion.Eval -- --only synthetic --seed 1827 --people 3 --turns 180 --replay life-0001
```

Write grouped train/validation/test splits:

```powershell
dotnet run --project tools/Companion.Eval -- --only synthetic --seed 1827 --people 200 --out artifacts/synthetic-life.jsonl --split-out artifacts/synthetic-splits --split-group life
```

Valid split groups are `life`, `family`, and `template`.

## JSONL Provenance

Each `SyntheticCorpusRow` records:

- life/scenario identity: `lifeId`, `scenarioId`, `personId`, `seed`, `turn`;
- source identity: `generator`, `source`, `family`, `eventId`, `templateFamilyId`, `structureKey`;
- hidden state: `canonicalStateBefore`, `canonicalStateAfter`, `previousFact`, `currentFact`;
- target facts: `candidateFacts`, `affectedFacts`;
- temporal metadata: `permanent`, `temporalScope`, `eventDistance`, `eventDistanceBucket`;
- labels: `expectedLabel`, `expectedMemoryOperation`, `difficulty`;
- evaluation fields for incumbent/model decisions and failure/disagreement export.

This keeps synthetic, curated, regression, captured, and adjudicated data distinguishable when corpora are mixed later.

## Diagnostics

`SyntheticLife.Report` and the CLI report:

- total lives, turns, and labeled events;
- count per semantic family, memory operation, difficulty dimension, scenario family, and distance bucket;
- unique scenario-family count;
- exact duplicate utterances;
- repeated normalized utterances;
- duplicate event structures;
- lexical diversity per row;
- warnings for requested coverage misses and extreme class imbalance.

These diagnostics are intentionally lightweight and dependency-free.

## Leakage Prevention

`SyntheticLife.Split` creates deterministic train/validation/test splits by group rather than by individual row. Use `life` to prevent one simulated person's timeline from leaking across splits, `family` to test unseen scenario families, or `template` to test unseen verbalization templates.

## Integration Guide

The specialized-model branch can consume this system without changing it:

1. Read the JSONL rows.
2. Train or evaluate on utterance plus structured fields such as `previousFact`, `candidateFacts`, `affectedFacts`, and `canonicalStateBefore`.
3. Write model outputs back to `specializedModelDecision`, `modelVersion`, and `modelConfidence`, or implement `ISyntheticDecisionProbe`.
4. Use `SyntheticEvaluation.Evaluate` and `FailuresAndDisagreements` to export adjudication candidates.

The next integration step should be a separate adapter that runs generated turns through Ava's normal API boundary using isolated per-user state. Keep that adapter beside this deterministic generator so tests and bulk generation still require no external LLM.

## End-To-End Ava Evaluation

Phase 3 adds an adapter layer around the deterministic generator:

```text
SyntheticScenario
  -> IAvaConversationClient
  -> /conversations + /chat
  -> /memories + /diagnostics/turns
  -> SyntheticCanonicalComparer
  -> survival report + failure artifacts
```

The selected application boundary is the same REST path normal callers use:

- `POST /conversations` creates the conversation.
- `POST /chat` sends each synthetic utterance.
- `GET /memories` inspects resulting memory state.
- `GET /diagnostics/turns` reads retrieval/trace information where available.

User identity is server-side via `IUserContext`/`User:Id`; requests never carry arbitrary user IDs. For HTTP end-to-end runs, launch Ava with a synthetic user id such as:

```powershell
$env:User__Id = "synthetic:1827:life-0000"
$env:Models__Provider = "Mock"
dotnet run --project src/Companion.Api
```

Then run one isolated life through that process:

```powershell
dotnet run --project tools/Companion.Eval -- --only synthetic --seed 1827 --people 1 --turns 120 --events 8 --ava-url http://localhost:5266 --ava-token <token> --e2e-failures artifacts/e2e.failures.jsonl
```

The CLI intentionally supports one HTTP life per Ava process for now, because the current API is a trusted single-user app. Multi-life HTTP evaluation should launch separate isolated Ava instances or add a future trusted test-only user-context switch.

## Survival Metrics

`SyntheticSurvivalReport` distinguishes:

- current canonical facts retained;
- missing current facts;
- stale superseded facts retained as current;
- another-person contamination;
- temporary-state promotion;
- correction failures;
- refinement failures.

`SyntheticFailureArtifact` preserves replay and adjudication context: seed, life id, turn, family, difficulty, canonical state before/after, candidate facts, affected facts, utterance, expected label/operation, resulting memory state, failure stage, trace id when available, and generator version.

Failure stages are conservative:

- `RetrievalFailure`
- `SemanticClassificationFailure`
- `MemoryOperationFailure`
- `PersistenceFailure`
- `SubjectResolutionFailure`
- `TemporalInterpretationFailure`
- `UnknownPipelineFailure`

Unavailable provenance remains null rather than fabricated.

## Naturalization

The deterministic utterance remains authoritative. Optional verbalizers sit after it:

```text
structured event -> deterministic utterance -> optional verbalizer -> validated utterance
```

Available implementations:

- `DeterministicTemplateVerbalizer`, always accepted and CI-safe.
- `LlmSyntheticVerbalizer`, using `IChatModel`, optional only.

`ConservativeSyntheticVerbalizationValidator` quarantines naturalized text when required values disappear or another-person facts appear to shift onto the user. Rejected/quarantined rows are marked `TrustedForTraining = false` and are excluded by `TrustedTrainingRows()`.

Naturalized rows retain `VerbalizationGroupId`, so grouped splits with `--split-group verbalization` keep paraphrases of the same structured event in the same split.
