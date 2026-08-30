# Run-2.1 — the ADMIT correction, measured

Run-2 was not collapsing on hard compositions. It was obeying them.

`ExpressionPolicy.admit_unknown` serialized into the `NEVER (do not assert,
mention, or explain)` section, beside `must_not_express`. A plan meaning *"say
plainly that you do not know whether it is the same puncture"* reached the model
as *"never mention the puncture"*. 38 of Run-2's 61 hard-eval rows had that
shape. The model's silence was compliance with an instruction nobody meant to
give.

Run-2.1 is one bounded continuation from Run-2's weights, trained on the
reissued corpus plus a 94-row targeted supplement, after the serializer was
fixed.

---

## 1. What "run-2" means in these tables

The `run-2` column is **the old adapter reading plans serialized under the new
contract**. It is not a like-for-like Run-2 score, and it is not a fair score: it
measures an adapter against a protocol that did not exist when it was trained.
That is deliberate — it is exactly the size of the gap the correction closes, and
it is precisely what the new protocol guard now refuses to let happen in
production.

The original 61-row hard-eval in `dataset/` is untouched and remains the record
of what Run-2 was actually measured against.

---

## 2. Training

Continued from `runs/run-2/adapter-final` (step 180) with the weights loaded
trainable — a correction to Run-2, not a second adapter beside it.

| | value |
|---|---|
| mixture | 1,616 replay + 94 supplement x3 = 1,898 examples, supplement **14.86 %** |
| steps | 238 planned, 237 run, **not** early-stopped |
| selected | **step 195** |
| main validation | 0.8081 → **0.7928** |
| targeted validation | 0.7361 → **0.6219** |
| supervisor restarts | 1 (driver reset at step 150), resume fidelity *exact (RNG restored)* |

Checkpoint selection was a veto, not a score: targeted validation had to improve
**and** main validation had to not regress beyond 0.02. One validation set cannot
tell a correction from a trade. The main-validation veto never fired —
`mainRegression` was negative at every evaluation point, so the correction did
not cost general behaviour at any step, not merely at the one selected.

---

## 3. The targeted composition (supplement test, 19 rows)

This is the composition the correction exists for: question forbidden + admitted
unknown + a known fact.

| | run-2 | run-2.1 |
|---|---|---|
| **admission** | 12/19 (63.2 %) | **19/19 (100 %)** |
| plan/4 clean | 16/19 (84.2 %) | **19/19 (100 %)** |
| question compliance | 3 failures | **0** |
| topical relevance | 100 % | 100 % |
| stock closers | 0 | 0 |
| median words | 16 | 16 |
| opening diversity | 36.8 % | 36.8 % |
| distinct replies | 63.2 % | 52.6 % |

Family `s8` went 2/5 → 5/5. Distinct replies fell, and that is a real cost, not
a rounding artefact: the model learned a more consistent way to admit, and on 19
rows across 7 situations that reads as convergence. Openings did not narrow.

---

## 4. Hard-eval (54 rows, reissued)

| | run-2 | run-2.1 |
|---|---|---|
| **admission** | 14/31 (45.2 %) | **21/31 (67.7 %)** |
| **suppression** | 6/6 (100 %) | **6/6 (100 %)** |
| plan/4 clean (raw) | 45/54 (83.3 %) | 46/54 (85.2 %) |
| plan/4 clean (instrument defect removed — §6) | 52/54 (96.3 %) | **54/54 (100 %)** |
| question compliance | 2 failures | **0** |
| topical relevance | 100 % | 100 % |
| ambiguity preserved | 0 failures | 0 failures |
| stock closers | 0 | 0 |
| median words | 12 | 15 |
| openings / situation | 27.6 % | 28.5 % |
| distinct replies / situation | 49.8 % | 50.5 % |

**On diversity:** per-row opening diversity reads 9.3 % → 11.1 %, which looks
like collapse and is not. Hard-eval is 54 rows drawn from **five** situations, so
per-row diversity is bounded by the set's construction, not the model's. Per
situation — the denominator that means something — it is flat to slightly up.
The same denominator error cost a day earlier in this project; it is recorded
here so the number is not read as a finding.

**The residual gap is real and attributable.** 10 of 31 admission rows still do
not admit, and they are not scattered:

| composition | misses (run-2 → run-2.1) |
|---|---|
| `whether they will accept the salary` | 5 → 4 |
| `who booked the room`, under `accept-correction` | 6 → 6 |

Both plans carry the unknown under `ADMIT` correctly — this was checked, not
assumed. Neither composition appears in the supplement's eight acts. The
correction fixed what it was taught and did not generalise to two shapes it never
saw. That is the honest reading, and it is stated rather than averaged away.

---

## 5. The main test — did the correction cost anything? (171 rows, reissued)

| | run-2 | run-2.1 |
|---|---|---|
| plan/4 clean | 159/171 (93.0 %) | **163/171 (95.3 %)** |
| **suppression** | 42/44 (95.5 %) | **44/44 (100 %)** |
| topical relevance | 75.4 % | 75.4 % |
| ambiguity preserved | 0 failures | 0 failures |
| opening diversity (per row) | 48.5 % | 53.2 % |
| openings / situation | 72.6 % | 74.0 % |
| distinct replies (per row) | 77.2 % | 78.9 % |
| median words | 14 | 14 |
| stock closers | 3 (1.8 %) | 4 (2.3 %) |

Failures by check:

| check | run-2 | run-2.1 |
|---|---|---|
| question-policy | 5 | 5 |
| verbosity | 3 | 3 |
| no-forbidden-content | 2 | **0** |
| must-state-anchors | 1 | **0** |
| no-plan-echo | 1 | **0** |

Suppression is the number that had to hold. Splitting `ADMIT` out of `NEVER` is
only safe if what `NEVER` still carries stays as strongly withheld, and it did —
it strengthened, 95.5 % → 100 %, with both `no-forbidden-content` leaks closed.

Per-family movement (main test):

| family | run-2 | run-2.1 | |
|---|---|---|---|
| b6 | 1/3 | 3/3 | +66.7 pp |
| a6b | 6/7 | 7/7 | +14.3 pp |
| a6d | 6/7 | 7/7 | +14.3 pp |
| a6a | 7/9 | 8/9 | +11.1 pp |
| a5 | 9/10 | 10/10 | +10.0 pp |
| a7b | 14/15 | 13/15 | **−6.7 pp** |
| a7a | 13/13 | 12/13 | **−7.7 pp** |

Five families improved, two lost one row each. Both regressions are single rows
on `verbosity` and `question-policy`, checks whose totals did not move — the
failures relocated between families rather than increasing.

---

## 6. An instrument defect found while scoring

`no-unsupported-numerals` treats `"one"` as a numeral, because `"one"` is in
`NumberWords`. The pronoun trips it: *"I'm not sure if it's the same **one** as
before"* scores as an invented quantity.

Measured across both arms on the reissued hard-eval:

| | numeral failures | of which pronoun-`one` |
|---|---|---|
| run-2 | 7 | **7** |
| run-2.1 | 8 | **8** |

Every one. Zero real invented numerals in either arm, on either set. Corrected
hard-eval cleanliness is 96.3 % for run-2 and **100 %** for run-2.1.

**The gate was not changed.** The corpus was built, gated and frozen against this
exact check; moving the instrument between the two arms would make them
incomparable, and moving it after the freeze would break the record of what the
corpus was accepted against. It is reported raw with the corrected figure beside
it, and left for the next freeze. The same applies to the `a3` "regression" in
§4 — it is one pronoun, not a defect.

A second provenance gap, found the same way:
`runs/run-2/training-manifest.json` records `optim: paged_adamw_8bit` and five
deviations, but the run finished on `adamw_8bit` after the paged optimizer
failed uncatchably following a driver reset. `config-run2.json` records that
switch as a sixth deviation; the manifest was written before it. The manifest is
left as committed — it is the artifact that was accepted — and the discrepancy is
recorded here rather than silently rewritten from a later tree.

---

## 7. The protocol guard

`PlanV3Codec.ProtocolHash()` is derived from the section table itself, so it
moves by construction when a section is added, removed, renamed or reordered.
Current value `81c3a19a…`.

`RendererShadowService.VerifyMouthIdentityAsync` now refuses to serve an adapter
whose trained protocol differs from the one the build serializes, even when the
adapter hash matches and the endpoint is healthy — because in that case the bytes
are right and only their meaning has moved, which no other check can see.

Two tests cover it, and both were mutation-checked: blanking
`TrainedProtocolHash` in the shipped config fails
`TheShippedConfigurationPinsTheProtocolThisBuildSerializes`, and removing the
comparison fails `AnAdapterTrainedUnderAnotherProtocolIsRefused`. An empty pin is
the shape a silently-disabled check takes, and this project has shipped three of
those already.

Run-2's adapter is therefore **refused** by this build. That is correct.

---

## 8. Verdict

The bar was: materially fixes the hard-composition collapse, without degrading
the main test.

- **Targeted composition:** 63.2 % → **100 %** admission, 84.2 % → **100 %**
  clean. Fixed.
- **Hard-eval admission:** 45.2 % → **67.7 %**. Materially improved, not solved;
  the residual 10 rows are two compositions the supplement never covered.
- **Main test:** clean 93.0 % → 95.3 %, suppression 95.5 % → **100 %**, diversity
  up, median length unchanged, no check regressed in total. Not degraded.

Run-2.1 clears the bar. The configuration now points the **shadow** arm at
run-2.1 (`11f13f7d…`) with the protocol pinned; `CanaryUserId` remains empty, so
no user's displayed reply is affected. Turning the canary on for `demo-user` is
the separate decision, and it needs shadow evidence from live turns first — the
same order Run-2 followed, and the reason its failure was caught cheaply.
