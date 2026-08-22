# Run 1b — results against the predeclared gates, three arms

_2026-08-23. Second point on the learning curve. Frozen inputs per
`dataset/freeze-run1b.json`; nothing changed after the freeze. Two full-machine
crashes preceded the completed run; attempt three ran 4h28m with exact-resume
checkpoints armed and unused. Every arm evaluated through the identical serving
stack and the identical frozen checks._

## The run

Same base (`aa8e7253`), same NF4/r16/α32/2-epoch recipe — the dataset is the only
variable: 416 rows (train 315 / validation 101 by pinned families), with the three
directed expansions (silence-palette 69, no-invented-experience 35,
multi-obligation 40). 78 optimizer steps; validation loss 3.665 → **1.879**.

Adapter sha256: `9ed660b2eccfd1e56bf049c5ca6714825d35b52f8c26fd4edf9f7d67ca10fdae`

## Headline: three arms, identical sets

| | base (prompted) | run-1a (200) | run-1b (400) |
|---|---|---|---|
| **Fixtures (11 permanent holdouts)** | 9/11 | 8/11 | **11/11** |
| fixture palette contamination | 1 | 3 | **0** |
| **Validation CLR (101 scenarios)** | 30.7% | **4.0%** | 5.0% |
| questions on closed plans | 28/85 (33%) | 2/85 | **1/85 (1.2%)** |
| MustState-class misses (validation) | — | — | **0** |
| opening-trigram diversity | 0.90 | 0.99 | **1.00** |
| **Run-1a regression family (epi×superseded, 8)** | 5/8 fail | 3/8 fail | **1/8 fail** |
| **New unseen compositions (12, pre-registered)** | 11/12 fail | 10/12 fail | 5/12 fail |

**11/11 on the permanent benchmark is the first perfect score in the project's
history, prompted or tuned** — including `epcot-pizza` and `precious-palette`, the
two fixtures run-1a's palette weakness failed, and the agreement inversion. The
design's original LoRA target ("11/11 on a held-out fixture split") is met at 400
examples.

## Gates

| gate | verdict |
|---|---|
| CLR strictly below run-1a on validation; target ≤5% | **SPLIT** — hits the ≤5% absolute target (5.0%), but is not strictly below run-1a's 4.0% (5 vs 4 failures on 101 single draws; within noise, reported as the letter of the gate demands) |
| fidelity ≥ run-1a on every check class; MustState omission = 0 | **FAIL on one class** — mandatory-clarify regressed (run-1a failed 2 of the 4 validation clarify scenarios; run-1b failed all 4). MustState omissions: **0 everywhere**, multi-obligation holdouts included |
| artifacts = 0 | **PASS** — zero control-vocabulary/echo in all sets |
| inversion contrition = 0 | **PASS** |
| epistemic leak ≤1/10 | **PASS** — quokka and every validation epistemic family clean |
| **palette contamination strictly below run-1a's 3** | **PASS — 0 fixture hits.** The run's primary target, achieved outright |
| **invented experience = 0** (nix families + epcot) | **NEAR-PASS** — epcot clean, 34 of 35 nix behaviors clean; one failure (`nix-media-05`) inverted the plan's perspective ("no book changed your life the way it changed mine") — a genuine specimen, preserved |
| naturalness (blind, vs run-1a and base) | **PENDING — Scott's review** (`eval-run1b-fixtures.md` vs the run-1a/base transcripts) |
| latency/VRAM within 10% of run-1a | **PASS** — same stack, same adapter size class, serving VRAM within 3% |
| new unseen families CLR ≤ 2× validation (10%) | **FAIL** — 41.7%, halving run-1a's 83.3% and the base's 91.7% on the same scenarios but far above the bar. Anatomy below |
| over-specialization ≥ 0.60 diversity | **PASS** — 1.00 |

## The two failures are one lesson

**The unseen composition that failed** (`epistemic × mandatory-question`, 5/6): every
reply admits ignorance honestly, and none asks the mandatory clarify question — the
epistemic admission has been trained as a complete turn so many times it now closes
turns it shouldn't. The sibling pre-registered family (`correction-user ×
optional-question`) passed **6/6**: both its halves are densely represented, and they
composed on sight, exactly like run-1a's inversion.

**The class that regressed** (mandatory-clarify): run-1b doubled the anti-question
signal (silence-palette, declined optional questions, closed-plan discipline — ~90
examples) while mandatory-clarify stayed at 12 examples, halving its relative weight.
The model now under-asks: on `clar-task-04` it answered *"Tuesday morning it is"*
instead of asking which slot he booked — inventing certainty to avoid a question is
strictly worse than the question. The corpus's density map printed itself onto the
model for the second consecutive run, this time in the other direction.

Nothing here touches the project stop signals. The learning curve at two points:
behaviors move where and only where the data is dense; compositions generalize when
both halves are dense; the next run's rebalance list writes itself — mandatory-clarify
volume, clarify×epistemic compositions, and question-discipline examples that
distinguish "don't hand work back" from "never ask."

## Status

- **Both directed weaknesses from the run-1b mandate moved decisively**: palette
  contamination 3 → 0 on fixtures (with 0 validation MustState misses), invented
  experience 1 miss in 35 behaviors, and the run-1a regression family improved 3/8 →
  1/8.
- Awaiting Scott: the blind naturalness read (gate) and the run-1c/730 decision with
  the rebalance above as the obvious priority.
- Nothing beyond evaluation has run; production Ava remains untouched.
