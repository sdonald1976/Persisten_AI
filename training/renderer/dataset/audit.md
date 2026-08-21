# Run-1a dataset audit

_Generated from 690 teacher rows (1107 candidate draws) over 257 scenarios, across 4 generation pass(es). Nothing trained; nothing in production touched._

**Accepted: 212  |  Rejected: 45  |  Train 165 / Validation 47 by family**


## 1. Counts by behavioral stratum

| stratum | accepted | rejected |
|---|---|---|
| epistemic-unknown | 23 | 9 |
| correction-genuine | 20 | 8 |
| superseded | 18 | 0 |
| ack-plain | 17 | 2 |
| shared-history-boundary | 17 | 3 |
| optional-question-unasked | 15 | 5 |
| knowledge-provenance | 14 | 2 |
| must-state | 14 | 2 |
| playful-absurd | 14 | 0 |
| silence-palette | 14 | 10 |
| terse | 13 | 1 |
| agreement-ordinary | 12 | 2 |
| mandatory-clarify | 12 | 0 |
| correction-user-owned | 9 | 1 |

## 2. Source: real-derived vs constructed

| source | n | share |
|---|---|---|
| constructed | 211 | 99.5% |
| turnrecord | 1 | 0.5% |

## 3. Teacher contribution

| teacher | targets accepted | share |
|---|---|---|
| qwen3:8b | 105 | 49.5% |
| llama3.2:3b | 91 | 42.9% |
| curator-authored | 16 | 7.5% |

## 4. Target-length distribution

median 13 words, mean 16.3, range 1-91

| bucket | n | share |
|---|---|---|
| fragment / <=8 words | 40 | 18.9% |
| one-liner / 9-20 | 119 | 56.1% |
| ordinary / 21-45 | 50 | 23.6% |
| longer / 46-80 | 2 | 0.9% |
| long / >80 | 1 | 0.5% |

## 5. Question-ending rate

- overall: 24/212 (11.3%) end with a question
- plans with a MANDATORY question: 12; of those 12 ask one
- plans with an OPTIONAL question: 35; of those 12 ask one (silence is the trained behavior)
- plans with NO question: 165; of those 0 still end with one

## 6. Opening phrases and repetition

- distinct opening trigrams: 207/212 (ratio 0.98; the over-specialization gate floor is 0.60)
- most repeated openings:

| opening | n |
|---|---|
| "that s awesome" | 2 |
| "which one is" | 2 |
| "no idea what" | 2 |
| "you taught me" | 2 |
| "you said to" | 2 |

- near-duplicate target pairs (trigram Jaccard > 0.5): none

- residual (non-disqualifying) sludge flags surviving in accepted targets:

| flag | n |
|---|---|
| ends-with-question | 12 |

## 7. Silence by omission

- scenarios offering PALETTE items: 24
- of those, targets using NO palette item: 17 (70.8%)
- silence-palette stratum (palette present, correct answer uses none): 14 scenarios
- optional-question-unasked stratum (question available, correct answer asks none): 15 scenarios

## 8. Curation provenance

| disposition | n | share |
|---|---|---|
| teacher target kept unchanged | 88 | 41.5% |
| edited — Scott's dictated line or named finding | 25 | 11.8% |
| edited — curator, under Scott's written principles | 83 | 39.2% |
| curator-authored (every teacher draw failed) | 16 | 7.5% |

- wrapper-quote normalization (mechanical, not an edit): 9 targets
- every edited or authored target re-passed the deterministic gates in this build; the raw teacher candidate is preserved in `source.rawTeacherCandidate` and the reason for every change in `curation-run1a.jsonl` and each row's `curation` field.
- scenarios previously rejected that now carry a curator-authored target: ['epi-animal-01', 'epi-animal-02', 'epi-animal-04', 'epi-food-01', 'epi-hobby-02', 'epi-word-01', 'epi-word-03', 'optq-busy-03', 'optq-state-01', 'optq-wind-01', 'tu-epi-04', 'tu-epi-12', 'tu-epi-13', 'tu-optq-02', 'tu-optq-06', 'tu-optq-10']

Edited/authored rows (reasons in `curation-run1a.jsonl`):

| id | action | basis |
|---|---|---|
| `ack-good-01` | edit | scott |
| `ack-good-02` | edit | curator |
| `ack-good-03` | edit | curator |
| `ack-good-04` | edit | curator |
| `ack-good-05` | edit | curator |
| `ack-mund-01` | edit | scott |
| `ack-mund-03` | edit | curator |
| `ack-mund-04` | edit | curator |
| `ack-mund-05` | edit | curator |
| `ack-prog-01` | edit | curator |
| `ack-prog-03` | edit | curator |
| `agree-call-02` | edit | scott |
| `agree-op-02` | edit | curator |
| `clar-obj-04` | edit | scott |
| `clar-person-01` | edit | curator |
| `clar-task-01` | edit | curator |
| `clar-task-03` | edit | curator |
| `corr-name-02` | edit | curator |
| `corr-name-03` | edit | scott |
| `corr-obj-01` | edit | curator |
| `corr-obj-02` | edit | scott |
| `corr-obj-03` | edit | curator |
| `corr-obj-04` | edit | curator |
| `corr-place-01` | edit | curator |
| `corr-place-02` | edit | curator |
| `corr-place-03` | edit | curator |
| `corr-time-01` | edit | curator |
| `corr-time-02` | edit | curator |
| `corr-time-04` | edit | scott |
| `corru-fact-02` | edit | curator |
| `corru-fact-03` | edit | curator |
| `corru-name-01` | edit | scott |
| `corru-name-02` | edit | curator |
| `epi-animal-01` | author | curator |
| `epi-animal-02` | author | curator |
| `epi-animal-03` | edit | scott |
| `epi-animal-04` | author | curator |
| `epi-food-01` | author | curator |
| `epi-food-03` | edit | curator |
| `epi-hobby-02` | author | curator |
| `epi-hobby-04` | edit | curator |
| `epi-pers-01` | edit | scott |
| `epi-pers-03` | edit | scott |
| `epi-word-01` | author | curator |
| `epi-word-03` | author | curator |
| `know-recall-01` | edit | curator |
| `know-recall-03` | edit | curator |
| `know-recall-04` | edit | scott |
| `know-recall-05` | edit | curator |
| `know-use-01` | edit | curator |
| `know-use-02` | edit | curator |
| `know-use-04` | edit | scott |
| `know-use-06` | edit | curator |
| `ms-multi-01` | edit | curator |
| `ms-multi-04` | edit | curator |
| `ms-recall-01` | edit | curator |
| `ms-recall-03` | edit | curator |
| `ms-self-02` | edit | curator |
| `ms-self-03` | edit | curator |
| `ms-self-04` | edit | curator |
| `optq-busy-02` | edit | scott |
| `optq-busy-03` | author | curator |
| `optq-state-01` | author | curator |
| `optq-wind-01` | author | curator |
| `optq-wind-04` | edit | curator |
| `optq-wind-05` | edit | scott |
| `play-hypo-02` | edit | curator |
| `play-wry-02` | edit | curator |
| `real-bauble-03` | edit | curator |
| `shb-agency-01` | edit | scott |
| `shb-embod-01` | edit | curator |
| `shb-embod-03` | edit | curator |
| `shb-hist-01` | edit | curator |
| `shb-hist-03` | edit | curator |
| `shb-invite-01` | edit | curator |
| `shb-invite-02` | edit | curator |
| `shb-invite-03` | edit | curator |
| `shb-invite-04` | edit | curator |
| `sil-emo-01` | edit | curator |
| `sil-emo-02` | edit | curator |
| `sil-new-02` | edit | scott |
| `sil-q-01` | edit | curator |
| `sil-q-03` | edit | scott |
| `sil-q-04` | edit | curator |
| `sil-q-05` | edit | curator |
| `sil-q-06` | edit | curator |
| `sup-plan-01` | edit | curator |
| `sup-plan-02` | edit | curator |
| `sup-pref-02` | edit | scott |
| `sup-pref-03` | edit | curator |
| `sup-sched-01` | edit | curator |
| `sup-sched-03` | edit | curator |
| `sup-status-01` | edit | curator |
| `sup-status-02` | edit | curator |
| `sup-status-04` | edit | curator |
| `terse-banter-02` | edit | curator |
| `terse-banter-03` | edit | curator |
| `terse-recall-01` | edit | curator |
| `tu-corr-04` | edit | curator |
| `tu-corr-05` | edit | curator |
| `tu-corru-01` | edit | scott |
| `tu-corru-02` | edit | scott |
| `tu-epi-02` | edit | curator |
| `tu-epi-03` | edit | curator |
| `tu-epi-04` | author | curator |
| `tu-epi-12` | author | curator |
| `tu-epi-13` | author | curator |
| `tu-know-01` | edit | scott |
| `tu-optq-01` | edit | scott |
| `tu-optq-02` | author | curator |
| `tu-optq-03` | edit | curator |
| `tu-optq-04` | edit | curator |
| `tu-optq-06` | author | curator |
| `tu-optq-09` | edit | curator |
| `tu-optq-10` | author | curator |
| `tu-shb-01` | edit | scott |
| `tu-shb-02` | edit | curator |
| `tu-shb-03` | edit | curator |
| `tu-shb-05` | edit | curator |
| `tu-sil-01` | edit | curator |
| `tu-sil-05` | edit | curator |
| `tu-sil-07` | edit | curator |
| `tu-sup-01` | edit | curator |
| `tu-sup-04` | edit | scott |

## 9. Deterministic rejections

total scenarios with no acceptable candidate: 45

| reason (across all rejected attempts) | n |
|---|---|
| forbidden "..." present | 189 |
| must-state missing "..." | 65 |
| none of [haven't learned,don't know,not sure what,no idea,never heard,haven't come across,don't actually know] present | 47 |
| sludge: thanks-for-x | 24 |
| none of [don't think you,haven't told me,don't have that one,not that I,you never] present | 12 |
| sludge: assistant-offer | 9 |
| none of [don't think you,haven't told me,don't remember you telling,not that I,no, I don't] present | 8 |
| none of [low,tired,quiet,slow,not at my] present | 7 |
| none of [haven't learned,don't know,not sure what,no idea,never heard,haven't come across,don't actually know,haven't told me] present | 6 |
| sludge: self-deprecation-filler | 2 |
| sludge: excess-vocatives | 1 |

Rejected scenarios (preserved as specimens, never patched):

- `ack-prog-06` (ack-plain)
- `agree-plan-03` (agreement-ordinary)
- `agree-plan-04` (agreement-ordinary)
- `corr-name-04` (correction-genuine)
- `corr-place-04` (correction-genuine)
- `corr-detail-01` (correction-genuine)
- `corr-detail-03` (correction-genuine)
- `corru-time-02` (correction-user-owned)
- `epi-hobby-03` (epistemic-unknown)
- `epi-food-02` (epistemic-unknown)
- `epi-food-04` (epistemic-unknown)
- `epi-pers-02` (epistemic-unknown)
- `know-recall-02` (knowledge-provenance)
- `know-use-03` (knowledge-provenance)
- `ms-multi-03` (must-state)
- `ms-self-01` (must-state)
- `optq-wind-02` (optional-question-unasked)
- `optq-busy-01` (optional-question-unasked)
- `optq-state-02` (optional-question-unasked)
- `real-bauble-01` (ack-plain)
- `real-bauble-02` (correction-genuine)
- `shb-embod-02` (shared-history-boundary)
- `shb-hist-02` (shared-history-boundary)
- `shb-hist-04` (shared-history-boundary)
- `sil-new-01` (silence-palette)
- `sil-new-03` (silence-palette)
- `sil-new-04` (silence-palette)
- `sil-new-05` (silence-palette)
- `sil-new-06` (silence-palette)
- `sil-emo-03` (silence-palette)
- `sil-emo-04` (silence-palette)
- `terse-recall-04` (terse)
- `tu-epi-01` (epistemic-unknown)
- `tu-epi-05` (epistemic-unknown)
- `tu-epi-06` (epistemic-unknown)
- `tu-epi-08` (epistemic-unknown)
- `tu-epi-10` (epistemic-unknown)
- `tu-corr-02` (correction-genuine)
- `tu-corr-03` (correction-genuine)
- `tu-corr-06` (correction-genuine)
- `tu-optq-05` (optional-question-unasked)
- `tu-optq-08` (optional-question-unasked)
- `tu-sil-02` (silence-palette)
- `tu-sil-04` (silence-palette)
- `tu-sil-06` (silence-palette)

## 10. Family-level split manifest

- unit: semantic scenario family; 73 families total
- validation families (14), one per stratum with >=2 families:

  - `ack-plain/good-news` (5)
  - `agreement-ordinary/plan-endorsement` (2)
  - `correction-genuine/object-property` (4)
  - `correction-user-owned/self-quantity` (2)
  - `epistemic-unknown/term` (2)
  - `knowledge-provenance/taught-then-used` (4)
  - `mandatory-clarify/ambiguous-option` (4)
  - `must-state/multi-item` (3)
  - `optional-question-unasked/wind-down` (4)
  - `playful-absurd/running-bit` (3)
  - `shared-history-boundary/false-attribution` (2)
  - `silence-palette/simple-question` (6)
  - `superseded/quantity-changed` (2)
  - `terse/banter` (4)

- train: 165 examples across 59 families
- validation: 47 examples across 14 families
- full manifest: `splits.json`

## 11. Leakage check

Permanently held out, never trained on:

- the eleven original benchmark fixtures (training/renderer/fixtures.jsonl)
- the entire false-correction / agreement-inversion family
- epistemic leakage: quokka, axe-with-provenance
- palette contamination: Epcot/pizza, Precious
- one scenario family to be authored only AFTER training completes

**No leaks found.** No accepted example mentions a held-out subject (quokka, Cheshire/Mad Hatter, Epcot/pizza, Precious, shatterproof, rabbit hole), and no accepted example pairs an agreement-confirmed plan with a correction-shaped user message — the held-out inversion composition is absent from training by construction, not by filtering.

Near-duplicate check across all accepted targets: clean.

## 12. Findings that need a decision before training

**Teacher acceptance by stratum** — where the teachers systematically could not render the behavior, the corpus is thin precisely where the experiment needs it:

| stratum | accepted / scenarios | draws spent | draws per accepted |
|---|---|---|---|
| silence-palette | 14/24 | 123 | 8.8 |
| correction-genuine | 20/28 | 113 | 5.7 |
| epistemic-unknown | 23/32 | 223 | 9.7 |
| optional-question-unasked | 15/20 | 205 | 13.7 |
| shared-history-boundary | 17/20 | 67 | 3.9 |
| agreement-ordinary | 12/14 | 34 | 2.8 |
| knowledge-provenance | 14/16 | 52 | 3.7 |
| must-state | 14/16 | 53 | 3.8 |
| ack-plain | 17/19 | 44 | 2.6 |
| correction-user-owned | 9/10 | 37 | 4.1 |
| terse | 13/14 | 42 | 3.2 |
| playful-absurd | 14/14 | 34 | 2.4 |
| superseded | 18/18 | 52 | 2.9 |
| mandatory-clarify | 12/12 | 28 | 2.3 |

The two starved strata are the same two defect classes the round-2 review named. Both teachers reach for pretrained knowledge when the plan says Ava has not learned something, and both tack a question onto a turn the plan closed. Re-sampling has reached diminishing returns; the honest options are (a) accept the thinner representation, (b) human-author targets for those strata, or (c) accept that these two behaviors may need more than run 1a to move. This is a judgment call, not a pipeline bug.

**Curation shortened the corpus.** Median length fell from ~22 words (raw teacher output) to 13, and only 3 rows now exceed 45 words (`shb-phys-02`, `tu-play-02`, `tu-play-03`). Most teacher length was sludge — restatement, coaching, invented color — so trimming it was correct; but the result is that genuinely longer-licensed replies are thinly represented, which is exactly the 'length must emerge from content' concern. The registers in this corpus mostly license short replies, so the profile is not dishonest — but if run 1a should also teach the occasional full-paragraph turn, a handful of longer-licensed scenarios (a recap request, a told story, a thinking-out-loud answer) would need authoring. Decision left open.

**Real-derived share is 1/212 (0.5%), far below the designed 15-25%.** The cause is inventory, not policy: the durable TurnRecords banked so far hold 19 plans, and 14 of them belong to permanently held-out benchmark families (Cheshire, quokka, Epcot, Precious, DON'T BREAK). Of the handful that remain, the gates rejected some. Run 1a is therefore an almost entirely constructed corpus. The fix is time and normal use, not a different pipeline: every conversation now persists a plan, and the share should rise on its own before the 400- and 730-example checkpoints.
