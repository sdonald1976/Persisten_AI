# Specialist architecture audit (Phase 1–3, inspect-and-report)

**Status: inspection only. No training, no architectural change, no model swap.** Read-only.
Preserves commit `bd84d26c`, protocol `81c3a19a`, the Run-2.2 Mouth, and current deployment.

The short version: **the architecture you describe is already ~70% built.** A deterministic
authority boundary (`PlanV3Assembler`) already grants every privileged field; the Mouth is
already a trained, evaluated, authority-bounded specialist; a runtime seam, shadow recorder,
eval harness, and capture pipeline for specialists already exist (`docs/SPECIALIST_MODELS.md`,
`training/cognition/`). The general model's remaining cognitive authority is **narrower than the
brief assumes**. The real gaps are **(a) real labelled data** — captured volume is tiny today —
and **(b) adoption**: five specialists are built and measured but not promoted, all blocked on
data quantity, not on architecture.

---

## 1. Model / decision inventory

Legend — **Owner**: `det` deterministic code, `rule` regex/heuristic, `emb` embedding model,
`gen` general chat model (Stheno-8B), `role` a configured non-conversational LLM, `mouth`
run-2.2, `onnx` a specialist encoder. **Typed?**: is the output a typed value or free prose.
**Escape?**: can this component alter a field it does not own.

| # | Cognitive area / decision | Impl & owner | Inputs available | Output shape | Typed? | Model/provider | Escape? | Training data | Eval | Prod / shadow | Fallback | Replace feasible? |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 1 | **Command intent** (chat / recall / forget) | `TurnIntentClassifier` (`rule`, regex) | prompt text | `IntentKind` enum | yes | none | no | n/a | suite | prod | n/a | keep det |
| 2 | **Turn intent / move** (answer-question, correction, answers-open-question…) | `TurnUnderstanding` + `TurnIntentClassifier` (`rule`) | recent turns, prompt, retrieved count, bound-question | `TurnIntentState`, `ConversationMove` | yes | none (`UseLlmIntentParser` off) | no | borrowed CLINC150 15,250 | small | prod | rule is the impl | classifier candidate (low value) |
| 3 | **Memory retrieval** | `Retriever` (`emb` + `det` signals) | query, embeddings, keyword/recency/importance/project | ranked `RetrievalResult[]` | yes | `nomic-embed-text` | no | n/a (unsupervised) | signal tests | prod | keyword-only if embed down | keep; improve rerank (see #4) |
| 4 | **Memory relevance rerank** | `IMemoryReranker` → `LlmMemoryReranker` (`gen`/3B) OR `RuleBasedMemoryReranker` OR `CrossEncoderMemoryReranker` (`onnx`, if enabled) | query + candidate texts | reordered list | yes | qwen3b (a **generative model doing scoring**) | **relevance only** — cannot change facts/policy | borrowed + captured; cross-encoder built | P@1 0.917 R@3 1.000 (n=12) | rerank **off in prod** (`RerankMemories:false`); cross-encoder shadow-loadable | rule floor | **YES — best first candidate** |
| 5 | **Privacy: skip-derived-memory** | `IPrivacyClassifier` → `LlmPrivacyClassifier` (`role`=Safety/7B) + `RuleBasedPrivacyClassifier` floor | prompt text | bool (sensitive) | yes | Safety role, rule fallback | **advisory to a typed gate** | rule + captured | small | prod (rule floor always) | rule-based | hybrid; keep det floor |
| 6 | **Audience / disclosure / retention / tool-authorization** | `PlanV3Codec.ValidateForAudience` + `PlanV3Assembler` family-owner table (`det`) | plan items, recipient principals, trust context | `AudienceDecision`, typed policies | yes | **none — pure det** | **NO (this is the boundary)** | n/a | adversarial suite | prod (v3 path) | fatal error → replan/refuse, never drop | **keep det permanently** |
| 7 | **Epistemic state / uncertainty** | `ExpressionPolicy.admit_unknown` in plan; `ConceptKnowledge`/supersession (`det`) | concept store, supersession | typed plan items | yes | none | no | n/a | ADMIT hard-eval | prod | admit_unknown item | keep det |
| 8 | **Fact supersession** (stale vs current) | `FactSupersession` (`rule` wording heuristic); NLI (`onnx`) measured **worse** | fact pair | must_not_express + must_express | yes | rule; NLI rejected | no | DialogueNLI 310,110; CommitmentBank 250 | P 1.000 R 0.500 (rule beats NLI 0.462) | prod | rule | keep rule (NLI disproven) |
| 9 | **Social / emotional interpretation** | `MoodDetector` (`rule`, regex); Emotion ONNX **off, no weights** | user message | `MoodReading` | yes | none live | **suggests tone only** | GoEmotions available, not fetched | none live | shadow only when enabled | rule | classifier candidate (low risk, low value) |
| 10 | **Preference / relationship state** | `IPreferenceStore`, `RelationshipSnapshot`, `MoodContributor`/`FamiliarityContributor` (`det` over stored signals) | stored preferences, emotional-signal log | typed register contributions | yes | none | **register precedence only, assembler-gated** | captured signals | none | prod | det | keep det |
| 11 | **Curiosity generation** | `Reflector`/`SleepCycle` (`role`=Reflection) writes musings/curiosities post-turn | conversation, memories | stored `Curiosity` (text) | partly (text body) | Reflection role | post-turn only; **cannot touch displayed reply** | captured | none | prod (background) | deterministic greeter | keep; content-bounded |
| 12 | **Question policy** (none/optional/required) | plan field, set by planners/contributors (`det`) + executive planner may *decline* an optional | plan | `QuestionPolicy` enum | yes | det (+ planner on route) | planner: **decline only, re-validated** | reg-supplement | question-policy checks | prod | plan default | keep det + bounded planner |
| 13 | **Planning: semantic content selection** | `ResponsePlanner.BuildProductionPlan` + `PlanV3Builder.BuildNativePlan` (`det`); `IExecutivePlanner` (`role`=14B) on Stheno-free route only | intent, working ctx, memories, knowledge, tools | `ResponsePlan` / native plan/4 | yes | det; planner proposes typed items | planner: **may_express/admit_unknown only, assembler-gated** | reg-supplement | move/planner tests | prod (det); planner demo-user only | det plan stands | keep det + bounded planner |
| 14 | **Tool selection** | `ToolLoop` driven by `ToolPlanner` (`role`=3B); model text **never executes as a command** | compact planning ctx | typed tool calls | yes | qwen3b | **bounded: typed args, authorization separate** | n/a | tool-layer suite | prod | rule (no calls) | keep; small model correct |
| 15 | **Procedure / activity strategy** | `IRoamingPolicy` seam (`det` observation/ranked deliberation); no policy trained | structured observation | ranked actions | yes | none | no | none | none | seam only | heuristic | seam ready; no model yet |
| 16 | **Final language realization (the Mouth)** | run-2.2 adapter (`mouth`) on Stheno-free route; **Stheno (`gen`) on production route** | MouthPromptV4 (system packet + CompactV4 + transcript) | utterance | in→typed, out→prose | **run-2.2 adapter c0fe119d… (bd84d26c commit)** | **guards prevent escape** (see #18) | Run-2/2.1/2.2 frozen corpora | hard-eval, contract/hardset probes | mouth = demo-user; Stheno = everyone else | DeterministicMouth / honest-failure | already a specialist |
| 17 | **Reply generation (production route)** | `ReplyGenerator` over `IChatModel` (`gen`=Stheno) | rendered packet | reply text | no (prose) | **Stheno-8B** | **HIGH — see §3** | none (base) | naturalness only | prod (non-demo-user) | — | route replacement in progress |
| 18 | **Plan/render fidelity + guards** | `RendererShadowChecks` (`det`): plan-echo, question-policy, `epistemic-admission-absent`, `unauthorized-stance`, suppression, invented-experience | plan + reply | violation list; critical → fallback | yes | none | **NO (this is a guard)** | reg-supplement | 27 stance fixtures + suite | prod (mouth path critical) | deterministic fallback | keep det |
| 19 | **Reply gate (meaning)** | `LlmReplyGate` (`role`=Safety/7B), **Shadow mode** (records, changes nothing) | reply, prompt | allow/block + reason | yes | Safety 7B | enforce mode can replace reply; **currently shadow** | captured | none | shadow | fail-open | keep as veto, measure first |
| 20 | **Critics (corpus gate)** | faithfulness/adversarial/naturalness (`role`, factory-time only) | scenario + target | verdict | yes | qwen14b/qwen3-8b/qwen3b | **route to review, never auto-reject** | n/a | audited for refusal asymmetry | factory only | manual review | keep independent |
| 21 | **Fallback rendering** | `DeterministicMouth` (`det`) | plan/4 | utterance | yes | none | **NO** | n/a | route tests | prod (mouth fallback) | honest-failure const | keep det |
| 22 | **Learning / trace capture / dataset** | `CognitiveCapture` + `TurnRecords` + `ShadowComparisons`; `training/cognition/harvest.py` | real turns | labelled jsonl (review queue) | yes | none | no | **produces** the data | crossval harness | Capture **ON** in prod | n/a | the fuel supply |
| 23 | **Future vision** | interface boundary only (`IVisionModel`, optional) | image | text | — | llama3.2-vision (optional) | boundary only | n/a | n/a | off | n/a | out of scope |

---

## 2. Traced production path (user input → visible output)

Two routes exist today. The **Stheno-free route** (demo-user) already realises the target
architecture; the **production route** (everyone else) still ends in the general model.

```
ADMISSION            validate, resolve conversation+ownership, store user msg        [det]
  ↓
PROJECT RESOLUTION   resolve project reference; ambiguity ⇒ clarify (control flow)   [det]
  ↓
UNDERSTANDING        intent + conversational move                                    [rule]
  ↓
PRIVACY / ROLEPLAY   sensitive? in-character? ⇒ no durable memory this turn          [role+rule floor]
  ↓
CONTEXT ASSEMBLY     retrieve (emb+signals) → rerank → packet                        [emb + det]
  ↓
PRODUCTION PLAN      ResponsePlanner.BuildProductionPlan (act, acks, content auth,   [det]
                     epistemic, question)  → ResponsePlan
  ↓
NATIVE PLAN/4        PlanV3Builder.BuildNativePlan (typed, from state — never from    [det]
                     the v2 plan) ; ContributeAsync folds tool/frame contributions
  ↓
  AUTHORITY GATE     PlanV3Assembler: the ONLY grantor of must_express /             [det ← THE BOUNDARY]
                     must_not_express / ask_required / privileged reason-codes.
                     Unregistered source ⇒ background_only or rejected, diagnosed.
  ↓
  EXECUTIVE PLANNER  (Stheno-free route only) 14B proposes typed items; authority     [role, assembler-gated]
                     layer admits as may_express/admit_unknown only; re-validated.
  ↓
EXECUTION
  ├─ Stheno-free route:  MouthPromptV4 → run-2.2 → RendererShadowChecks (critical    [mouth + det guards]
  │                      guards; refusal/echo/admission → deterministic fallback)
  └─ production route:   ReplyGenerator → Stheno-8B → same guards in SHADOW only      [gen]  ← authority leak, §3
  ↓
REPLY GATE           LlmReplyGate (Safety 7B), SHADOW — records, changes nothing      [role, shadow]
  ↓
PERSIST + POST-TURN  store displayed reply; extraction, mood, reflection (post-turn, [det + role, cannot
                     cannot alter the displayed reply)                                alter reply]
  ↓
OBSERVABILITY        plan-fidelity + renderer-shadow rows; models.called ledger       [det, record-only]
```

`models.called` (the per-turn AsyncLocal ledger) makes "which models ran" a measured fact on
every turn. On the Stheno-free route it reads `conversation:0`.

---

## 3. Where a model can currently exceed its intended authority

Honest list, most-to-least severe. Only #1 is a live authority breach; the rest are bounded.

1. **The general model on the production route (#17) owns final wording with no plan/4 authority
   boundary in front of it.** The RendererShadowChecks guards run in **shadow** there, not
   critical — so on non-demo-user turns Stheno can (and did, 2026-08-31) introduce a refusal,
   question, claim, or stance the plan never authorised, and it displays. This is the single
   biggest gap between current state and the stated principle. *Mitigation already shipping:* the
   Stheno-free route + critical `unauthorized-stance` guard closes it for demo-user; extending
   that guard-critical behaviour to the production canary is a config decision, not new code.
2. **Reply gate enforce mode (#19)** *could* replace a reply, but is deliberately in **shadow**;
   promoting it to enforce is gated on measuring its false-positive rate first (correct).
3. **Executive planner (#13) and reranker (#4)** are generative models in judgement seats. Both
   are **structurally bounded** — the planner's output re-passes the assembler + validation; the
   reranker touches ordering only — so neither can alter facts, policy, or authority. The risk is
   *quality*, not *authority*.
4. **Curiosity/reflection (#11)** writes model prose into durable state, but **post-turn** and
   never into the displayed reply; model-generated hypotheses are not auto-promoted to memory.

Everything privileged — privacy, audience, retention, disclosure, tool-authorization, epistemic
integrity, suppression — is a **typed constraint enforced in `PlanV3Assembler`/`ValidateForAudience`**,
not a prose suggestion. That boundary is real and adversarially tested today.

---

## 4. Proposed target architecture (smallest appropriate mechanism per area)

**Keep deterministic, permanently** (exact authorization / invariants): audience-disclosure-
retention-tool-authorization (#6), the assembler authority gate, epistemic policy (#7), the
fidelity guards (#18), fallback rendering (#21), command intent (#1). *No model earns authority
here.*

**Classifier (bounded categorical), shadow-first:**

| Specialist | Responsibility | Prohibited authority | Input contract | Output contract | Class / size | Base? | Data have | Data need | Gate | Fallback | Shadow | Promote / rollback |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| **Memory reranker** (recommend first) | relevance ordering of retrieved memories | facts, policy, retention, count | `(query, [candidate text])` typed | reordered ids + scores | cross-encoder MiniLM 22M ONNX (apache-2.0) | pretrained + our data | cross-encoder **built**; borrowed pairs | ~1–2k **real** harvested relevance labels | P@1, R@3, MRR vs rule floor; no fold worse than floor | `RuleBasedMemoryReranker` | already shadow-loadable | promote when ≥ floor on real held-out across all folds; rollback = config flag |
| Cognitive classifier | memory.unfinished / decision / assertion multi-label | never gates alone — proposes to a det veto | typed sentence + context | label + confidence | encoder 22M ONNX | pretrained + our data | corpus built, crossval'd | real captured labels (Capture ON) | F1 vs detector, ±fold spread < gap | rule detectors | seam built | promote per-decision |
| Emotion / social | tone suggestion only | privacy, epistemic, facts, stance | user message | emotion label + conf | GoEmotions encoder | pretrained | — | fetch GoEmotions | agreement vs MoodDetector; no destructive dep | `MoodDetector` | seam built | low-stakes |

**Structured predictor (limited planning fields):** the executive planner (#13) already is one —
typed proposals, assembler-gated. No change to authority; future work is a smaller tuned model in
that seat, not more authority.

**Generative, only where generation is genuinely necessary:** the Mouth (#16) — already a trained,
evaluated, authority-bounded specialist. And nothing else: planning, policy, memory, personality
are typed decisions, not generation.

**Independent critics (#20):** keep, already refusal-asymmetry-audited.

---

## 5. Ranked migration sequence

Ranked by the brief's criteria (value · data · label-objectivity · isolation · regression-risk ·
compute · shadow-testability). Higher = do sooner.

| Rank | Migration | Value | Data | Labels | Isolation | Regress risk | Compute | Shadow | Net |
|---|---|---|---|---|---|---|---|---|---|
| **1** | **Memory reranker → cross-encoder** (#4) | high (9 callers; removes a generative model from a scoring job) | model built; needs real labels | **objective** (click/use, human relevance) | high (ordering only) | low (rule floor) | tiny (CPU ONNX) | yes, already | **best first** |
| 2 | Production-route guard-critical (#17/#18) | **highest safety** | none (config) | n/a | high | low | none | n/a | do alongside — closes the one real authority leak |
| 3 | Cognitive classifier (#2/#8-adjacent) shadow | med | corpus built | mixed | med | low (shadow) | tiny | yes | after #1 proves the harness on real data |
| 4 | Emotion classifier (#9) | low | fetch GoEmotions | objective | high | very low | tiny | yes | low-stakes warm-up |
| 5 | Reply gate → enforce (#19) | med safety | captured | subjective | med | med (FP replaces replies) | none | measure shadow first | only after FP rate known |
| — | NLI supersession (#8) | — | — | — | — | — | — | — | **rejected** — measured worse than the rule |

---

## 6. Recommended first specialist: **the memory reranker (cross-encoder)**

Why, against the brief's own ranking: a bounded, objectively measurable decision (relevance
ordering) with the model **already built and measured**, nine callers, a generative model
currently doing a scoring job (#4 — the clearest "don't use a 3B for what a 22M does better"), a
deterministic floor already in place, a shadow seam already wired, and **zero authority** over any
protected field. It cannot alter a fact, a policy, or a memory — only the order candidates are
considered in. If it regresses, the rule floor is one config flag away.

It is preferred over the Mouth (open-ended generation, already done) and over any
policy/privacy area (must stay deterministic).

### Concrete plan

- **Training-data source.** `training/cognition/harvest.py` against the running API (Capture is
  **ON** in prod), plus the borrowed relevance pairs already fetched. **Honest gap:** real
  captured volume is tiny right now (5 TurnRecords / 21 shadow rows in the live DB) — the first
  real task is *accumulation*, not training. Target ~1–2k labelled `(query, memory, relevant?)`
  triples before adoption; until then it stays shadow.
- **Dataset schema.** `{query, candidate_text, memory_id, label∈{0,1} or graded, source∈
  {captured,borrowed}, turn_id, split}`. Query = the turn's retrieval query; candidates = the
  retrieved set; label = whether the memory was actually used/kept (objective) or human-marked.
- **Split & leakage controls.** Split by **user and by conversation** (never by row) so a memory
  seen in train cannot reappear in test; freeze the split with a hash; borrowed vs captured kept
  as separate strata and never mixed across splits; a frozen held-out test never touched during
  selection — the same discipline the Mouth corpora use.
- **Baseline to beat.** `RuleBasedMemoryReranker` (keyword+recency+importance) *and* the current
  `LlmMemoryReranker` (3B) — the cross-encoder must match or beat **both** to justify adoption,
  and must not be worse than the rule floor on **any** fold (the ±0.27 fold-spread lesson).
- **Metrics / thresholds.** P@1, R@3, MRR, per-fold. Adopt only if ≥ floor on every fold and ≥
  the 3B on aggregate; else stay shadow.
- **Adversarial / paraphrase.** Paraphrase the query (same intent, different words) — ranking must
  be stable; inject a near-duplicate distractor memory — must not outrank the true one; a query
  with no relevant memory must not fabricate relevance (rank spread stays flat).
- **Shadow comparison.** Load behind `IMemoryReranker` via the existing config seam; record its
  ordering beside the rule ordering per turn (the shadow recorder already supports this); compare
  offline. Displayed context is unchanged during shadow.
- **Hardware / runtime.** 22M ONNX on **CPU**, ~25 ms/call measured — no GPU, no eviction pressure
  on the RTX 5070, runs beside everything. Fine-tune (if needed beyond pretrained) is minutes on
  the 5070.
- **Integration points.** `IMemoryReranker` (one interface, already abstracted);
  `CognitiveModels:Reranker` + `RerankMemories` flags; `ICognitiveModel` runtime seam.
- **Rollback.** Set `RerankMemories:false` (or `Reranker:Enabled:false`) → instant return to the
  rule floor. Reversible, no redeploy of anything else.
- **Bounded sequence.** (1) Harvest until ~1–2k real labels exist; (2) freeze user/conversation
  split; (3) evaluate pretrained cross-encoder as-is vs both baselines; (4) fine-tune only if it
  loses; (5) shadow behind the flag; (6) promote per the gate or leave shadow.

---

## 7. Risks, unknowns, and decisions for Scott

- **Missing evidence, stated honestly:** real captured label volume is **too small to adopt
  anything today** (single-digit turns in the live DB). Every prior specialist verdict in
  `docs/SPECIALIST_MODELS.md` ends on the same sentence — the corpus is synthetic and fold spread
  (±0.27) is wider than the effect measured. **The first bottleneck is data accumulation, not
  modelling.** No amount of architecture fixes this; only real usage does.
- **The one real authority leak** is the production route's final wording (#17) — Stheno with
  guards in shadow. Decision: do you want the production canary moved to **guard-critical**
  (config only, closes the leak, some turns fall back) now, independent of any specialist work?
- **Decision — first specialist:** confirm the **reranker** as first, or override toward the
  cognitive classifier (broader but subjective labels) if you weight breadth over objectivity.
- **Decision — data policy:** the reranker's best labels are *objective usage signals* (was the
  memory kept/used). Capturing those cleanly may need a small typed "memory used this turn" signal
  on the trace. That is a tiny additive change — approve separately before I build it.
- **Not in scope / do not do yet:** no Mouth correction, no explicit-corpus expansion, no base-
  model swap, no broad refactor. The Mouth's residual explicit-refusal ceiling (Run-2.2 finding)
  is a *base-model* limit, orthogonal to this specialisation work.
- **Constraint honoured:** every proposal keeps privacy/audience/retention/disclosure/tool-
  authorization as typed constraints in `PlanV3Assembler`, keeps the plan/4 → guard → fallback
  chain, and prefers shadow + reversible flags. No specialist in this plan gains authority over a
  field it was not trained to predict.

**Stopping here for review. No training or architectural change until you approve the first
specialist and the data-capture signal.**
