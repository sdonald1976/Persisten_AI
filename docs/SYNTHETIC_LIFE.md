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

Valid split groups are `life`, `family`, `template`, and `verbalization`.

Naturalize a boundary-focused subset through an OpenAI-compatible endpoint:

```powershell
$env:OPENAI_API_BASE = "http://localhost:11434/v1"
$env:OPENAI_API_KEY = "local-or-provider-key"
$env:MODEL_NAME = "your-verbalizer-model"
dotnet run --project tools/Companion.Eval -- --only synthetic --seed 1827 `
  --people 300 --turns 180 --events 10 --naturalize --naturalize-events 120 `
  --paraphrases 3 --naturalize-concurrency 6 `
  --out artifacts/synthetic-life-phase4-naturalized.jsonl `
  --quarantine artifacts/synthetic-life-phase4-quarantine.jsonl `
  --structured-out artifacts/synthetic-life-phase4-structured.jsonl `
  --split-out artifacts/synthetic-life-phase4-naturalized-splits --split-group verbalization
```

The naturalization command fails closed per attempt: transport failures and conservative semantic
validation failures are written only to the quarantine artifact. It selects the audited decision
boundaries first (`COEXIST`/`SUPERSEDES`, then `REFINES`, `UNCERTAIN`, and `CORRECTS`) and caps
trivial `DUPLICATE` rows. The expected label and canonical before/after states are copied from the
structured event and are never model outputs.

## JSONL Provenance

Each `SyntheticCorpusRow` records:

- life/scenario identity: `lifeId`, `scenarioId`, `personId`, `seed`, `turn`;
- source identity: `generator`, `source`, `family`, `eventId`, `templateFamilyId`, `structureKey`;
- hidden state: `canonicalStateBefore`, `canonicalStateAfter`, `previousFact`, `currentFact`;
- target facts: `candidateFacts`, `affectedFacts`;
- temporal metadata: `permanent`, `temporalScope`, `eventDistance`, `eventDistanceBucket`;
- labels: `expectedLabel`, `expectedMemoryOperation`, `difficulty`;
- naturalization: original and naturalized utterances, `verbalizationGroupId`, verbalizer/model,
  attempt seed, validation status, validation reason, and `trustedForTraining`;
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

Naturalized runs additionally report exact and normalized uniqueness, paraphrases per structured
event, diversity by label/scenario family/verbalization group, validation outcomes/reasons, and
counts for each targeted decision boundary. These diagnostics are intentionally lightweight and
dependency-free.

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

## Phase 4 checkpoint (2026-08-19): the corpus, frozen before its evaluation

Two sessions produced this, and the seam between them is worth recording. The generator fixes,
the naturalization pipeline, the validator, the tests and the structured base corpus are
dev_automate's (Codex), left uncommitted in the working tree when its session stopped at 06:21
needing a live model endpoint. The naturalization run itself, the audits below and this commit
are the evaluator session's. Everything here was written **before** any training result on this
corpus was read; evaluation findings belong to the next section when it exists, not to this one.

**What Codex completed**, verified against the data rather than its notes:

- The three recorded generator defects are fixed upstream, each pinned by a test:
  `establish-fact` only populates a previously-unset slot; value selection is state-aware and
  `EnsureSemanticallyValid` throws on value-identical non-DUPLICATE events (0 such rows in the
  shipped corpus, against 1,507 in phase 3); split axes `life`/`family`/`template`/
  `verbalization` all keep groups disjoint. The redundant phase-3 artifacts are deleted.
- The naturalization pipeline end to end: boundary-prioritized selection with a DUPLICATE cap,
  style-seeded prompts, the conservative validator with per-reason quarantine, full provenance
  (verbalizer, model, seed, deterministic and naturalized utterance, status, reason, group id),
  diversity diagnostics, CLI flags. 994 tests green with all of it.
- `artifacts/synthetic-life-phase4-structured.jsonl` — 1,000 events over 100 lives — and its
  life-grouped splits (0 of 100 lives straddle; the phase-3 split defect is fixed in the data,
  not just the code). The artifact is byte-reproducible from the generator: every substantive
  field matches a fresh run, so it is NOT committed; the command below regenerates it.

**What the evaluator session completed**: the run Codex could not do. Executed with the shipped
pipeline unmodified:

```powershell
dotnet run --project tools/Companion.Eval -- --only synthetic --seed 1827 `
  --people 100 --turns 180 --events 10 --naturalize --naturalize-events 120 `
  --paraphrases 3 --naturalize-concurrency 2 `
  --verbalizer-url http://localhost:11434/v1 --verbalizer-model qwen2.5:3b-instruct `
  --out artifacts/synthetic-life-phase4-naturalized.jsonl `
  --quarantine artifacts/synthetic-life-phase4-quarantine.jsonl `
  --structured-out <scratch> --split-out artifacts/synthetic-life-phase4-naturalized-splits `
  --split-group verbalization
```

The verbalizer is `qwen2.5:3b-instruct` because the 7B could not allocate under the machine's
memory pressure at run time — a quality cost the quarantine numbers price in. The LLM sampling
is unseeded (temperature 0.95), so unlike the structured corpus **this artifact is not
reproducible**. `artifacts/` is gitignored for storage reasons, so
`synthetic-life-phase4-naturalized.jsonl` and `synthetic-life-phase4-quarantine.jsonl` live
only on this machine — copy them before wiping it, because no command regenerates them.

**Corpus statistics.** 120 structured events selected (boundary-first: 213 attempts on
COEXIST-vs-SUPERSEDES, 51 REFINES-vs-DUPLICATE/CORRECTS, 48 UNCERTAIN-vs-actionable, 33
SUPERSEDES-vs-CORRECTS), 360 attempts, **189 accepted, 171 quarantined** (86 value-not-preserved,
28 coexist-implies-replacement, 20 subject-shift, 17 supersession-not-explicit, 9+7+4 others).
Accepted rows: 87 verbalization groups over 62 lives and 26 template families; 0 exact and 1
normalized duplicate; every group holds paraphrases of exactly one structured event, nests inside
one life, and 0 groups straddle the shipped splits. Mean within-label lexical overlap halves
against the deterministic surface (Jaccard 0.367 → 0.173) — the linguistic diversity the corpus
exists to add is real.

**Defects found by the pre-training audit, recorded for the NEXT corpus iteration** — not fixed
now, because this corpus is frozen the moment its first training run is read:

- Collision-avoidance writes label markers into fact VALUES: `actually X` (28 % of CORRECTS
  events), `X with extra detail` (23 % of REFINES), `no longer X`, and routine values landing in
  `preference.coffee` ("coffee preference: weekend shifts"). Some are semantically invalid
  outright (correcting a value to `actually <same value>` corrects nothing); the rest leak the
  label into the rendered fact where a model can read it without reading the utterance.
- The validator's all-content-words rule over-rejects: **every** CONTRADICTS (15) and DUPLICATE
  (3) paraphrase died on it ("can't abide Perth" fails to contain "cannot"), so two of seven
  labels are absent from the accepted corpus. `now ` with a trailing space misses sentence-final
  "now"; the `" my "` subject-shift check fires on "my notebook", which the deterministic
  templates themselves contain.
- The validator's marker REQUIREMENTS make CORRECTS and UNCERTAIN fully marker-recoverable in
  accepted rows (a trivial marker rule scores them 16/16 and 18/18) — a milder relative of the
  template artifact, enforced by the quarantine itself. The prompt's own meta-language leaked
  into a handful of accepted utterances ("this isn't a change over time").
- 74 % of accepted rows keep the notebook-tag noise; a few address the listener with questions.

The evaluation methodology, gates and holdout are `SUPERSESSION_TASK.md`'s and are not restated
here.
