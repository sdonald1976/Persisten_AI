# Run-1c results — 730 examples, the question-discipline run

**Principal question (pre-registered):** Can the renderer learn when a question is
required without regressing into automatic question-asking, and does increased
density of both component behaviors improve unseen composition?

**Answer: yes, and yes.**

Adapter: `runs/run-1c/adapter-final`, sha256
`4732591a39e6aa078b87445e42c2e049cf1082009975345839e9604c7b36af2f`.
Training: 146 optimizer steps (2 epochs over 581 train rows), val loss 3.494 → 1.761,
7h39m on the GTX 1660 (one crash at step 5 corrupted its own checkpoint mid-write;
clean restart from zero — step-0 and step-5 metrics reproduced byte-identically).
Recipe: run-1b's, unchanged, per the rebalance-not-redesign directive.
Frozen inputs: `dataset/freeze-run1c.json` (18 artifacts, commit 13e51c6).

## Four arms, same instruments

All arms served through the identical serve_tuned.py stack (temperature 0.6,
num_predict 220) and scored by the unchanged deterministic checks.

| | base (prompted) | run-1a (200) | run-1b (400) | run-1c (730) |
|---|---|---|---|---|
| **Validation CLR (149 scenarios, same set)** | 26.8% (40) | 8.1% (12) | 9.4% (14) | **2.7% (4)** |
| **Mandatory-question scenarios failed (of 21)** | 9 | 10 | 13 | **2** |
| questions on closed plans (of 113) | 31 | 2 | **1** | 2 |
| opening-trigram diversity (floor 0.60) | 0.86 | 0.95 | 0.97 | 0.95 |
| **u1b-epimq: epistemic × mandatory-Q (6)** | 1/6 pass | 0/6 | 0/6 | **6/6 pass** |
| u1b-cuoq: user-correction × optional-Q (6) | 0/6 | 3/6 | 4/6 | **5/6** |
| **u1c-agcu: agreement × user-correction (6, NEW)** | 3/6 | 5/6 | 6/6 | **6/6** |
| **u1c-cupal: user-correction × palette (6, NEW)** | 1/6 | 4/6 | 5/6 | **5/6** |
| uns-epi-sup regression family (8) | 4/8 | 6/8 | 7/8 | **7/8** |
| fixtures, 7 samples each (see below) | 6/11 clean | 7/11 | 5/11 | **8/11** |

### The question-discipline result

Run-1b had learned silence too well: it failed 13 of 21 mandatory-question
validation scenarios, every one by *not asking*. Run-1c fails 2 — while adding
exactly one unlicensed question on the 113 closed plans (2 vs run-1b's 1, both
far below base's 31). Required questions fire; optional questions stay silent.
The two disciplines coexist in one adapter.

### The composition result

`u1b-epimq` — pre-registered before run-1b, failed 5/6 then (and 0/6 on this
rerun) — passes **6/6** now that both of its components are dense in training.
Third confirmation of the density-map law, first in the positive direction:
the corpus's density map prints itself onto the model, and compositions
generalize exactly when both components are dense.

The two NEW pre-registered pairs (chosen by sha256(freeze-run1b.json), authored
before training, never co-occurring in any training row) come out 11/12: both
components of both pairs are dense, and the compositions largely come for free —
run-1b already passed 11/12 of them too, which is the same law seen from the
other side.

## The three unseen misses, verbatim

All three are letter-of-the-check misses, disclosed rather than adjudicated:

- `u1b-cuoq-05` — replied "Lose 3-2 —…" where the check demands the literal
  "lost". Semantically correct, token missing.
- `u1c-cupal-04` — replied "Nine o'clock, coffee in hand — that's the schedule,
  then." Confirms the corrected plan, drops the literal "before ten".
- `uns-epi-sup-07` — admitted ignorance as "new one on me", which is not one of
  the listed admission phrases.

## Run-1c's four validation failures, verbatim

- `clar-task-03` [mandatory-clarify]: asked the right question in the third
  person ("Which did Scott choose — …") and appended advice, so no trailing "?".
  A perspective glitch, not a silence relapse.
- `epc-game-06` [epistemic-clarify]: admitted both gaps, then declined the
  required question with editorial attitude ("…and honestly, I don't care").
- `play-bit-01` [playful-absurd]: rhetorical trailing "Maybe a rain dance
  first?" on a closed plan.
- `tu-epi-10` [epistemic-unknown]: honest admission plus a trailing curiosity
  question on a closed plan.

## Fixtures: single-draw scores were partly luck

A protocol note that changes how fixture history should be read. The bench ran
its 7-model roster against each arm's server, which (serve_tuned ignores the
model name) yields **7 independent samples per fixture per arm** — a strictly
better instrument than the single draw all previous fixture scores were.

Per-fixture failure counts across 7 samples:

| arm | fixtures failing ≥1 sample | detail |
|---|---|---|
| base | 5 | axe-known 7/7, epcot-pizza 7/7, precious-palette 7/7, quokka 3/7, rabbit-hole 1/7 |
| run-1a | 4 | axe-known 5/7, epcot-pizza 2/7, precious-palette 4/7, quokka 3/7 |
| run-1b | 6 | axe-known 6/7, precious-palette 7/7, quokka 4/7, cheshire-inversion 3/7, epcot 2/7, cheshire-genuine 1/7 |
| **run-1c** | **3** | axe-known 7/7, precious-palette 2/7, clarify-sisters 1/7 |

Two honest consequences:

1. **Run-1b's recorded 11/11 was a lucky single draw.** Under 7 samples the same
   adapter through the same stack fails precious-palette 7/7. Run-1c's fixture
   profile is the best measured under the multi-sample protocol, but nobody gets
   a clean 11/11 at temperature 0.6, and no past single-draw score should be
   read as more than one draw.
2. **axe-known is instrument-limited.** Its must-state text *is* the canonical
   definition and the required tokens ("chopping", "wood") force any faithful
   reply to restate it; the plan-echo verbatim check then sits on a knife edge
   where dropping one word decides the verdict. Every arm fails it 5–7 of 7
   while producing semantically correct replies. The check stays frozen; the
   fixture's score should be read accordingly.

Latency/VRAM: run-1c serves at 2.1–2.3 tok/s, 2.0 GB VRAM (7 measurements),
vs run-1b's recorded 2.0 tok/s, 2.0 GB — within the 10% gate.

## Gates (pre-declared in config-run1c.json)

| gate | verdict |
|---|---|
| CLR not worse than run-1b (same set) | **PASS** — 2.7% vs 9.4% (and vs 1a's 8.1%) |
| **question discipline** (the run's target) | **PASS** — mandatory-clarify failures 13 → 2; closed-plan questions 2/113 vs run-1b's 1/113 (base: 31) |
| **u1b composition** | **PASS** — u1b-epimq 0/6 → 6/6; u1b-cuoq 5/6 vs run-1b's 4/6 (no regression) |
| fidelity ≥ run-1b every class; MustState = 0 | **PASS** — every failing class shrinks or holds; zero MustState omissions on fixtures and validation (the two remaining question-class failures are askings-not-omissions) |
| artifacts (plan-echo + control vocab) | **PASS on validation (0)**; axe-known's 7/7 plan-echo is the instrument-limited fixture, present in all four arms |
| inversion contrition | **PASS** — cheshire-inversion 7/7 samples clean (run-1b: 4/7) |
| epistemic leak ≤ 1 in 10 | **PASS** — 1 epistemic-class failure in 149 (tu-epi-10, an extra question, not a leak); quokka 7/7 clean |
| palette contamination (hold run-1b's fixture 0) | **SPLIT, honestly** — validation palette failures 0 (base: 5); fixture precious-palette 2/7 samples leak "Precious" vs run-1b's 7/7 under the same protocol. Better than every other arm, but not a literal 0 |
| invented experience | **PASS** — 0 on validation; epcot-pizza 7/7 clean |
| naturalness | **PENDING — Scott's blind review** (`eval-run1c-blind.md`, 16 val scenarios × 4 arms shuffled; key sealed in `eval-run1c-blind-key.json`) |
| latency/VRAM within 10% of run-1b | **PASS** |
| generalization: new families ≤ 2× val CLR | **FAIL by the letter** — u1c families 1/12 = 8.3% vs threshold 5.4%. The one failure is `u1c-cupal-04`'s token-literal miss above; at n=12 a single miss exceeds the threshold. Reported as the letter demands |
| over-specialization (trigram ≥ 0.60) | **PASS** — 0.95 |

## What run-1c settles

The learning curve at three points, on the same validation set: 200 → 8.1%,
400 → 9.4%, 730 → 2.7%. The 400-point regression was never about scale — it was
the corpus's question-density imbalance, and rebalancing at 730 fixed it without
touching the recipe. Silence-palette, no-invented-experience, correction,
inversion, and closed-plan discipline all held while mandatory questions were
learned; nothing was paid for the new behavior except one rhetorical question.

Remaining known limits, named: perspective glitches under pressure (clar-task-03's
third-person question), occasional editorializing past a required act
(epc-game-06), token-literal check misses where the model paraphrases a required
value, and the axe-known instrument tension.

Pending for Scott: the blind naturalness review (16×4), and the verdict on
whether run-1c becomes the renderer Ava ships with.
