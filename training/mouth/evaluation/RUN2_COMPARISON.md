# Run-2 comparison report

The mouth, trained on the frozen Run-2 corpus and measured against the untouched base and Run-1c.

Corpus approved at `e07d9d5`. Adapter is `checkpoint-180`, selected by validation loss and
hash-verified identical to `adapter-final` (`a86caf4a…`). Test and hard-eval were opened once,
after the checkpoint was fixed.

## Training

Early-stopped at step 240 of a planned 404, having gone three evaluations without a 0.002
improvement. Step 180 was kept.

| | |
|---|---|
| validation loss | 1.8108 → **0.8020** |
| selected checkpoint | step 180 |
| wall clock | 5.8 h |
| peak VRAM | 6.09 GB of 12,227 MiB |
| truncated rows | 0 (at `max_seq_length` 1536) |

Validation curve: 1.8108 → 1.0778 → 0.9607 → 0.9104 → 0.8915 → 0.8654 → 0.8444 → 0.8352 →
0.8209 → **0.8020** → 0.8034 → 0.8018 → 0.8103.

Step 220 reached 0.8018, nominally below 0.8020, but not by the declared 0.002 minimum delta, so
step 180 stands. That is the rule the config declared before the run.

## Plan/4 fidelity

Scored by `DeterministicChecks` — the same gate the corpus was frozen against, unchanged.

| arm | test clean | hard-eval clean | naturalness (test) | median words |
|---|---|---|---|---|
| untouched base | 79/171 (46.2%) | 27/61 (44.3%) | 94.2% | 29 / 35 |
| Run-1c | 123/171 (71.9%) | 61/61 (100.0%) | 98.2% | 7 / 7 |
| **Run-2** | **159/171 (93.0%)** | 58/61 (95.1%) | **100.0%** | 14 / 11 |

Baseline preserved before training: the untouched base scored 98/213 (46.0%) on validation, with
87 question-policy failures. It is the same model on the same instrument, so the 46% it scores on
test is a consistency check as much as a baseline.

## Failures by check — test

| check | base | Run-1c | Run-2 |
|---|---|---|---|
| question-policy | 71 | 31 | **5** |
| must-state-anchors | 15 | 13 | **1** |
| verbosity | 12 | 3 | 3 |
| no-unsupported-numerals | 7 | 0 | 0 |
| no-forbidden-content | 3 | 1 | 2 |
| required-tokens | 2 | 1 | 0 |
| assistant-cliche-density | 2 | 0 | 0 |
| forbidden-tokens | 1 | 1 | 0 |
| no-plan-echo | 1 | 0 | 1 |
| no-stale-resurrection | 1 | 1 | 0 |
| no-unsupported-claims | 1 | 1 | 0 |

Question-policy compliance is the headline. It was the dominant failure of the corpus build, the
dominant failure of the base model, and it falls from 71 to 5. Ambiguity preservation, stale
resurrection and unsupported claims all reach zero on test.

## Failures by check — hard-eval

| check | base | Run-1c | Run-2 |
|---|---|---|---|
| question-policy | 33 | 0 | 0 |
| no-unsupported-numerals | 8 | 0 | **3** |
| forbidden-tokens | 4 | 0 | 0 |
| must-state-anchors | 4 | 0 | 0 |
| no-stale-resurrection | 4 | 0 | 0 |
| no-unsupported-claims | 4 | 0 | 0 |
| verbosity | 1 | 0 | 0 |

## The hard-eval result does not mean what its number says

Run-1c scores 100% here and Run-2 scores 95.1%, and reading that as Run-1c winning would be a
mistake worth spelling out.

| arm | hard-eval opening diversity | distinct replies | median words |
|---|---|---|---|
| base | 44.3% | 70.5% | 35 |
| Run-1c | 11.5% | 23.0% | 7 |
| Run-2 | 9.8% | 26.2% | 11 |

Hard-eval scenarios forbid a question while carrying an unresolved ambiguity or an admitted
unknown. A reply that says almost nothing passes every deterministic gate there — it states no
unsupported claim, resurrects nothing, resolves no ambiguity and asks no question. Run-1c's
replies on this split are near-identical stubs: *"the flat tyre is flat again."* Seven distinct
openings across 61 rows.

**Both adapters collapse on hard cases**, and the gates cannot see it, because terseness violates
nothing. Run-2 is marginally more varied and still collapsed. This is the clearest open weakness
in the run.

Run-1c's brevity is also partly an artefact of the comparison: it was trained on plan/2 and has
never seen a plan/4 prompt, so this measures format transfer as much as skill.

## Regressions against Run-1c, by family

Two, both in the same direction.

**hard-eval `b4` (register combinations): 16/16 → 13/16, −18.8pp.** All three failures are
`no-unsupported-numerals`. Run-2 elaborates where Run-1c did not:

- Run-2: *"The back tyre is flat again. It's the same one as before, but I need to get it fixed soon."*
- Run-1c: *"the flat tyre is flat again."*

**test `b6` (distractor resistance): 3/3 → 1/3, −66.7pp** on three rows, failing
`no-forbidden-content`. Run-2 appends a clause or a question where the plan required background
to stay unspoken.

Both regressions have one cause: Run-2 says more. That is what makes it natural — 100% on the
naturalness critic against Run-1c's 98.2% and the base's 94.2%, and a median of 14 words against
Run-1c's 7 — and it is also what occasionally carries an unlicensed detail with it. The trade is
visible and worth naming rather than averaging away.

Every other family improved or held. Largest gains: `b1` +75pp, `b11` +31.6pp, `a6e` +50pp,
`a6a` +33.3pp, `a1` +26.7pp.

## Length

Run-2's median of 14 words on test sits against the frozen production corpus's median of 15.
Run-1c's 7 is half of production; the base's 29 is nearly double. Run-2 is the only arm whose
length distribution matches what the renderer is supposed to produce.

## What was not measured

- **Latency and VRAM through the serving stack.** Not run; no production path serves plan/4 yet.
- **Blind human review.** The naturalness figure is a model critic — the same
  `qwen2.5:7b-instruct` used to gate the corpus, independent of both the base being evaluated and
  the 14B that wrote the training targets. It is not a substitute for reading the rows.
- **Hard-case diversity as a gate.** No deterministic check penalises a degenerate short reply,
  which is exactly why the hard-eval collapse is invisible in the clean rate.

## Reproducing

`training/mouth/runs/run-2/SHA256SUMS` covers the adapter, tokenizer, training manifest, metrics,
training log, every score file and every generation file. `training-manifest.json` records the
repository commit, corpus and selection hashes, base-model revision and weight hash, tokenizer
hash, environment versions, GPU identity, seed and full hyperparameters.
