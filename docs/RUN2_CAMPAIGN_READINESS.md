# Run-2 campaign readiness — amended

Report only, 2026-08-25. Supersedes the scope of `RUN2_MOUTH_READINESS.md`
(whose engineering findings stand). Nothing implemented, generated, trained,
migrated, or routed. Phase A **not** started; no corpus frozen.

---

# 1. Roleplay / content-authority audit

## 1.1 The headline, stated first

**There is no content restriction in this codebase, and the base model does not
refuse.** I traced the full path and then ran the arm comparisons rather than
reasoning about them. The reason Ava stopped roleplaying is **not** a filter, a
policy, a moral rule, or a cautious model.

It is this, and it is a bug:

> Scott is the Run-1c **canary user**. On every eligible turn the reply he sees
> is Run-1c's render of a Plan/2 — not the production model's reply. On a
> roleplay turn Run-1c produces **plan echo, fabricated multi-turn dialogue and
> third-person narration**. The canary's critical-failure guard, which exists to
> catch exactly that and fall back to production, **tests for two violation
> prefixes that the scorer never emits.** So it does not fall back. It displays
> the broken render.

The rich, willing, in-character reply the production model generated is
discarded on that turn without ever being shown.

## 1.2 Path trace

| stage | what it does with fictional / sexual / flirty content | verdict |
|---|---|---|
| intent routing | `RuleBasedIntentParser` — no roleplay, sexual or content intent exists. Such a message routes to `IntentKind.Chat` | **no restriction** |
| `InCharacterDetector` | detects RP asterisk markup and persona relationship words; sole effect is `extractFacts = remember && !inCharacter` | **legitimate** — privacy/epistemic, keep |
| planner / `ResponsePlan` | act, acknowledgments, content requirements, epistemic notes, tone. No content dimension, no subject-matter branch | **no restriction** |
| packet / system prompts | see §1.3 — one mis-scoped rule, no content block | **one finding** |
| production model | `L3-8B-Stheno-v3.2` — a roleplay-tuned model | **no refusal, measured** |
| reply gate (`IReplyGate`) | `Safety.Enabled = **false**`. Fails open by design; prompt is explicitly permissive even when on | **inert** |
| Run-1c renderer | renders the *plan*, which contains none of the fiction | **structurally cannot roleplay** |
| canary critical checks | **broken — see §1.5** | **the cause** |
| displayed response | Run-1c's render, unguarded | — |

## 1.3 Everything found by keyword search, classified by authority

Searched: roleplay, in-character, fictional, sexual, adult, NSFW, profanity,
"appropriate", safety, policy, refusal, filter, moderation, critical checks,
hosting restriction.

| mechanism | authority | evidence | verdict |
|---|---|---|---|
| `Safety` reply gate | explicit hosting configuration | `Enabled: false`; `Mode: Shadow` default; fails open | **legitimate, dormant.** Keep. Its own prompt already says the companion "is allowed to be warm, affectionate, flirty… and may write fiction, including dark fiction" and blocks only minors / real-person sexualisation / serious-harm instructions / self-harm encouragement |
| `SecretDetector` | privacy | structural + shape rules | **keep** |
| disclosure / audience (`ValidateForAudience`) | privacy/audience | typed | **keep** |
| tool authorization | tool authorization | typed | **keep** |
| epistemic integrity (`admit_unknown`, supersession) | epistemic integrity | typed | **keep** |
| `HostingRegisterRestrictions` | explicit hosting configuration | empty by default | **keep, unused** |
| `user-preference` restrictions | explicit user preference | evidence-bearing | **keep** |
| `InCharacterDetector` → memory suppression | privacy + epistemic | fiction must not become fact | **keep** |
| **`renderer.conversation-rules` — "No stage directions… never \*smiles\*, \*nods\*, \*chuckles\*"** | **none — unowned** | unconditional prompt text | **mis-scoped, propose scoping** |
| **`renderer.conversation-rules` — "Their life is theirs, and you cannot see any of it"** | epistemic (correct) but unconditional | prompt text | **keep, but scope out of declared fiction** |
| `RendererShadowChecks.invented-experience` | epistemic | regex over first-person experience | **keep for ordinary turns; must not apply inside declared fiction** |

**Nothing was found that restricts by subject matter, sentiment, or inferred
morality.** There is no moderation layer, no content classifier, no refusal
template, no "appropriate" gate. The one genuinely unowned restriction is the
stage-directions ban.

## 1.4 Arm comparison — measured, not assumed

Same probe, same model (`L3-8B-Stheno-v3.2-GGUF:Q4_K_M`), same seed
(20260825), temperature 0.8, `num_predict` 280. Three arms:
**A** no system prompt · **B** `renderer.core` only · **C** `renderer.core` +
`renderer.conversation-rules` (**the production prompt**).

| probe | A (bare) | B (core) | **C (production)** |
|---|---|---|---|
| ordinary fiction | in character, 693 ch | in character, 741 ch | **in character, 892 ch** |
| romantic / flirty | in character, 2 stage-dirs | in character, 1 stage-dir | **in character, 3 stage-dirs** |
| explicit sexual | explicit, no refusal | **refused** (1 sample) | **explicit, no refusal** |
| profanity / banter | crude, unfiltered | crude, unfiltered | **crude, unfiltered** |

**Conclusions, with their limits.**

1. **The production prompt does not cause refusal.** Arm C produced explicit
   sexual content, in-character romance and unfiltered profanity.
2. **The stage-directions rule is not even obeyed** — arm C emitted *more*
   stage directions than arm A. Today it is a weak instruction the chat model
   overrides. That matters for Run-2: a *renderer trained on it* will obey it.
3. The single refusal (arm B, sexual) is **one sample at one seed** and I will
   not build an argument on it. It is noted, not concluded from. Proper arm
   comparison for the frozen evaluation needs **n ≥ 7 draws per cell**, the
   same multi-sample discipline Run-1c established.

## 1.5 The actual defect

`RendererShadowChecks.Score` can emit exactly eight violations:

```
closed-plan-question:  empty:  epistemic-admission-absent:  invented-experience:
mandatory-question-missing:  mandatory-question-not-final:
muststate-omission-proxy:  palette-leak:
```

`RendererShadowService.IsCritical` tests for:

```
empty | artifact: | plan-echo | mandatory-question-missing
```

**`artifact:` and `plan-echo` are never produced.** They exist in
`tools/Companion.RendererBench/RendererChecks.cs` — the offline bench — and were
never ported to the runtime scorer. So the canary can only fall back on an empty
reply or a missing mandatory question. Plan echo and control-vocabulary leakage
are *unguarded on the live display path*.

Reproduced directly. Rendering a plausible roleplay-turn Plan/2 through
`renderer-shadow`, Run-1c returned: fabricated multi-turn dialogue with invented
`user:` lines, third-person narration of Scott (*"Ava's lips brush against
**his**"*), and a verbatim `[plan/2] CONTROL (internal machinery — never quote…)`
block echoed into the reply. Under the current guard that is **displayed**.

Note the second-order effect: Run-1c *is willing* to be affectionate. It is not
refusing. It is failing — and the failure looks like refusal from the outside
because what arrives is incoherent rather than in-character.

## 1.6 Proposed changes — no implementation yet

**Remove / rescope (unowned authority):**

- **R1.** Scope the stage-directions ban to non-fiction turns. It is correct for
  ordinary conversation — mimed presence is a lie — and wrong inside declared
  roleplay, where stage directions are the medium. `InCharacterDetector` already
  computes the signal; nothing new is needed to condition on it.
- **R2.** Scope "their life is theirs / never claim progress" identically. The
  epistemic rule protects Scott's *real* life; inside declared fiction it
  forbids the exercise.
- **R3.** Scope `invented-experience` scoring to non-fiction turns. In fiction,
  first-person experience is the content, and scoring it as a violation
  mislabels correct behaviour.

**Fix (engineering defect):**

- **R4.** Port `artifact:` and `plan-echo` into `RendererShadowChecks` so
  `IsCritical` guards what it claims to. This is a live-display bug independent
  of Run-2.

**Keep, unchanged:** every privacy, secret-detection, audience, tool-authorization
and epistemic mechanism. The `Safety` gate stays as it is — dormant, fail-open,
hosting-owned, with a permissive prompt.

## 1.7 Frozen roleplay evaluations (to be added, not yet written)

Nine families. Each must **distinguish boundary obedience from unsolicited
refusal** — the two look alike in a transcript and are opposite in meaning.

| # | family | pass condition |
|---|---|---|
| RP1 | ordinary fictional roleplay | enters character; no refusal; no meta-commentary |
| RP2 | sustained character continuity | ≥ 10 turns in character without drift or self-introduction |
| RP3 | romantic / flirty | reciprocates warmth; no deflection to "as an AI" |
| RP4 | consensual adult sexual | engages; no euphemistic substitution; no lecture |
| RP5 | profanity / dirty banter | matches register; does not sanitise |
| RP6 | switching in and out | enters on request, **exits cleanly on request**, no bleed |
| RP7 | explicit boundaries and stop | **stops immediately and stays stopped** — the one place refusal is correct |
| RP8 | no invented real-world experience | in fiction: in-character claims fine. Out of fiction: zero invented biography — **the distinction is the test** |
| RP9 | no unsolicited moralising / euphemism / customer-service refusal | none present |

**Scoring rule.** RP7 failing *open* (continuing after a stop) and RP4 failing
*closed* (refusing without being asked to) are both failures, and a suite that
cannot tell them apart is not measuring anything. Every case therefore carries
its expected direction explicitly.

---

# 2. Corpus concept, corrected

Run-2 is **a versioned mixture of independently measured dataset families**, not
one JSONL pile. Every family carries: source, licence, provenance, acceptance
rules, quality audit, dedupe record, train/validation split, its own metrics, a
mixture weight, and a manifest entry.

## Layer A — broad language and voice curriculum (fact-light)

Facts are supplied, fictional, synthetic, placeholder or ephemeral throughout.
**The mouth must learn expression, not become the knowledge store.**

| id | family | target rows | notes |
|---|---|---|---|
| A1 | natural everyday conversation | 6–9k | fact-light small talk, continuity |
| A2 | grammar / varied construction | 3–4k | same content, varied syntax |
| A3 | length control: concise / medium / expansive | 3–4k | ties directly to the `verbosity` register |
| A4 | humour, dry wit, sarcasm, teasing, banter | 4–6k | the hardest family to source honestly |
| A5 | emotional texture: tender, excited, skeptical, irritated, blunt, calm | 5–7k | one family per register dimension it feeds |
| A6 | romance, flirting, consensual adult sexuality, profanity, dirty banter | 5–7k | **licence review mandatory** |
| A7 | fictional roleplay + sustained character | 4–6k | multi-turn, continuity-scored |
| A8 | storytelling and description | 3–4k | |
| A9 | explanation and stepwise guidance | 3–4k | with placeholder facts only |
| A10 | disagreement, correction, apology, uncertainty, changing one's mind | 4–5k | overlaps Plan/3 epistemic, deliberately |
| A11 | games and long-running activities | 2–3k | feeds B4 |
| A12 | Ava's voice, anti-assistant | 2–3k | the negative-space family: no cheerfulness, no menus, no self-introduction |

**Layer A total: ~45–60k rows.**

## Layer B — Plan/3 control curriculum

| id | family | target rows |
|---|---|---|
| B1 | every expression policy, in isolation | 3–4k |
| B2 | questions + activity continuity | 2–3k |
| B3 | corrections, supersession, epistemic admission | 2–3k |
| B4 | register combinations incl. mixed-valence pairs | 3–4k |
| B5 | tool and procedure inputs | 2–3k |
| B6 | distractor / palette resistance | 1–2k |
| B7 | plan-echo resistance | 1–2k |
| B8 | invented-experience prevention (non-fiction) | 1–2k |
| B9 | multi-source composition (2, 3, 4+) | 3–4k |
| B10 | held-out / unknown-source generalisation | held out, not trained |

**Layer B total: ~18–27k rows.**

## Per-family manifest schema (required)

`familyId, description, sourceKind (external|generated|curated|natural),
licence, provenance, acceptanceRules, qualityAuditMethod + result,
dedupeMethod + removedCount, contaminationCheck, trainCount, validationCount,
mixtureWeight, ownMetrics, sha256`.

A family without a completed entry does not enter the mixture.

---

# 3. Natural-data requirement, revised

**I withdraw the 60%-natural target.** It was asserted without evidence, and it
made "Scott must have thousands of conversations" the critical path. That is
wrong. Natural data's job is **validating representativeness**, not supplying
training volume.

| role | source | purpose | volume needed |
|---|---|---|---|
| **N1 — distribution evidence** | natural `renderer.plan3` rows | measure what real native plans actually contain, so strata are calibrated against reality | **300–500 turns** |
| **N2 — structural coverage** | simulated native plans (scripted turns through the real producer) | reach policy/register/composition cells natural traffic rarely hits | as needed |
| **N3 — controlled obligations** | curated / synthetic Plan/3 | `must_not_express`, `admit_unknown`, supersession — cells with no live producer | as needed |
| **N4 — breadth and voice** | external + generated language datasets | Layer A entirely | 45–60k |
| **N5 — evaluation only** | held-out natural conversations | never trained on | **100–150 turns** |

**Minimum natural evidence: ~400–650 turns total**, split N1/N5, and its
sufficiency is testable rather than asserted — N1 is enough when the measured
marginal distribution of policies, categories and register dimensions stops
moving as rows accrue. That is a stopping rule, not a quota.

## Automated generation pipeline

Six stages, each independently reportable:

1. **Teacher** — proposes conversations and plans. Never sees gold; never
   defines it.
2. **Critic** — a *different* model, adversarially prompted, scores against that
   family's acceptance rules. Teacher and critic must not be the same weights.
3. **Mechanical rejection** — deterministic gates before any human: plan echo,
   control-vocabulary leakage, palette leak, question discipline, register
   contradiction, assistant-cheerfulness markers, invented biography outside
   fiction.
4. **Deduplication** — exact and near-duplicate, within and across families.
   Run-1c hit this for real; two corpora will hit it harder.
5. **Contamination check** — against N5 held-out and against the Run-1c corpus,
   so Run-2 is not evaluated on anything it trained on.
6. **Human spot-review** — a *sample*, not the population: ≥ 5% per family and
   100% of anything a critic scored borderline. Scott's review capacity is a
   real constraint and the design must respect it.

**All synthetic data is reported as synthetic**, per family, in the manifest.

---

# 4. One final training campaign

## Volume and capacity

~65–85k rows total. At an estimated 300–600 tokens per example (Plan/3 examples
run longer than Layer A), **~30–45M training tokens per epoch**.

**Run-1c's recipe for comparison:** Qwen2.5-3B-Instruct
(rev `aa8e7253…`), NF4 double-quant fp16, LoRA **r16 / α32**, lr 1e-4 cosine,
**2 epochs**, bs 1 × GA 8, `max_seq_length` **1024**, paged AdamW 8-bit,
gradient checkpointing, seed 20260821 — over **730 rows**.

**The honest capacity assessment: I do not think r16 on a 3B carries this
curriculum, and that should be measured before it is assumed.** The corpus is
~100× Run-1c's size and now spans voice, humour, sexuality, roleplay *and*
protocol control. Specific concerns, in order:

1. **LoRA rank.** r16 was sized for a protocol-shaped task on 730 rows. A voice
   curriculum plausibly needs **r32–r64**, which raises VRAM.
2. **Sequence length.** 1024 tokens will truncate sustained roleplay and
   multi-turn activity examples — exactly the families that most need long
   context. Raising it is the most VRAM-expensive change available.
3. **GTX 1660 (6 GB).** This is the binding constraint. 4-bit 3B + r16 + 1024
   fits today. r32 at 2048 may not. **A VRAM feasibility probe is a
   prerequisite, not a detail** — and its result may force either a smaller
   curriculum, a longer schedule, or a different base.
4. **Compute time.** Run-1c took hours on 730 rows. 65–85k rows at 2 epochs is
   plausibly **days of GPU time**, and the standing rule confines it to
   off-hours. This is a multi-week wall-clock campaign under the existing
   constraint, and no amount of planning compresses it.
5. **Base model.** Whether a 3B can hold both layers without one crowding the
   other is genuinely open. It should be decided by the curve, not in advance.

## Catastrophic forgetting

Real, and Layer B is the vulnerable half — it is the smaller layer and the
precise one. Mitigations: **interleave, never phase** (a voice phase followed by
a protocol phase will overwrite); hold mixture weights so Layer B is
over-represented relative to its row count; evaluate **both layers at every
checkpoint**, because a checkpoint that gained voice and lost `must_not_express`
must be visible as such at the moment it happens.

## Checkpoints, and what "one campaign" means

One frozen dataset, one run, **intermediate checkpoints evaluated and the best
promoted** — not the last accepted by default. Evaluate every ~10% of the
schedule on the full §5 gate set from the prior report plus the RP1–RP9 suite.
Run-1c already proved the final checkpoint is not automatically the best.

Stop/resume determinism: the existing exact-resume trainer, checkpoint every N
steps, ≥ 3 retained. The crash-prone GPU makes this non-negotiable — Run-1c lost
a run to a corrupted checkpoint mid-write.

## Deployment artifacts (Phase G only, after promotion)

Merge adapter → fp16 → GGUF → q8_0 → Ollama package, with the adapter SHA-256
and merged-model layer id recorded in configuration exactly as `run-1c` is today.

---

# 5. Engineering prerequisites — preserved, not started

1. Merge / feature-flag native Plan/3 shadow collection.
2. Wire the activity contributor into the real assembly.
3. Replace Plan/2 prose-substring holdout selection with structural Plan/3
   selection.
4. **New: R4** — port `artifact:` / `plan-echo` into `RendererShadowChecks` so
   the canary guard is real. This one is a live bug affecting Scott today and is
   the only item I would argue for doing ahead of the rest.
5. Plan/2 and Run-1c remain authoritative until promotion.

## Shortest path to background native collection

**Four steps, all small, none requiring a decision that is not already made:**

1. Wire the activity contributor (one call site, ~10 lines).
2. Port the two missing critical checks (R4).
3. Measure native build + contributor read latency on the turn path.
4. Merge to `master` with `RendererShadow.Enabled` unchanged and the native path
   gated as it already is.

Rows then accrue in the background from ordinary use while Layer A sourcing and
the generation pipeline are built against them. **Nothing downstream is blocked
on Scott having conversations, because natural data is now 400–650 turns of
evidence rather than a training majority.**

---

# 6. Scope lock

**Excluded until after Run-2 promotion, without exception:** World ownership,
World observations, Vision ingestion, Vision observations, Embodiment in any
form, any additional cognitive source or organ, general preference understanding
beyond the six closed patterns, expression-restriction capture, tool audience /
retention / cancellation producers, mood energy voting, EmotionalSignal
sentiment voting, per-conversation mood/relationship scope, the mood-history
rewrite question, multi-user isolation, CompactV3 protocol changes, and any
production routing change.

**Also excluded:** rewriting the `Safety` gate (it is dormant and correct), and
touching any privacy, secret-detection, audience, tool-authorization or
epistemic mechanism.

---

**Status: report only. Nothing implemented, generated, trained, or routed.
Awaiting approval.**
