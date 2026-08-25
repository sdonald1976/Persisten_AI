# Run-2 mouth readiness

Report only, 2026-08-25. No corpus generation, no training, no production
migration, no routing change. Written against measured state, not intent.

> **Headline.** The native Plan/3 producer is built, wired, and tested — and has
> **never once run outside a test process.** The live database contains **zero**
> `renderer.plan3` rows. The native corpus evidence available for Run-2 today is
> therefore **nil**, and every other readiness question is downstream of that
> one.

---

## 1. Current architecture state

### What remains isolated on `responseplan-v3`

30 commits ahead of `master`; 109 files, +29,334 lines. Nothing in it has ever
executed against real traffic. The isolated set:

| area | contents |
|---|---|
| protocol | `PlanV3`, `PlanV3Codec` (CompactV3), `response-plan-v3.schema.json`, `V2Translation`, `PlanParity` |
| producer | `PlanV3Builder` (native), `PlanV3Assembler`, `SourceRegistry` grants |
| contributors | tool + tool-authorization, user-preference + hosting-config, working-context, mood, familiarity, procedure/activity, perception stubs |
| supporting stores | `UserPreferences`, `ActivityBranches`, `CompanionMoodTransitions` (3 additive migrations) |
| privacy | signal tombstones, mood compaction, `MoodReplay` diagnostics |
| shadow | `V3ShadowEnvelope` + native section + assembly section, `ObservePlanOnly` |
| tests | 1399 in `Companion.Tests` + 31 prototype goldens |

### What must merge or be flagged to collect native evidence safely

Everything above must reach `master` to collect a single natural row, because
the native builder is invoked inside `Companion.RespondAsync`. The safety
argument is that the whole native path is already **doubly gated**:

1. `if (_rendererShadow.IsObserving || _rendererShadow.IsCanaryFor(userId))` —
   nothing native is built at all unless the shadow is on;
2. `RendererShadow.Enabled` is resolved from configuration **at composition
   time**, and defaults to false, registering `NullRendererShadow`.

So the flag is not a new mechanism to build; it already exists and is already
the rollback. What merging changes is only *what runs when the flag is on*.

Three things need checking before merge rather than after:

- **Native build cost on the turn path.** The build is synchronous inside the
  turn. It is pure in-memory work today, but it now also does **one store read
  per observed turn** (`IUserPreferenceStore.GetActiveAsync`) and, via the mood
  contributor's snapshot, one mood-log read. Both are indexed single-user
  queries; neither has been measured under load.
- **Exception containment.** The assembly is inside a `try/catch` that records a
  content-safe diagnostic, and a failure leaves the turn untouched. Verified by
  test, not by production.
- **Additive migrations.** Three new tables; all `CreateTable`/`AddColumn` only.
  `EmotionalSignal.Sentiment` and `.Valence` became **nullable**, which is a
  widening change and safe, but it is a schema change to an existing table and
  should be called out in the merge checkpoint rather than discovered.

### Confirmation of what stays authoritative

- **Plan/2 remains authoritative.** `CompactV2` is the only thing serialized to
  a renderer. `SerializeViaV3Hop` takes the `FromV2 → TranslateToV2` round trip
  and uses it **only when byte-identical to the direct serialization**, falling
  back with a log line otherwise. V3 is an intermediary that is permitted to
  produce identical bytes and nothing else.
- **Run-1c remains the displayed canary.** `IsCanaryFor(userId)` gates display;
  `CompactV3` is **never sent to any model**, on any path, in any test.
- **No routing change is proposed in this report.**

---

## 2. Real evidence inventory

### Native rows, by kind

| kind | count | notes |
|---|---|---|
| **naturally occurring** | **0** | live DB has 0 `renderer.plan3` rows (16 shadow rows total: 6 `renderer.plan2`, 6 `turn.intent`, 2 `knowledge.teaching`, 1 `context.reference`, 1 `gap.promotion`) |
| **simulated (live turn, test process)** | **~6 per suite run** | 5 tests drive `RespondAsync` end-to-end and assert a native row; working-context runs two turns |
| **constructed (unit assembly, no turn)** | **~110** | the per-source suites below |
| **translated-V2** | 804 | corpus golden tests — **excluded from native evidence by definition** |

**Only the "naturally occurring" line is eligible native corpus evidence, and it
is zero.** The simulated live-turn rows are integration proof that the path
works; they are six rows of scripted conversation and are not a corpus. The
constructed rows are authority proof and were never conversations at all.

### Counts by completed source

| source | tests | live-turn native row | wired at the real call site |
|---|---|---|---|
| working context (4a) | 11 | yes (2 turns) | **yes** |
| activity / procedure (1a/1b) | 11 | **no** | **NO — see blocker M3** |
| tools (2) | 21 | yes | yes |
| user preferences (3) | 26 | yes | yes |
| mood (4b) | 13 | yes | yes |
| familiarity (4c) | 10 | yes | yes |
| contribution boundary / grants | 29 | n/a | n/a |

### Multi-source compositions

**13** constructed assemblies combine two or more contributors. They cover:
user-preference × working-context (verbosity conflict), user-preference ×
hosting-config (authority conflict), familiarity × working-context (precedence),
familiarity × user-preference, mood × user-preference, and a three-authority
mixed-dimension case (warm + blunt + short).

Naturally occurring multi-source compositions: **0**, following from the row
count above.

---

## 3. Training readiness blockers

Separated deliberately, because the two lists have different owners.

### Genuine mouth/corpus blockers — these gate Run-2

**M1. Zero natural native rows.** No corpus can be built. Everything else is
secondary to collecting real traffic through the native producer. *(Merge +
shadow collection.)*

**M2. The native builder produces a thin plan.** `PlanV3Builder` sets only
act-derived verbosity on the register and leaves every other dimension at
canonical default; the contributors that fill the rest are shadow-gated and only
fire when their state exists. Before a corpus is worth freezing we need measured
evidence of **what a real native plan actually contains** — the distribution of
policies, categories, register dimensions and source counts across real turns.
That distribution is the thing that determines whether a corpus is trainable at
all, and today it is unknown.

**M3. The procedure/activity contributor is not wired.** Sources 1a/1b were
accepted for their own scope — runtime, store, LifeRunner volume — but
`ProcedureContributor` / `ActivityInstanceContributor` appear **nowhere** in
`Companion.RespondAsync`. Every other completed source joins the live assembly;
this one does not. Activity turns therefore produce native rows with no activity
content. This is a wiring gap in already-accepted work, not new scope, and it is
the one blocker fixable without any new design.

**M4. No native corpus tooling exists.** Run-1c's pipeline (`curate.py`,
`freeze.py`, `select_combination.py`, the candidate/curation/train JSONL chain)
is all Plan/2-shaped and lives on `master`. A Plan/3 equivalent — extraction from
`renderer.plan3` rows, stratification, freeze manifest — has not been written.

**M5. Held-out selection has no Plan/3 primitive vocabulary.**
`select_combination.py` detects primitives by **substring-matching Plan/2 prose**
("Ava made an error; Scott corrected her", "PALETTE", …). CompactV3 has no such
prose — it has typed categories and closed register tokens. The mechanical
holdout rule must be re-expressed over Plan/3 structure before it can select
anything, and it must be committed before evaluation exists, as before.

**M6. Latency of the native path on the turn is unmeasured.** See §1.

### Not blockers — future cognition limitations

These are recorded so they are not mistaken for gates. **None of them blocks
Run-2 unless a measured coverage requirement from §4 proves otherwise**, and
that measurement cannot exist until M1 is resolved.

- World ownership, Vision ingestion, Embodiment — all deferred by decision.
- General preference understanding: the interpreter is six closed patterns.
  Preferences appear in a corpus as *register values*, and a register value is
  the same whether a regex or a cognitive layer produced it.
- Additional tool metadata: audience, retention class, cancellation have no live
  producer. They affect *which* privacy behaviours can be sampled naturally, not
  whether the mouth can be trained.
- Mood energy voting; EmotionalSignal sentiment voting.
- Expression restrictions have no live capture path, so
  `must_not_express` items from `user-preference` will not occur naturally.
  **This one is closest to becoming a real blocker** — if §4's coverage floor for
  `must_not_express` cannot be met from natural rows, it must be met by curated
  rows or the floor must be lowered, explicitly.

---

## 4. Run-2 protocol and corpus

### The exact model-facing format

`PlanV3Codec.CompactV3` today. CRLF throughout; deterministic ordering; refuses
invalid plans and coaching-lint violations rather than emitting them.

```
[plan/3]
CONTROL (never quote, mention, or imitate)
  act = {act}
  question = {policy}[ -> {itemId}]
SAY (each item: convey the meaning, fresh words)
  [{id} {category}{owner}] {text}
ASK (end the reply with this)
OPTIONAL (use one only if it truly fits; silence is correct)
NEVER (do not assert, mention, or explain)
BACKGROUND (may shape tone; content must not surface)
STYLE
  warmth=… bluntness=… playful=… teasing=… skepticism=… intensity=… verbosity=… profanity=… mirror=…
```

Sections are ordered by policy, items within a section by descending priority
then ordinal id. Only the **closed** kebab category label appears; the open
`type` field never serializes, nor does `legacyStyle`. `CompactV3For` applies
recipient scoping first and refuses outright when an obligation is unauthorized.

Two properties to note for training. The register line is **always present and
always complete** — nine dimensions, every turn — where Plan/2's STYLE was
free prose. And the section headers carry their own instruction text, which the
model will see on every example; that repetition is deliberate but it is also
exactly the kind of thing a model can learn to echo, which §4's echo gate must
watch.

### Proposed strata and minimum coverage

Coverage floors are **proposed**, not frozen; they become binding at corpus
freeze. Percentages are of the final corpus.

| stratum | dimension | proposed floor |
|---|---|---|
| policy | `must_express` | ≥ 25% |
| | `ask_required` | ≥ 15% |
| | `may_express` | ≥ 15% |
| | `background_only` | ≥ 30% of rows contain ≥1 |
| | `must_not_express` | ≥ 8% |
| | `admit_unknown` | ≥ 5% |
| question mode | `question_forbidden` | ≥ 30% |
| | `ask_required` | ≥ 15% |
| | `may_ask` | ≥ 15% |
| register | each of 9 dimensions non-default | ≥ 5% each |
| | ≥ 2 dimensions non-default together | ≥ 20% |
| | mixed-valence pairs (warm+blunt, tender+plain, playful+skeptical) | ≥ 3% each |
| source composition | single-source | ≤ 50% |
| | 2 sources | ≥ 30% |
| | 3+ sources | ≥ 15% |
| privacy | restricted disclosure present | ≥ 5% |
| | non-`full` retention present | ≥ 10% |
| epistemic | `admit_unknown` / superseded item present | ≥ 8% |
| supersession | superseded item with a live replacement | ≥ 5% |
| tools | tool result present | ≥ 10% |
| | tool failure acknowledged | ≥ 3% |
| activities | activity question present | ≥ 8% |

**Natural vs simulated/curated proportion.** Target **≥ 60% natural**, ≤ 40%
simulated or curated, with a hard rule: **every stratum floor met entirely by
curated rows must be declared as such in the freeze manifest**, per stratum. A
corpus that hits every floor but hits half of them synthetically is a different
artifact from one that hits them naturally, and the manifest must make that
readable rather than leaving it to be inferred.

Run-1c's corpus for comparison: 730 training rows from 314 curation decisions.
Run-2 should not assume the same size is right; the size falls out of the strata
once the natural distribution (M2) is known.

### Teacher targets: generate, reject, review, dedupe, freeze

The Run-1 discipline transfers unchanged, because it is the part that worked.

1. **Generate.** A teacher proposes a candidate utterance from
   `CompactV3 + transcript window`. The teacher **never sees the gold**, and
   **never defines it** — it proposes.
2. **Reject mechanically first.** Deterministic gates before any human sees a
   candidate: palette leakage, plan echo (see below), question-discipline
   violation, must-state omission, control-section quotation, register
   contradiction. A candidate failing any gate is rejected without review.
3. **Review.** Every surviving candidate is a keep / edit / author decision,
   recorded per row as Run-1c's `curation-run1c.jsonl` does. Naturalness is
   judged by Scott, blind, never by the teacher and never by me.
4. **Deduplicate.** Exact and near-duplicate targets removed — Run-1c hit this
   for real (`r1c-epi-12` was byte-identical to `r1b-epi-04`), and the corpus
   must not contain the same sentence twice.
5. **Freeze.** A manifest hashing every input artifact, the base model and
   revision, the repo commit, and the per-stratum natural/curated split.
   Nothing trains before the hash exists; failures after the freeze are results.

### Preventing producer-authored coaching and plan echo

Three independent layers, two of which already exist:

- **At assembly (exists).** `PlanV3Codec.CoachingViolation` rejects authored
  coaching phrases from producer-authored sources, and `CompactV3` refuses to
  serialize a plan containing one. Tool and world text is exempt only because it
  is `Quoted` — data, not instruction.
- **At curation (to build).** A mechanical gate rejecting any target that
  quotes the CONTROL section, reproduces a section header, uses a register token
  as a word (`"verbosity"`, `"must_express"`, an item id), or reuses ≥ N
  contiguous tokens from any plan item. The last is the real echo test: an item's
  *meaning* must survive in fresh words, and the section header already says so.
- **At evaluation (to build).** A held-out echo metric measuring token overlap
  between plan items and generated replies, with a pre-declared ceiling.

---

## 5. Predeclared evaluation

Thresholds are proposed here and become binding at freeze. Every gate is
measured against **held-out** plans the model never trained on.

### Plan/3 fidelity

| metric | gate |
|---|---|
| must-state completeness | ≥ 95% of `must_express` items conveyed |
| question discipline — `question_forbidden` | ≤ 2% violation (Run-1c held 2/113) |
| question discipline — `ask_required` | ≥ 95% asked, and asked **last** |
| `must_not_express` respected | **100%** — any breach is a stop condition |
| `background_only` non-surfacing | ≥ 98% |
| `admit_unknown` honoured | ≥ 95% |
| register fidelity | ≥ 85% per dimension; mixed pairs measured separately |
| palette leakage | ≤ 1% |
| plan echo | ≤ Run-1c's measured rate, and no regression |
| invented experience | **0 tolerated**; measured on a dedicated probe set |
| latency (p50 / p95) | no worse than Run-1c at equal packet size |

### Naturalness

Blind review against Run-1c, Scott only, same protocol as the Run-1c review:
frozen arm mapping, ordinal scoring, unblinded only after judgments are
recorded. **Multi-sample: 7 draws per fixture per arm** — Run-1c proved a single
draw measures luck.

### Held-out and unseen

- **Held-out compositions**: source combinations present individually in the
  corpus but never together, selected mechanically by the freeze manifest hash,
  committed before evaluation exists. Requires M5.
- **Post-training unseen family**: a primitive combination the corpus never
  contains, authored after freeze and before results, by the same mechanical
  rule.

### Over-specialization

Run-2 must not become a plan-shaped-input specialist. Measured by: ordinary
conversational turns with a minimal plan (act + register only) scored for
naturalness against Run-1c, and a **no-regression floor** — Run-2 must not be
worse than Run-1c on Plan/2-era fixtures rendered through their V3 translation.

### Deployment stop conditions

Any one of these stops promotion outright:

1. Any `must_not_express` breach.
2. Any invented experience.
3. Any privacy breach: protected text in a row, a plan rendered for an
   unauthorized recipient, or a restricted item surfacing.
4. Naturalness worse than Run-1c on blind review.
5. Question discipline worse than Run-1c.
6. Any gate above missed on the held-out set, unless the miss is declared and
   accepted **before** unblinding.
7. Latency regression beyond the declared budget on the target hardware.

---

## 6. Execution sequence

Each phase gates the next. No phase starts before its predecessor's exit
criterion is met.

**Phase A — merge / integration checkpoint.**
Wire the procedure/activity contributor (M3). Measure native build + contributor
read latency on the turn path (M6). Confirm the three migrations apply cleanly
to the live database with a backup taken first. Merge `responseplan-v3` to
`master` with `RendererShadow.Enabled` **off**.
*Exit: master green, flag off, production byte-identical, Run-1c canary
unchanged.*

**Phase B — native shadow collection.**
Flag on for Scott's user only. Collect naturally occurring `renderer.plan3` rows
across ordinary use. Report the measured native plan distribution (M2): policies,
categories, register dimensions, source counts, composition depth.
*Exit: enough natural rows to evaluate the §4 strata against reality — and an
honest statement of which floors natural traffic cannot reach.*

**Phase C — corpus build and freeze.**
Build the Plan/3 extraction and stratification tooling (M4) and the Plan/3
holdout vocabulary (M5). Generate, gate, review, dedupe. Declare per-stratum
natural/curated split. Freeze and hash.
*Exit: a freeze manifest committed before any training run.*

**Phase D — Run-2 training curve.**
Same recipe discipline as Run-1: QLoRA, exact-resume checkpointing, off-hours
only. A learning curve, not a single run.
*Exit: checkpoints and metrics reproduced from the frozen inputs.*

**Phase E — evaluation.** §5, in order, blind review last.

**Phase F — user-scoped canary.** Reversible, Scott only, Run-1c as the
immediate fallback, exactly as the Run-1c canary works today.

**Phase G — merge / quantize / GGUF / package.** **Only after promotion.**

---

## 7. Scope lock

### Deferred until after Run-2 — explicitly

World ownership and observations; Vision ingestion and typed observations;
Embodiment in any form; any additional cognitive source; general preference
understanding beyond the six closed patterns; expression-restriction capture;
tool audience / retention / cancellation producers; mood energy voting;
EmotionalSignal sentiment voting; per-conversation mood and relationship scope;
the mood-history rewrite question; multi-user isolation anywhere; CompactV3
protocol changes; any production routing change.

### Shortest credible path to the first Run-2 canary

**A → B → C → D → E → F.** No step is skippable, and the ordering is forced by
data dependency rather than preference:

- C cannot start before B, because the strata are calibrated against a
  distribution nobody has measured.
- B cannot start before A, because the native path does not execute on `master`.
- E's held-out selection must be committed during C, before any result exists.

### Work estimate, as phases and dependencies

| phase | shape of the work | dependency risk |
|---|---|---|
| A | small, known — one wiring fix, one measurement, one merge | **low.** The only unknown is turn-path latency |
| B | **elapsed-time bound, not effort bound** — natural rows accrue at the rate real conversations happen | **the schedule's critical path.** Cannot be compressed by working harder; only by using the companion more |
| C | the largest build — extraction, stratification, gates, holdout vocabulary, review | **medium.** Review volume scales with corpus size, and review is Scott-only |
| D | recipe exists; off-hours GPU only | **low effort, high wall-clock.** Constrained by the no-training-during-work-hours rule |
| E | protocol exists from Run-1c; needs Plan/3 metric implementations | **medium.** Blind review is Scott-only and cannot be parallelized |
| F | mechanism exists and has run before | **low** |

The honest summary: **Phase B is the long pole and it is not an engineering
problem.** Everything before it is days of known work; everything after it is
bounded by review capacity and off-hours GPU time. The single highest-leverage
action available today is finishing Phase A so that Phase B can start
accumulating rows in the background while Phase C is built against them.

---

**Status: report only. Nothing was generated, trained, migrated, or routed.
Awaiting approval.**
