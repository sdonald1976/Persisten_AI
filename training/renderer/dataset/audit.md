# Run-1a dataset audit

_Generated from 690 teacher rows (1107 candidate draws) over 257 scenarios, across 4 generation pass(es). Nothing trained; nothing in production touched._

**Accepted: 196  |  Rejected: 61  |  Train 156 / Validation 40 by family**


## 1. Counts by behavioral stratum

| stratum | accepted | rejected |
|---|---|---|
| correction-genuine | 20 | 8 |
| superseded | 18 | 0 |
| ack-plain | 17 | 2 |
| shared-history-boundary | 17 | 3 |
| knowledge-provenance | 14 | 2 |
| must-state | 14 | 2 |
| playful-absurd | 14 | 0 |
| silence-palette | 14 | 10 |
| epistemic-unknown | 13 | 19 |
| terse | 13 | 1 |
| agreement-ordinary | 12 | 2 |
| mandatory-clarify | 12 | 0 |
| correction-user-owned | 9 | 1 |
| optional-question-unasked | 9 | 11 |

## 2. Source: real-derived vs constructed

| source | n | share |
|---|---|---|
| constructed | 195 | 99.5% |
| turnrecord | 1 | 0.5% |

## 3. Teacher contribution

| teacher | targets accepted | share |
|---|---|---|
| qwen3:8b | 105 | 53.6% |
| llama3.2:3b | 91 | 46.4% |

## 4. Target-length distribution

median 22 words, mean 25.6, range 2-154

| bucket | n | share |
|---|---|---|
| fragment / <=8 words | 22 | 11.2% |
| one-liner / 9-20 | 65 | 33.2% |
| ordinary / 21-45 | 91 | 46.4% |
| longer / 46-80 | 16 | 8.2% |
| long / >80 | 2 | 1.0% |

## 5. Question-ending rate

- overall: 12/196 (6.1%) end with a question
- plans with a MANDATORY question: 12; of those 12 ask one
- plans with an OPTIONAL question: 21; of those 0 ask one (silence is the trained behavior)
- plans with NO question: 163; of those 0 still end with one

## 6. Opening phrases and repetition

- distinct opening trigrams: 176/196 (ratio 0.90; the over-specialization gate floor is 0.60)
- most repeated openings:

| opening | n |
|---|---|
| "i don t" | 4 |
| "that s awesome" | 3 |
| "you re welcome" | 3 |
| "oh right i" | 3 |
| "that sounds like" | 2 |
| "you know i" | 2 |
| "so which one" | 2 |
| "i guess i" | 2 |

- near-duplicate target pairs (trigram Jaccard > 0.5): none

- residual (non-disqualifying) sludge flags surviving in accepted targets:

| flag | n |
|---|---|
| ends-with-question | 12 |

## 7. Silence by omission

- scenarios offering PALETTE items: 24
- of those, targets using NO palette item: 18 (75.0%)
- silence-palette stratum (palette present, correct answer uses none): 14 scenarios
- optional-question-unasked stratum (question available, correct answer asks none): 9 scenarios

## 8. Human editing

- human-edited targets: 0/196 (0.0%)
- every target in this build is raw teacher output that passed the gates and the sludge filter; the review package below is where human editing enters, and each edit will be recorded in `review.humanEdited` with the original preserved in `source.rawTeacherCandidate`.

## 9. Deterministic rejections

total scenarios with no acceptable candidate: 61

| reason (across all rejected attempts) | n |
|---|---|
| forbidden "..." present | 269 |
| none of [haven't learned,don't know,not sure what,no idea,never heard,haven't come across,don't actually know] present | 113 |
| must-state missing "..." | 65 |
| sludge: thanks-for-x | 24 |
| none of [don't think you,haven't told me,don't have that one,not that I,you never] present | 12 |
| sludge: assistant-offer | 10 |
| none of [don't think you,haven't told me,don't remember you telling,not that I,no, I don't] present | 8 |
| none of [haven't learned,don't know,not sure what,no idea,never heard,haven't come across,don't actually know,didn't know] present | 7 |
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
- `epi-animal-01` (epistemic-unknown)
- `epi-animal-02` (epistemic-unknown)
- `epi-animal-04` (epistemic-unknown)
- `epi-hobby-02` (epistemic-unknown)
- `epi-hobby-03` (epistemic-unknown)
- `epi-food-01` (epistemic-unknown)
- `epi-food-02` (epistemic-unknown)
- `epi-food-04` (epistemic-unknown)
- `epi-pers-02` (epistemic-unknown)
- `epi-word-01` (epistemic-unknown)
- `epi-word-03` (epistemic-unknown)
- `know-recall-02` (knowledge-provenance)
- `know-use-03` (knowledge-provenance)
- `ms-multi-03` (must-state)
- `ms-self-01` (must-state)
- `optq-wind-01` (optional-question-unasked)
- `optq-wind-02` (optional-question-unasked)
- `optq-busy-01` (optional-question-unasked)
- `optq-busy-03` (optional-question-unasked)
- `optq-state-01` (optional-question-unasked)
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
- `tu-epi-04` (epistemic-unknown)
- `tu-epi-05` (epistemic-unknown)
- `tu-epi-06` (epistemic-unknown)
- `tu-epi-08` (epistemic-unknown)
- `tu-epi-10` (epistemic-unknown)
- `tu-epi-12` (epistemic-unknown)
- `tu-epi-13` (epistemic-unknown)
- `tu-corr-02` (correction-genuine)
- `tu-corr-03` (correction-genuine)
- `tu-corr-06` (correction-genuine)
- `tu-optq-02` (optional-question-unasked)
- `tu-optq-05` (optional-question-unasked)
- `tu-optq-06` (optional-question-unasked)
- `tu-optq-08` (optional-question-unasked)
- `tu-optq-10` (optional-question-unasked)
- `tu-sil-02` (silence-palette)
- `tu-sil-04` (silence-palette)
- `tu-sil-06` (silence-palette)

## 10. Family-level split manifest

- unit: semantic scenario family; 71 families total
- validation families (14), one per stratum with >=2 families:

  - `ack-plain/good-news` (5)
  - `agreement-ordinary/plan-endorsement` (2)
  - `correction-genuine/object-property` (4)
  - `correction-user-owned/self-quantity` (2)
  - `epistemic-unknown/word` (1)
  - `knowledge-provenance/taught-then-used` (4)
  - `mandatory-clarify/ambiguous-option` (4)
  - `must-state/multi-item` (3)
  - `optional-question-unasked/mid-task` (1)
  - `playful-absurd/wry-observation` (3)
  - `shared-history-boundary/physical-experience` (2)
  - `silence-palette/hobby-news` (2)
  - `superseded/preference-changed` (3)
  - `terse/banter` (4)

- train: 156 examples across 57 families
- validation: 40 examples across 14 families
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
| epistemic-unknown | 13/32 | 223 | 17.2 |
| optional-question-unasked | 9/20 | 205 | 22.8 |
| silence-palette | 14/24 | 123 | 8.8 |
| correction-genuine | 20/28 | 113 | 5.7 |
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

**Real-derived share is 1/196 (0.5%), far below the designed 15-25%.** The cause is inventory, not policy: the durable TurnRecords banked so far hold 19 plans, and 14 of them belong to permanently held-out benchmark families (Cheshire, quokka, Epcot, Precious, DON'T BREAK). Of the handful that remain, the gates rejected some. Run 1a is therefore an almost entirely constructed corpus. The fix is time and normal use, not a different pipeline: every conversation now persists a plan, and the share should rise on its own before the 400- and 730-example checkpoints.
