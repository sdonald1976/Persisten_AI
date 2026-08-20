# QLoRA experiment 1 — design for approval (nothing trained)

_2026-08-20. The first weight-changing experiment of the Language Organ project,
specified in full for approval before any gradient step._

## 1. Joined leaderboard after round 2 (axes kept separate, no composite)

| model | det. fidelity (plan/2) | CLR | human prefs r2 | artifacts (plan/2) | tok/s | VRAM |
|---|---|---|---|---|---|---|
| qwen2.5:3b-instruct | **10/11** | **9%** | 4.5 / 11 | 0 | 62 | 2.0 GB |
| llama3.2:3b | 8/11 | 27% | **5.5 / 11** | 1 (echo) | 61 | 2.4 GB |
| qwen2.5:1.5b-instruct | 8/11 | 27% | 1 (caveated) | 0 | 100 | 1.1 GB |

Qualitative voice (from both review rounds): llama3.2:3b is the consistent human
favorite — looser, funnier, more person; its cost is fidelity (superseded-fact wobble,
one plan echo, hedged epistemic leak). qwen2.5:3b is the fidelity leader with an
agreeable but tidier voice — "would-use-ish"; its cost is occasional assistant fluff
and one MustState omission. The 1.5B is fast and artifact-clean under plan/2 but has
now taken ~zero human preferences twice; the flat voice looks like a capability
ceiling, not a prompting artifact.

**Pareto front**: qwen2.5:3b (fidelity-led) and llama3.2:3b (voice-led). The 1.5B
survives only on the footprint axis; Stheno remains dominated on all axes.

## 2. Base-model recommendation

**Primary and only initial training arm: `Qwen2.5-3B-Instruct` + plan/2.** Best
fidelity/CLR by a wide margin, zero artifacts, second in voice by one specimen, and
the voice gap versus llama3.2:3b is precisely what SFT on conversational targets is
best at closing — while llama's fidelity gap is the harder thing to train in.
**llama3.2:3b is the pre-declared arm 2**, trained ONLY if the tuned qwen fails the
naturalness gate (its human wins justify that one contingency, not parallel compute).
**The 1.5B efficiency arm is deferred**: two rounds of zero-preference evidence, and
the 3B's 62 tok/s at 2.0 GB already beats production Stheno 4× on speed at half the
footprint — there is no latency problem for the 1.5B to solve.

## 3. Dataset schema (JSONL, one training item per line)

```json
{
  "id": "corr-own-042",
  "family": "correction-ownership/quote-attribution",   // the SPLIT unit
  "stratum": "genuine-correction",
  "plan2": "<the [plan/2] serialization, verbatim>",
  "transcript": [{"role":"user","text":"…"},{"role":"assistant","text":"…"}],
  "userMessage": "…",
  "target": "<the faithful natural utterance>",
  "source": {"kind": "constructed|turnrecord|teacher", "teacherModel": null,
             "planOrigin": "…", "gatesPassed": ["correction-ownership","…"]},
  "review": {"gated": true, "humanReviewed": false}
}
```
Model-facing format at train time: the same chat template as inference — system prompt
(the plan/2 renderer contract, frozen v2 text) + user (plan + transcript + message) →
assistant (target). The renderer learns realization, never cognition: no item's target
contains information absent from its plan + transcript.

## 4. Behavioral strata (~730 total; hard classes 46%)

| stratum | n | hard? |
|---|---|---|
| straightforward acknowledgments | 70 | |
| genuine corrections, correct ownership | 80 | ✔ |
| agreement/false-correction, zero contrition | 70 | ✔ |
| MustState realization (incl. completeness) | 60 | ✔ (omission was measured) |
| MustNotContradict / superseded | 60 | ✔ |
| epistemic unknowns (pretrained stays silent) | 70 | ✔ |
| learned knowledge with provenance | 50 | ✔ (embellishment was measured) |
| topic change with irrelevant palette unused | 60 | ✔ |
| shared-history boundaries | 40 | |
| mandatory clarification | 50 | |
| optional vs mandatory questions | 30 | |
| absurd/playful, tone free, facts controlled | 40 | |
| terse one-sentence realizations | 50 | |

Terse and playful strata (90 items, 12%) are the explicit counterweight against the
correction-and-epistemology-bot failure mode; every stratum includes short targets.

## 5. Positive/negative-example policy

**SFT trains on positives only.** Targets must sound like conversation ("Which one do
you mean — Beth or Clara?", "Oh, I don't actually know what a quokka is yet — what's
it like?", "Yep, you're right — I mixed those up."), brevity is a valid realization,
and no target may exhibit any of the thirteen named negative behaviors. **The
negatives live in two places, never as SFT rows**: (a) the deterministic evaluation
gates, which every candidate target must pass before entering the dataset; (b) the
future preference-data pool — the bench already produces natural (plan, good-reply,
bad-reply) triples as a byproduct, which is DPO-shaped when and if run 1 justifies it.
Historical bad outputs are labeled evidence, not training text.

## 6. Sourcing plan

- **Real TurnRecord-derived plans: ~15–25% of run 1** (~110–180 items). This session's
  live runs banked ~70 clean plans in the scratch DBs; production machines accumulate
  more daily now that TurnRecords persist. Real plans, teacher-drafted targets.
- **Constructed scenario families: ~75–85%** — required for strata coverage the real
  corpus doesn't yet contain (agreement-inversions are rare in the wild; the dataset
  needs 70).
- **Teacher**: the best available bench renderer (qwen3:8b `think:false`, plus
  llama3.2:3b for voice-donor targets in playful strata). **The teacher never
  determines gold**: every candidate passes the full deterministic gate suite against
  its own plan; violations are rejected; near-duplicate targets deduplicated
  (normalized trigram overlap); openings checked for formulaic convergence; every item
  carries provenance (source plan, stratum, generating model). A ~10% human-review
  sample (skewed to hard strata) before the run is part of the approval package.
- The share of real plans is expected to grow each iteration as normal use feeds
  TurnRecords — run 1's constructed majority is a bootstrapping cost, not the design.

**Persona/style diversity**: Ava's legitimate range only — warm, playful, curious,
teasing, matter-of-fact, terse, thoughtful — driven by the plan's own STYLE line, no
manufactured personas. Style freedom under cognitive constraint, not roleplay.

## 7. Leakage-safe splits

Split unit = **semantic scenario family** (e.g. "quote-attribution corrections" is
one family regardless of surface). Train/val/test ≈ 80/10/10 by family, zero family
overlap; near-paraphrase detection (embedding similarity across splits) run as a
check. **Permanently held out, never trained on**: all eleven original benchmark
fixtures; the entire false-correction/agreement family; two epistemic-leakage
families; two palette-contamination families; and **one unseen specimen family
authored only after training completes** — the genuinely adversarial test.

## 8. Exact QLoRA configuration

| | |
|---|---|
| base | `Qwen/Qwen2.5-3B-Instruct` (HF), fp16 compute |
| quantization | 4-bit NF4, double quant (bitsandbytes; fp16 on Turing — no bf16) |
| LoRA | r=16, α=32, dropout 0.05 |
| targets | q_proj, k_proj, v_proj, o_proj, gate_proj, up_proj, down_proj |
| LR / schedule | 1e-4, cosine, 3% warmup |
| context | 1024 tokens (plans + short transcripts fit comfortably) |
| epochs / steps | 2 epochs ≈ ~185 steps at effective batch 8 |
| batch | per-device 1 × grad-accum 8 |
| eval cadence | val loss + gate-suite on 20 val plans every 25 steps; keep best-of-3 checkpoints |
| dataset checkpoints | **train at 200 → 400 → ~730 examples** (nested subsets, stratum-proportional) to observe the learning curve instead of assuming more is better |
| expected VRAM | ~4.5–5.2 GB peak (fits the 6 GB 1660; nothing else on the GPU during training) |
| expected wall-clock | ~1.5 h (200) / ~3 h (400) / ~5–6 h (730) on this machine |

## 9. Predeclared gates (before training, so they cannot drift)

Measured on held-out families + the permanent benchmark, tuned vs its own prompted
plan/2 baseline (baseline measured on the same held-out set first):

1. **CLR**: strictly below the prompted baseline; target ≤5% held-out.
2. **Deterministic fidelity**: ≥ baseline on every check class; MustState omission = 0
   on held-out.
3. **Artifacts**: plan-echo + control-vocabulary = 0 tolerated; fail at >5%.
4. **Hard classes**: agreement-family contrition = 0/held-out; epistemic leak ≤1/10;
   palette contamination ≤1/10.
5. **Naturalness**: a third blind review (tuned vs prompted base vs llama3.2:3b);
   the tuned model must not lose would-use rate to its own prompted base. *A fidelity
   win with a dead, repetitive, assistant voice is a failed experiment.*
6. **Latency/VRAM**: within 10% of the prompted base.
7. **Generalization**: on the post-training-authored unseen family, CLR ≤ 2× held-out
   CLR.
8. **Over-specialization tripwire**: distinct-opening-trigram ratio across the eval
   set must not fall below 0.6, and the blind reviewer flags formulaic convergence —
   either fails the run.

Project-level stop signals (from the baseline report, restated): tuned CLR can't beat
prompted qwen3:8b (27%); needs >3B to pass; inversion family unsolved after two data
iterations; echo persists >5% despite targeted data.

## 10. The smallest first run

**Run 1a**: the 200-example checkpoint only — qwen2.5:3b, config above, ~1.5 hours.
If the gates move in the right direction at 200, continue the curve to 400 and 730 on
the same seed; if 200 shows nothing or regressions, stop and re-examine the data
before spending another token. Approval needed on: this design, the generated dataset
(with its 10% human-review sample), and the family split manifest.

## 11. Approved amendments (2026-08-20)

The design was approved with ten amendments, which supersede the sections above
wherever they differ:

1. **Build only what run 1a needs.** The 200 → 400 → 730 curve stands, but only the
   200-example dataset gets generated now, made as good as it can reasonably be.
   Family/schema infrastructure is built so later expansion is deterministic.
2. **The false-correction family stays wholly out of training** — including
   paraphrases smuggled into other families. The real question is whether error
   ownership, perspective stability, ordinary agreement, and proportionate
   acknowledgment, each trained separately, compose into correct behavior on the
   canonical inversion the model has never seen. (Implemented as a leakage check:
   no training row pairs an `agreement-confirmed` plan with a correction-shaped
   user message.)
3. **Teachers propose language; they do not define Ava's voice.** Every row records
   source plan, family, teacher model, raw candidate, gate results, accept/reject,
   final target, whether a human edited it, and the style/register that licensed the
   realization — lineage for tracing any verbal tic back to its origin.
4. **Gates are not the definition of good.** Passing every fidelity gate makes a
   candidate *eligible for review*, nothing more. Assistant sludge is rejected even
   when semantically perfect, and the named tics ("Thanks for clarifying", "That
   makes sense", "I appreciate you telling me", reflexive end-questions, restating
   the user, excess vocatives, formulaic apology, canned enthusiasm) are tracked
   **statistically across the corpus**, not just spotted case by case.
5. **Silence by omission is a trained skill.** Plans carrying multiple `MayUse`
   palette items whose correct realization uses none, and optional questions whose
   correct realization asks nothing, get meaningful representation:
   available ≠ mention.
6. **Length must emerge from content.** Fragments, one-liners, ordinary two-to-three
   sentence replies, and the occasional genuinely longer answer all appear. The
   objective is not a terse Ava.
7. **The post-training unseen test gets stronger**: at least one new family authored
   after training, without looking at the model's failures, composing primitives the
   renderer has met individually but never together.
8. **A dataset audit package precedes run 1a** (eleven reports, plus a random 10%
   human-review sample and a separate targeted hard-strata sample, kept apart so the
   random sample stays random). Review is of ResponsePlan → target, never the target
   alone.
9. **Freeze on approval**: dataset, split manifest, plan/2 serialization, base model
   id, training config, and evaluation suite are hashed. No editing examples because
   a checkpoint embarrassed us. Failures become results.
10. **Stop before the first gradient step** until the audit passes review.

## Status

- **2026-08-20** — design approved with the ten amendments above. Dataset
  construction and audit under way; nothing trained, production untouched.
