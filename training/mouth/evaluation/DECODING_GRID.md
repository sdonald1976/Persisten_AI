# Decoding grid: the collapse is not a decoding problem

Run at `17ddee9`. No configuration was selected; the canary stays off.

## Rollback, verified first

| | |
|---|---|
| `demo-user` reply | production (Stheno) |
| canary stage in turn record | **none** — production path |
| run-1c `activeRenderer` | `production`, canaryUser `null` |
| mouth `observing` | **true** |
| mouth `activeRenderer` | **shadow only** |

Canary diagnostics preserved at `evaluation/canary-run1/`: counters (12 rendered, 12 displayed,
0 fallback), the API run log, and the durable shadow rows in `companion.db`.

## The grid

Eight configurations over 46 validation rows. Selection used **validation only** — test and
hard-eval stayed closed, and the two live failure shapes were recreated by structure.

| config | relevance | distinct | openings | endings | question | unsupported | median | p50 |
|---|---|---|---|---|---|---|---|---|
| **current-greedy** | 0.739 | 0.913 | 0.804 | 0.783 | 0.949 | 2.91 | 15 | 4174 ms |
| greedy-rep1.10 | 0.717 | 0.891 | 0.848 | 0.761 | 0.949 | 3.98 | 14 | 3743 ms |
| greedy-rep1.15-ng4 | 0.717 | 0.913 | 0.848 | 0.870 | **0.872** | **5.98** | 17 | 4464 ms |
| t0.3-p0.9 | 0.761 | 0.870 | 0.761 | 0.761 | 0.949 | 2.67 | 15 | 3772 ms |
| t0.5-p0.9 | 0.761 | 0.913 | 0.804 | 0.783 | 1.000 | 3.02 | 15 | 4124 ms |
| t0.7-p0.9 | 0.783 | 0.891 | 0.783 | 0.761 | 1.000 | 2.89 | 15 | 4074 ms |
| t0.5-p0.9-rep1.10 | 0.739 | 0.848 | 0.804 | 0.696 | 0.974 | 4.67 | 15 | 3878 ms |
| t0.7-p0.92-rep1.15-ng4 | 0.761 | 0.913 | 0.826 | 0.848 | **0.846** | **6.93** | 16 | 4444 ms |

**No configuration improves both topical relevance and diversity.** `t0.7-p0.9` gains 4.4 points
of relevance and loses 2.2 of distinct replies; `t0.5-p0.9` gains 2.2 of relevance with diversity
flat. On 46 rows those margins are one or two replies — noise, not signal, and selecting on them
would be fitting the grid rather than fixing the model.

**Anti-repetition settings trade directly against fidelity.** Repetition penalty 1.15 with
no-repeat-ngram 4 moves unsupported words per reply from 2.91 to 5.98 and question compliance from
0.949 to 0.872. That is the mechanism working exactly as it must: told not to reuse tokens it has,
the model reaches for tokens the plan never supplied. The `b4` weakness is *made worse* by the
setting that was supposed to help the stubs.

## What the subset could not reproduce, and why that is the answer

| | distinct replies | distinct openings |
|---|---|---|
| validation subset, current greedy | **91.3%** | **80.4%** |
| hard-eval, same model, same decoding | **26.2%** | **9.8%** |

Every validation stratum sits between 83% and 100%:

| stratum | n | distinct | openings |
|---|---|---|---|
| no-must reactions | 8 | 100% | 100% |
| forbidden-question | 8 | 100% | 100% |
| `b4` structure | 6 | 83% | 83% |
| `b6` structure | 6 | 100% | 67% |
| stock-closer risk | 8 | 100% | 100% |
| narrow subject | 8 | 100% | 100% |
| **ambiguity / unknown** | **2** | **50%** | **50%** |

There is no collapse in validation to fix. The one stratum that shows any is the one with two rows
in it.

## The cause, and it is a corpus decision rather than a decoding one

The collapse is confined to a single composition: **question forbidden, with an unresolved
ambiguity or an admitted unknown.**

| split | rows | carrying that composition |
|---|---|---|
| validation | 213 | **2** |
| hard-eval | 61 | **61** |
| **train** (of the exported 2,000) | 1,616 | **0** |

Every `hardCase` row in the accepted pool — all 61 — was routed to the hard/evaluation split. Of
the 2,000 rows exported for training, **zero** carry the composition run-2 collapses on.

Run-2 has never seen this structure. It is not degrading under greedy decoding; it is
extrapolating, and it extrapolates to a stub.

That routing was a decision made here, taken from the two options offered when the accepted-quota
mix was set: put difficult forbidden-question compositions in the hard/evaluation split, *or*
select harder examples within the 63.3% allocation. The first was chosen, which kept the main
corpus production-shaped and left the hardest composition entirely untrained. The consequence is
now measured.

## Conclusion

**Decoding does not fix the collapse, and no viable configuration was found that improves
relevance and diversity together.** The canary was not restored; run-2 remains in shadow, and
`demo-user` remains on production run-1c.

This is not the "every configuration still produces generic or unrelated replies" case exactly —
on the composition it was trained for, run-2 is fine: 91.3% distinct, 93.0% plan/4-clean, 100%
naturalness. The failure is narrower and more specific than that, which makes a targeted Run-2.1
correction the right next move rather than a corpus rebuild.

The correction that the evidence points at: **train the hard composition instead of only
evaluating it.** That means generating question-forbidden turns carrying an ambiguity or an
admitted unknown into the *training* split, and keeping a disjoint set held out — not moving the
existing 61 rows across, which would leave nothing to measure against.

Nothing was retrained, regenerated, or rolled out.
