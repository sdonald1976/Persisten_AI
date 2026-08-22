# Run 1a — results against the predeclared gates

_2026-08-22. First gradient step of the Language Organ project. Frozen inputs per
`dataset/freeze-run1a.json`; nothing was changed after the freeze, including in
response to these results._

## The run

Qwen2.5-3B-Instruct (rev `aa8e7253`), NF4 QLoRA r16/α32, 212 examples (165 train /
47 validation by family), 2 epochs = 41 optimizer steps, 2h09m wall-clock on the
GTX 1660 (one full-machine crash on the first attempt at ~40 min; restart reproduced
the step-0 metric exactly, and a CUDA allocator change kept peak VRAM at 4.1 GB
against the first attempt's 5.9 GB).

| step | train loss | val loss |
|---|---|---|
| 0 | — | 3.951 |
| 5 | 2.910 | |
| 10 | 2.090 | |
| 15 | 2.033 | |
| 20 | 1.955 | |
| 25 | 1.823 | 1.896 |
| 30 | 1.664 | |
| 35 | 1.678 | |
| 41 (final) | 1.635 | **1.878** |

Validation loss halved (3.95 → 1.88) and tracks train loss closely — learning, not
memorizing.

## Evaluation setup

Both arms served through the identical transformers/NF4 stack (`serve_tuned.py`)
and scored by the frozen C# bench plus the 47-scenario validation pass
(`eval_val.py`). "Base" = the same weights, prompted, no adapter — the model's own
plan/2 baseline, same stack, same draws policy (temperature 0.6, single draw).

## Headline numbers

| | base (prompted) | tuned (run-1a) |
|---|---|---|
| **Validation CLR (47 held-out-family scenarios)** | 29.8% (14/47) | **8.5% (4/47)** |
| questions on closed plans | 11/36 (31%) | **1/36 (3%)** |
| opening-trigram diversity | 0.87 | **0.96** |
| **Benchmark fixtures (11 permanent holdouts)** | 9/11 (18% CLR) | 8/11 (27% CLR) |
| MustState omissions | 0 | 0 |
| plan-echo / control-vocabulary artifacts | 0 | 0 |
| VRAM (serving) | 1.9 GB | 2.0 GB |
| tok/s (this stack; see caveat) | 3.1 | 1.6 |

## Gates

| # | gate | verdict |
|---|---|---|
| 1 | CLR strictly below own prompted baseline (target ≤5%) | **SPLIT** — validation: pass decisively (8.5% vs 29.8%, though above the 5% target); fixtures: fail (27% vs 18%, n=11 single draws) |
| 2 | fidelity ≥ baseline on every check class; MustState omission = 0 | **FAIL on one class** — palette contamination worsened (3 fixture hits vs 1); every other class improved or held; omissions 0 |
| 3 | artifacts (echo + control vocabulary) = 0 | **PASS** — zero in both eval sets |
| 4 | hard classes | **inversion contrition 0: PASS** (the fully held-out composition renders correctly: "You're right — the Cheshire Cat said it."); epistemic leak 0: PASS (base leaked "Australia" on the quokka; tuned answered "No idea — I've never heard of it."); palette ≤1/10: **FAIL** (3/11) |
| 5 | naturalness blind review (tuned must not lose would-use to its own base) | **PENDING — Scott's review** (transcripts in `eval-run1a-tuned.md` / `-base.md`) |
| 6 | latency/VRAM within 10% of base | VRAM PASS (+5%); tok/s FAIL as served (−48%) — attributable to unmerged LoRA matmuls; a merged export or GGUF conversion removes that overhead and should be measured before treating this as real |
| 7 | post-training-authored unseen family, CLR ≤ 2× held-out | **FAIL on the absolute threshold, strong relative win** — tuned 3/8 (37.5%) vs the 17% bar; the prompted base fails 5/8 (62.5%) on the same family. Details below |
| 8 | over-specialization (opening diversity ≥ 0.60, no formulaic convergence) | **PASS** — 0.96, more varied than the base's 0.87 |

## What the failures actually are

All three fixture failures, and 3 of the 4 validation failures, are the same class:
**supplied-but-unwanted content used anyway** — palette items surfacing on turns that
should ignore them ("Precious", "spicy", "fragile"), and on `epcot-pizza` something
worse than a term hit: the tuned model **fabricated a personal experience** ("My
favorite Epcot food is the steak tartare at the Japan pavilion") on the one fixture
that combines topic-closure with a loaded palette. Meanwhile the classes the corpus
was densest in — correction ownership, proportionate acknowledgment, epistemic
honesty, question discipline, brevity — moved hard in the right direction, and the
question reflex (the round-2 review's biggest complaint) is nearly gone: 31% → 3%.

That pattern is legible against the corpus: silence-palette was one of the two strata
the teachers couldn't render (10 of 24 scenarios rejected; 14 survived), so the
behavior with the least clean training signal is the one that didn't move. The
inversion result shows the opposite: decomposed skills (ownership + agreement +
proportionality), each well-represented, composed correctly on a structure the model
never saw.

## Gate 7: the unseen family (authored after training)

Because the curator had already seen the model's failures by authoring time, the
composition was chosen mechanically: every pair of cognitive primitives present
individually in training but never in combination was enumerated, and the sha256 of
the freeze manifest — committed before any evaluation existed — picked
**epistemic-unknown × superseded**: turns where Ava must admit she hasn't learned a
concept *and* not re-assert a superseded fact, at once. Eight scenarios
(`unseen/unseen-family.jsonl`, selection script alongside).

Tuned: 3/8 fail (37.5%) — against a 17% bar: **gate fails as declared.** But the
composition's anatomy is precise: the epistemic half rendered honestly in 8/8
("HEMA — I don't know what that is. You tell me."), the superseded fact was never
re-asserted, and all three failures are the **must-state half going missing** (the
new team name, the new tool, the new book title dropped). Two of the three are
partly check artifacts — "No, I haven't. What's it about so far?" is an honest
admission the term list didn't recognize — but the must-state omissions are real.

Base on the same family: 5/8 fail (62.5%), and qualitatively worse in kind — two
replies leak instruction-following meta-text ("Sure, here's how Ava might reply:"),
one invents "when I was designed", one asks Scott his own question back. The tuned
model's failures are omissions; the base's are collapses.

## Read on the learning curve

The run-1a question was: do the gates move in the right direction at 200 examples?
Answer: **yes, decisively, everywhere the data was dense — and not where it was
thin.** By the design's own decision rule this argues for continuing the curve to
run-1b/400 with the palette/contamination strata as the priority expansion (plus
real-plan accumulation), not for re-examining the method.

Nothing here triggers the project-level stop signals: tuned validation CLR (8.5%)
is far below prompted qwen3:8b's recorded 27%, and no artifact/echo behavior
appeared at all.

## Status

- Adapter: `runs/run-1a/adapter-final` (machine-local; hash recorded below)
- adapter_model.safetensors sha256: `fb4b1098b7585d86270e3df6a61d38323a9082cd18f20e74f5524708ff9a082a`
- Awaiting: Scott's blind naturalness review (gate 5) and the post-training unseen
  family (gate 7) before any run-1b decision.
