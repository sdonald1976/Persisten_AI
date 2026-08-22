# Run-1a dataset audit

_Generated from 1184 teacher rows (1755 candidate draws) over 447 scenarios, across 4 generation pass(es). Nothing trained; nothing in production touched._

**Accepted: 416  |  Rejected: 31  |  Train 315 / Validation 101 by family**


## 1. Counts by behavioral stratum

| stratum | accepted | rejected |
|---|---|---|
| silence-palette | 69 | 0 |
| multi-obligation | 40 | 0 |
| no-invented-experience | 35 | 0 |
| epistemic-unknown | 34 | 8 |
| correction-genuine | 33 | 5 |
| ack-plain | 27 | 2 |
| superseded | 23 | 0 |
| terse | 23 | 1 |
| shared-history-boundary | 22 | 3 |
| optional-question-unasked | 20 | 5 |
| knowledge-provenance | 19 | 2 |
| playful-absurd | 19 | 0 |
| agreement-ordinary | 17 | 2 |
| must-state | 14 | 2 |
| mandatory-clarify | 12 | 0 |
| correction-user-owned | 9 | 1 |

## 2. Source: real-derived vs constructed

| source | n | share |
|---|---|---|
| constructed | 415 | 99.8% |
| turnrecord | 1 | 0.2% |

## 3. Teacher contribution

| teacher | targets accepted | share |
|---|---|---|
| qwen3:8b | 187 | 45.0% |
| llama3.2:3b | 168 | 40.4% |
| curator-authored | 61 | 14.7% |

## 4. Target-length distribution

median 15 words, mean 17.4, range 1-86

| bucket | n | share |
|---|---|---|
| fragment / <=8 words | 81 | 19.5% |
| one-liner / 9-20 | 208 | 50.0% |
| ordinary / 21-45 | 116 | 27.9% |
| longer / 46-80 | 8 | 1.9% |
| long / >80 | 3 | 0.7% |

## 5. Question-ending rate

- overall: 32/416 (7.7%) end with a question
- plans with a MANDATORY question: 12; of those 12 ask one
- plans with an OPTIONAL question: 63; of those 20 ask one (silence is the trained behavior)
- plans with NO question: 341; of those 0 still end with one

## 6. Opening phrases and repetition

- distinct opening trigrams: 401/416 (ratio 0.96; the over-specialization gate floor is 0.60)
- most repeated openings:

| opening | n |
|---|---|
| "no idea what" | 4 |
| "that s awesome" | 2 |
| "that s a" | 2 |
| "you re right" | 2 |
| "which one is" | 2 |
| "never heard of" | 2 |
| "honestly no idea" | 2 |
| "you told me" | 2 |

- near-duplicate target pairs (trigram Jaccard > 0.5): none

- residual (non-disqualifying) sludge flags surviving in accepted targets:

| flag | n |
|---|---|
| ends-with-question | 12 |

## 7. Silence by omission

- scenarios offering PALETTE items: 90
- of those, targets using NO palette item: 72 (80.0%)
- silence-palette stratum (palette present, correct answer uses none): 69 scenarios
- optional-question-unasked stratum (question available, correct answer asks none): 20 scenarios

## 8. Curation provenance

| disposition | n | share |
|---|---|---|
| teacher target kept unchanged | 155 | 37.3% |
| edited — Scott's dictated line or named finding | 25 | 6.0% |
| edited — curator, under Scott's written principles | 169 | 40.6% |
| curator-authored (every teacher draw failed) | 65 | 15.6% |

- wrapper-quote normalization (mechanical, not an edit): 12 targets
- every edited or authored target re-passed the deterministic gates in this build; the raw teacher candidate is preserved in `source.rawTeacherCandidate` and the reason for every change in `curation-run1a.jsonl` and each row's `curation` field.
- scenarios previously rejected that now carry a curator-authored target: ['epi-animal-01', 'epi-animal-02', 'epi-animal-04', 'epi-food-01', 'epi-hobby-02', 'epi-word-01', 'epi-word-03', 'mob-mixed-01', 'mob-mixed-06', 'mob-ms3-03', 'mob-ms3-07', 'mob-msepi-01', 'mob-msepi-03', 'mob-msepi-04', 'mob-msepi-07', 'mob-msepi-10', 'mob-msq-01', 'mob-msq-06', 'nix-act-05', 'nix-act-07', 'nix-food-03', 'nix-food-07', 'nix-media-05', 'nix-media-08', 'optq-busy-03', 'optq-state-01', 'optq-wind-01', 'r1b-corr-04', 'r1b-corr-09', 'r1b-epi-02', 'r1b-epi-03', 'r1b-epi-07', 'r1b-epi-09', 'r1b-epi-10', 'r1b-know-01', 'r1b-know-02', 'r1b-know-05', 'r1b-optq-01', 'r1b-optq-05', 'r1b-play-01', 'r1b-terse-03', 'sil-emo-03', 'sil-emo-04', 'sil-new-01', 'sil-new-03', 'sil-new-04', 'sil-new-05', 'sil-new-06', 'sil2-close-02', 'sil2-close-09', 'sil2-close-11', 'sil2-load-04', 'sil2-load-06', 'sil2-pref-05', 'sil2-ser-01', 'sil2-ser-03', 'tu-epi-04', 'tu-epi-12', 'tu-epi-13', 'tu-optq-02', 'tu-optq-06', 'tu-optq-10', 'tu-sil-02', 'tu-sil-04', 'tu-sil-06']

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
| `mob-ack2-01` | edit | curator |
| `mob-ack2-02` | edit | curator |
| `mob-mixed-01` | author | curator |
| `mob-mixed-02` | edit | curator |
| `mob-mixed-03` | edit | curator |
| `mob-mixed-05` | edit | curator |
| `mob-mixed-06` | author | curator |
| `mob-ms3-02` | edit | curator |
| `mob-ms3-03` | author | curator |
| `mob-ms3-04` | edit | curator |
| `mob-ms3-05` | edit | curator |
| `mob-ms3-06` | edit | curator |
| `mob-ms3-07` | author | curator |
| `mob-msepi-01` | author | curator |
| `mob-msepi-03` | author | curator |
| `mob-msepi-04` | author | curator |
| `mob-msepi-05` | edit | curator |
| `mob-msepi-06` | edit | curator |
| `mob-msepi-07` | author | curator |
| `mob-msepi-08` | edit | curator |
| `mob-msepi-09` | edit | curator |
| `mob-msepi-10` | author | curator |
| `mob-msq-01` | author | curator |
| `mob-msq-02` | edit | curator |
| `mob-msq-05` | edit | curator |
| `mob-msq-06` | author | curator |
| `mob-sup-01` | edit | curator |
| `mob-sup-03` | edit | curator |
| `mob-sup-05` | edit | curator |
| `mob-sup-06` | edit | curator |
| `ms-multi-01` | edit | curator |
| `ms-multi-04` | edit | curator |
| `ms-recall-01` | edit | curator |
| `ms-recall-03` | edit | curator |
| `ms-self-02` | edit | curator |
| `ms-self-03` | edit | curator |
| `ms-self-04` | edit | curator |
| `nix-act-01` | edit | curator |
| `nix-act-02` | edit | curator |
| `nix-act-04` | edit | curator |
| `nix-act-05` | author | curator |
| `nix-act-07` | author | curator |
| `nix-food-01` | edit | curator |
| `nix-food-02` | edit | curator |
| `nix-food-03` | author | curator |
| `nix-food-04` | edit | curator |
| `nix-food-06` | edit | curator |
| `nix-food-07` | author | curator |
| `nix-food-08` | edit | curator |
| `nix-media-02` | edit | curator |
| `nix-media-03` | edit | curator |
| `nix-media-05` | author | curator |
| `nix-media-06` | edit | curator |
| `nix-media-07` | edit | curator |
| `nix-media-08` | author | curator |
| `nix-media-09` | edit | curator |
| `nix-travel-01` | edit | curator |
| `nix-travel-03` | edit | curator |
| `nix-travel-05` | edit | curator |
| `nix-travel-07` | edit | curator |
| `nix-travel-08` | edit | curator |
| `optq-busy-02` | edit | scott |
| `optq-busy-03` | author | curator |
| `optq-state-01` | author | curator |
| `optq-wind-01` | author | curator |
| `optq-wind-04` | edit | curator |
| `optq-wind-05` | edit | scott |
| `play-hypo-02` | edit | curator |
| `play-wry-02` | edit | curator |
| `r1b-ack-02` | edit | curator |
| `r1b-ack-04` | edit | curator |
| `r1b-ack-07` | edit | curator |
| `r1b-ack-08` | edit | curator |
| `r1b-agree-02` | edit | curator |
| `r1b-agree-04` | edit | curator |
| `r1b-corr-03` | edit | curator |
| `r1b-corr-04` | author | curator |
| `r1b-corr-05` | edit | curator |
| `r1b-corr-06` | edit | curator |
| `r1b-corr-07` | edit | curator |
| `r1b-corr-09` | author | curator |
| `r1b-corr-10` | edit | curator |
| `r1b-epi-02` | author | curator |
| `r1b-epi-03` | author | curator |
| `r1b-epi-05` | edit | curator |
| `r1b-epi-06` | edit | curator |
| `r1b-epi-07` | author | curator |
| `r1b-epi-09` | author | curator |
| `r1b-epi-10` | author | curator |
| `r1b-know-01` | author | curator |
| `r1b-know-02` | author | curator |
| `r1b-know-03` | edit | scott-principle |
| `r1b-know-04` | edit | curator |
| `r1b-know-05` | author | curator |
| `r1b-optq-01` | author | curator |
| `r1b-optq-02` | edit | curator |
| `r1b-optq-04` | edit | curator |
| `r1b-optq-05` | author | curator |
| `r1b-play-01` | author | curator |
| `r1b-play-02` | edit | curator |
| `r1b-play-03` | edit | curator |
| `r1b-play-04` | edit | curator |
| `r1b-play-05` | edit | curator |
| `r1b-shb-01` | edit | scott-principle |
| `r1b-shb-03` | edit | curator |
| `r1b-shb-05` | edit | curator |
| `r1b-sup-01` | edit | curator |
| `r1b-sup-02` | edit | curator |
| `r1b-terse-02` | edit | curator |
| `r1b-terse-03` | author | curator |
| `r1b-terse-05` | edit | curator |
| `r1b-terse-06` | edit | curator |
| `r1b-terse-07` | edit | curator |
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
| `sil-emo-03` | author | curator |
| `sil-emo-04` | author | curator |
| `sil-new-01` | author | curator |
| `sil-new-02` | edit | scott |
| `sil-new-03` | author | curator |
| `sil-new-04` | author | curator |
| `sil-new-05` | author | curator |
| `sil-new-06` | author | curator |
| `sil-q-01` | edit | curator |
| `sil-q-03` | edit | scott |
| `sil-q-04` | edit | curator |
| `sil-q-05` | edit | curator |
| `sil-q-06` | edit | curator |
| `sil2-ans-04` | edit | curator |
| `sil2-ans-05` | edit | curator |
| `sil2-close-02` | author | curator |
| `sil2-close-03` | edit | curator |
| `sil2-close-05` | edit | curator |
| `sil2-close-06` | edit | curator |
| `sil2-close-07` | edit | curator |
| `sil2-close-08` | edit | curator |
| `sil2-close-09` | author | curator |
| `sil2-close-11` | author | curator |
| `sil2-close-12` | edit | curator |
| `sil2-load-03` | edit | curator |
| `sil2-load-04` | author | curator |
| `sil2-load-05` | edit | curator |
| `sil2-load-06` | author | curator |
| `sil2-load-09` | edit | curator |
| `sil2-load-12` | edit | curator |
| `sil2-pref-01` | edit | curator |
| `sil2-pref-03` | edit | curator |
| `sil2-pref-04` | edit | curator |
| `sil2-pref-05` | author | curator |
| `sil2-pref-06` | edit | curator |
| `sil2-pref-08` | edit | curator |
| `sil2-ser-01` | author | curator |
| `sil2-ser-02` | edit | curator |
| `sil2-ser-03` | author | curator |
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
| `tu-corr-03` | edit | curator |
| `tu-corr-04` | edit | curator |
| `tu-corr-05` | edit | curator |
| `tu-corr-06` | edit | curator |
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
| `tu-play-04` | edit | curator |
| `tu-shb-01` | edit | scott |
| `tu-shb-02` | edit | curator |
| `tu-shb-03` | edit | curator |
| `tu-shb-05` | edit | curator |
| `tu-sil-01` | edit | curator |
| `tu-sil-02` | author | curator |
| `tu-sil-04` | author | curator |
| `tu-sil-05` | edit | curator |
| `tu-sil-06` | author | curator |
| `tu-sil-07` | edit | curator |
| `tu-sup-01` | edit | curator |
| `tu-sup-04` | edit | scott |

## 9. Deterministic rejections

total scenarios with no acceptable candidate: 31

| reason (across all rejected attempts) | n |
|---|---|
| forbidden "..." present | 72 |
| none of [haven't learned,don't know,not sure what,no idea,never heard,haven't come across,don't actually know] present | 49 |
| must-state missing "..." | 42 |
| sludge: thanks-for-x | 13 |
| none of [don't think you,haven't told me,don't have that one,not that I,you never] present | 12 |
| none of [don't think you,haven't told me,don't remember you telling,not that I,no, I don't] present | 8 |
| none of [low,tired,quiet,slow,not at my] present | 7 |
| none of [haven't learned,don't know,not sure what,no idea,never heard,haven't come across,don't actually know,haven't told me] present | 6 |
| sludge: assistant-offer | 2 |
| sludge: self-deprecation-filler | 2 |

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
- `terse-recall-04` (terse)
- `tu-epi-01` (epistemic-unknown)
- `tu-epi-05` (epistemic-unknown)
- `tu-epi-06` (epistemic-unknown)
- `tu-epi-08` (epistemic-unknown)
- `tu-optq-05` (optional-question-unasked)
- `tu-optq-08` (optional-question-unasked)

## 10. Family-level split manifest

- unit: semantic scenario family; 119 families total
- validation families (27), one per stratum with >=2 families:

  - `ack-plain/good-news` (5)
  - `ack-plain/routine-done` (4)
  - `agreement-ordinary/endorsement-2` (2)
  - `agreement-ordinary/plan-endorsement` (2)
  - `correction-genuine/object-property` (4)
  - `correction-genuine/wrong-ownership` (2)
  - `correction-user-owned/self-quantity` (2)
  - `epistemic-unknown/personal-2` (2)
  - `epistemic-unknown/term` (3)
  - `knowledge-provenance/taught-then-used` (4)
  - `knowledge-provenance/use-2` (3)
  - `mandatory-clarify/ambiguous-option` (4)
  - `multi-obligation/kitchen-sink` (6)
  - `must-state/multi-item` (3)
  - `no-invented-experience/media-preference` (10)
  - `optional-question-unasked/moment-2` (3)
  - `optional-question-unasked/wind-down` (4)
  - `playful-absurd/hypo-2` (1)
  - `playful-absurd/running-bit` (3)
  - `shared-history-boundary/false-attribution` (2)
  - `shared-history-boundary/tenure-2` (3)
  - `silence-palette/heavy-load` (12)
  - `silence-palette/simple-question` (6)
  - `superseded/number-changed` (2)
  - `superseded/quantity-changed` (2)
  - `terse/banter` (4)
  - `terse/confirm-2` (3)

- train: 315 examples across 92 families
- validation: 101 examples across 27 families
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
| optional-question-unasked | 20/25 | 258 | 12.9 |
| epistemic-unknown | 34/42 | 294 | 8.6 |
| correction-genuine | 33/38 | 157 | 4.8 |
| must-state | 14/16 | 53 | 3.8 |
| shared-history-boundary | 22/25 | 88 | 4.0 |
| agreement-ordinary | 17/19 | 44 | 2.6 |
| correction-user-owned | 9/10 | 43 | 4.8 |
| knowledge-provenance | 19/21 | 75 | 3.9 |
| ack-plain | 27/29 | 64 | 2.4 |
| terse | 23/24 | 67 | 2.9 |
| playful-absurd | 19/19 | 52 | 2.7 |
| silence-palette | 69/69 | 262 | 3.8 |
| superseded | 23/23 | 76 | 3.3 |
| mandatory-clarify | 12/12 | 28 | 2.3 |
| multi-obligation | 40/40 | 123 | 3.1 |
| no-invented-experience | 35/35 | 71 | 2.0 |

The two starved strata are the same two defect classes the round-2 review named. Both teachers reach for pretrained knowledge when the plan says Ava has not learned something, and both tack a question onto a turn the plan closed. Re-sampling has reached diminishing returns; the honest options are (a) accept the thinner representation, (b) human-author targets for those strata, or (c) accept that these two behaviors may need more than run 1a to move. This is a judgment call, not a pipeline bug.

**Curation shortened the corpus.** Median length fell from ~22 words (raw teacher output) to 15, and only 11 rows now exceed 45 words (`nix-food-09`, `nix-travel-02`, `nix-travel-04`, `nix-travel-06`, `shb-phys-02`, `sil2-load-01`, `sil2-load-07`, `sil2-pref-02`, `sil2-pref-09`, `sil2-pref-10`, `tu-play-03`). Most teacher length was sludge — restatement, coaching, invented color — so trimming it was correct; but the result is that genuinely longer-licensed replies are thinly represented, which is exactly the 'length must emerge from content' concern. The registers in this corpus mostly license short replies, so the profile is not dishonest — but if run 1a should also teach the occasional full-paragraph turn, a handful of longer-licensed scenarios (a recap request, a told story, a thinking-out-loud answer) would need authoring. Decision left open.

**Real-derived share is 1/416 (0.2%), far below the designed 15-25%.** The cause is inventory, not policy: the durable TurnRecords banked so far hold 19 plans, and 14 of them belong to permanently held-out benchmark families (Cheshire, quokka, Epcot, Precious, DON'T BREAK). Of the handful that remain, the gates rejected some. Run 1a is therefore an almost entirely constructed corpus. The fix is time and normal use, not a different pipeline: every conversation now persists a plan, and the share should rise on its own before the 400- and 730-example checkpoints.
