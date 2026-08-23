# Run-1a dataset audit

_Generated from 1812 teacher rows (2593 candidate draws) over 761 scenarios, across 4 generation pass(es). Nothing trained; nothing in production touched._

**Accepted: 730  |  Rejected: 31  |  Train 581 / Validation 149 by family**


## 1. Counts by behavioral stratum

| stratum | accepted | rejected |
|---|---|---|
| mandatory-clarify | 78 | 0 |
| silence-palette | 69 | 0 |
| question-contrast | 60 | 0 |
| correction-genuine | 53 | 5 |
| epistemic-unknown | 52 | 8 |
| epistemic-clarify | 48 | 0 |
| ack-plain | 47 | 2 |
| multi-obligation | 40 | 0 |
| terse | 39 | 1 |
| no-invented-experience | 35 | 0 |
| superseded | 33 | 0 |
| shared-history-boundary | 30 | 3 |
| knowledge-provenance | 29 | 2 |
| playful-absurd | 29 | 0 |
| agreement-ordinary | 27 | 2 |
| must-state | 24 | 2 |
| optional-question-unasked | 20 | 5 |
| correction-user-owned | 17 | 1 |

## 2. Source: real-derived vs constructed

| source | n | share |
|---|---|---|
| constructed | 729 | 99.9% |
| turnrecord | 1 | 0.1% |

## 3. Teacher contribution

| teacher | targets accepted | share |
|---|---|---|
| qwen3:8b | 334 | 45.8% |
| llama3.2:3b | 264 | 36.2% |
| curator-authored | 132 | 18.1% |

## 4. Target-length distribution

median 14 words, mean 16.5, range 1-86

| bucket | n | share |
|---|---|---|
| fragment / <=8 words | 139 | 19.0% |
| one-liner / 9-20 | 391 | 53.6% |
| ordinary / 21-45 | 188 | 25.8% |
| longer / 46-80 | 8 | 1.1% |
| long / >80 | 4 | 0.5% |

## 5. Question-ending rate

- overall: 189/730 (25.9%) end with a question
- plans with a MANDATORY question: 156; of those 156 ask one
- plans with an OPTIONAL question: 112; of those 33 ask one (silence is the trained behavior)
- plans with NO question: 462; of those 0 still end with one

## 6. Opening phrases and repetition

- distinct opening trigrams: 667/730 (ratio 0.91; the over-specialization gate floor is 0.60)
- most repeated openings:

| opening | n |
|---|---|
| "i don't know" | 12 |
| "i've never heard" | 7 |
| "no idea what" | 7 |
| "which one is" | 6 |
| "i haven't come" | 4 |
| "no i don't" | 4 |
| "you re right" | 3 |
| "i don t" | 3 |

- near-duplicate target pairs (trigram Jaccard > 0.5): 3
  - r1b-agree-03 ~ r1c-agree-03 (trigram Jaccard 0.67)
  - r1b-epi-04 ~ r1c-epi-04 (trigram Jaccard 0.67)
  - r1b-epi-04 ~ r1c-epi-07 (trigram Jaccard 0.57)

- residual (non-disqualifying) sludge flags surviving in accepted targets:

| flag | n |
|---|---|
| ends-with-question | 122 |

## 7. Silence by omission

- scenarios offering PALETTE items: 94
- of those, targets using NO palette item: 72 (76.6%)
- silence-palette stratum (palette present, correct answer uses none): 69 scenarios
- optional-question-unasked stratum (question available, correct answer asks none): 20 scenarios

## 8. Curation provenance

| disposition | n | share |
|---|---|---|
| teacher target kept unchanged | 299 | 41.0% |
| edited — Scott's dictated line or named finding | 25 | 3.4% |
| edited — curator, under Scott's written principles | 268 | 36.7% |
| curator-authored (every teacher draw failed) | 136 | 18.6% |

- wrapper-quote normalization (mechanical, not an edit): 14 targets
- every edited or authored target re-passed the deterministic gates in this build; the raw teacher candidate is preserved in `source.rawTeacherCandidate` and the reason for every change in `curation-run1a.jsonl` and each row's `curation` field.
- scenarios previously rejected that now carry a curator-authored target: ['cl2-opt-08', 'cl2-pron-02', 'cl2-time-01', 'epc-class-01', 'epc-class-05', 'epc-class-06', 'epc-craft-01', 'epc-craft-02', 'epc-craft-03', 'epc-craft-05', 'epc-craft-06', 'epc-food-01', 'epc-food-02', 'epc-food-04', 'epc-food-05', 'epc-gadget-02', 'epc-gadget-04', 'epc-gadget-05', 'epc-game-01', 'epc-game-04', 'epc-game-05', 'epc-place-01', 'epc-place-02', 'epc-place-03', 'epc-place-05', 'epc-place-06', 'epc-term-01', 'epc-term-03', 'epc-term-04', 'epc-term-05', 'epc-term-06', 'epc-tool-01', 'epc-tool-02', 'epc-tool-05', 'epi-animal-01', 'epi-animal-02', 'epi-animal-04', 'epi-food-01', 'epi-hobby-02', 'epi-word-01', 'epi-word-03', 'mob-mixed-01', 'mob-mixed-06', 'mob-ms3-03', 'mob-ms3-07', 'mob-msepi-01', 'mob-msepi-03', 'mob-msepi-04', 'mob-msepi-07', 'mob-msepi-10', 'mob-msq-01', 'mob-msq-06', 'nix-act-05', 'nix-act-07', 'nix-food-03', 'nix-food-07', 'nix-media-05', 'nix-media-08', 'optq-busy-03', 'optq-state-01', 'optq-wind-01', 'qc-bbq-s', 'qc-bed-s', 'qc-bird-s', 'qc-book-s', 'qc-gift-s', 'qc-kid-s', 'qc-movie-s', 'qc-music-s', 'qc-news-s', 'qc-photo-s', 'qc-soup-s', 'qc-trip-s', 'r1b-corr-04', 'r1b-corr-09', 'r1b-epi-02', 'r1b-epi-03', 'r1b-epi-07', 'r1b-epi-09', 'r1b-epi-10', 'r1b-know-01', 'r1b-know-02', 'r1b-know-05', 'r1b-optq-01', 'r1b-optq-05', 'r1b-play-01', 'r1b-terse-03', 'r1c-ack-13', 'r1c-ack-20', 'r1c-corr-03', 'r1c-corr-08', 'r1c-corr-10', 'r1c-corr-13', 'r1c-corr-15', 'r1c-corr-18', 'r1c-corr-19', 'r1c-corr-20', 'r1c-corru-04', 'r1c-epi-02', 'r1c-epi-05', 'r1c-epi-06', 'r1c-epi-08', 'r1c-epi-09', 'r1c-epi-10', 'r1c-epi-13', 'r1c-epi-15', 'r1c-epi-18', 'r1c-know-02', 'r1c-know-04', 'r1c-know-10', 'r1c-play-01', 'r1c-shb-03', 'sil-emo-03', 'sil-emo-04', 'sil-new-01', 'sil-new-03', 'sil-new-04', 'sil-new-05', 'sil-new-06', 'sil2-close-02', 'sil2-close-09', 'sil2-close-11', 'sil2-load-04', 'sil2-load-06', 'sil2-pref-05', 'sil2-ser-01', 'sil2-ser-03', 'tu-epi-04', 'tu-epi-12', 'tu-epi-13', 'tu-optq-02', 'tu-optq-06', 'tu-optq-10', 'tu-sil-02', 'tu-sil-04', 'tu-sil-06']

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
| `cl2-inst-03` | edit | curator |
| `cl2-obj-10` | edit | curator |
| `cl2-opt-06` | edit | curator |
| `cl2-opt-08` | author | curator |
| `cl2-per-08` | edit | curator |
| `cl2-pron-02` | author | curator |
| `cl2-pron-05` | edit | curator |
| `cl2-pron-06` | edit | curator |
| `cl2-qty-03` | edit | curator |
| `cl2-time-01` | author | curator |
| `cl2-time-07` | edit | curator |
| `cl2-time-09` | edit | curator |
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
| `epc-class-01` | author | curator |
| `epc-class-02` | edit | curator |
| `epc-class-03` | edit | curator |
| `epc-class-04` | edit | curator |
| `epc-class-05` | author | curator |
| `epc-class-06` | author | curator |
| `epc-craft-01` | author | curator |
| `epc-craft-02` | author | curator |
| `epc-craft-03` | author | curator |
| `epc-craft-05` | author | curator |
| `epc-craft-06` | author | curator |
| `epc-food-01` | author | curator |
| `epc-food-02` | author | curator |
| `epc-food-04` | author | curator |
| `epc-food-05` | author | curator |
| `epc-food-06` | edit | curator |
| `epc-gadget-01` | edit | curator |
| `epc-gadget-02` | author | curator |
| `epc-gadget-03` | edit | curator |
| `epc-gadget-04` | author | curator |
| `epc-gadget-05` | author | curator |
| `epc-gadget-06` | edit | curator |
| `epc-game-01` | author | curator |
| `epc-game-02` | edit | curator |
| `epc-game-04` | author | curator |
| `epc-game-05` | author | curator |
| `epc-game-06` | edit | curator |
| `epc-place-01` | author | curator |
| `epc-place-02` | author | curator |
| `epc-place-03` | author | curator |
| `epc-place-05` | author | curator |
| `epc-place-06` | author | curator |
| `epc-term-01` | author | curator |
| `epc-term-02` | edit | curator |
| `epc-term-03` | author | curator |
| `epc-term-04` | author | curator |
| `epc-term-05` | author | curator |
| `epc-term-06` | author | curator |
| `epc-tool-01` | author | curator |
| `epc-tool-02` | author | curator |
| `epc-tool-03` | edit | curator |
| `epc-tool-04` | edit | curator |
| `epc-tool-05` | author | curator |
| `epc-tool-06` | edit | curator |
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
| `qc-bbq-s` | author | curator |
| `qc-bed-s` | author | curator |
| `qc-bird-s` | author | curator |
| `qc-book-q` | edit | curator |
| `qc-book-s` | author | curator |
| `qc-bread-s` | edit | curator |
| `qc-car-s` | edit | curator |
| `qc-cat-q` | edit | curator |
| `qc-cat-s` | edit | curator |
| `qc-coffee-q` | edit | curator |
| `qc-fence-s` | edit | curator |
| `qc-fix-s` | edit | curator |
| `qc-gift-s` | author | curator |
| `qc-kid-s` | author | curator |
| `qc-movie-s` | author | curator |
| `qc-music-s` | author | curator |
| `qc-news-q` | edit | curator |
| `qc-news-s` | author | curator |
| `qc-paint-q` | edit | curator |
| `qc-photo-s` | author | curator |
| `qc-plant-s` | edit | curator |
| `qc-run-s` | edit | curator |
| `qc-shed-q` | edit | curator |
| `qc-soup-s` | author | curator |
| `qc-tea-q` | edit | curator |
| `qc-tea-s` | edit | curator |
| `qc-trip-s` | author | curator |
| `qc-walk-s` | edit | curator |
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
| `r1c-ack-02` | edit | curator |
| `r1c-ack-03` | edit | curator |
| `r1c-ack-04` | edit | curator |
| `r1c-ack-05` | edit | curator |
| `r1c-ack-06` | edit | curator |
| `r1c-ack-10` | edit | curator |
| `r1c-ack-12` | edit | curator |
| `r1c-ack-13` | author | curator |
| `r1c-ack-14` | edit | curator |
| `r1c-ack-15` | edit | curator |
| `r1c-ack-16` | edit | curator |
| `r1c-ack-18` | edit | curator |
| `r1c-ack-20` | author | curator |
| `r1c-agree-10` | edit | curator |
| `r1c-corr-01` | edit | curator |
| `r1c-corr-02` | edit | curator |
| `r1c-corr-03` | author | curator |
| `r1c-corr-04` | edit | curator |
| `r1c-corr-05` | edit | curator |
| `r1c-corr-06` | edit | curator |
| `r1c-corr-07` | edit | curator |
| `r1c-corr-08` | author | curator |
| `r1c-corr-09` | edit | curator |
| `r1c-corr-10` | author | curator |
| `r1c-corr-11` | edit | curator |
| `r1c-corr-13` | author | curator |
| `r1c-corr-14` | edit | curator |
| `r1c-corr-15` | author | curator |
| `r1c-corr-18` | author | curator |
| `r1c-corr-19` | author | curator |
| `r1c-corr-20` | author | curator |
| `r1c-corru-02` | edit | curator |
| `r1c-corru-04` | author | curator |
| `r1c-corru-08` | edit | curator |
| `r1c-epi-02` | author | curator |
| `r1c-epi-05` | author | curator |
| `r1c-epi-06` | author | curator |
| `r1c-epi-08` | author | curator |
| `r1c-epi-09` | author | curator |
| `r1c-epi-10` | author | curator |
| `r1c-epi-11` | edit | curator |
| `r1c-epi-12` | edit | curator |
| `r1c-epi-13` | author | curator |
| `r1c-epi-15` | author | curator |
| `r1c-epi-16` | edit | curator |
| `r1c-epi-18` | author | curator |
| `r1c-know-01` | edit | curator |
| `r1c-know-02` | author | curator |
| `r1c-know-04` | author | curator |
| `r1c-know-05` | edit | curator |
| `r1c-know-06` | edit | curator |
| `r1c-know-07` | edit | curator |
| `r1c-know-10` | author | curator |
| `r1c-ms-02` | edit | curator |
| `r1c-ms-05` | edit | curator |
| `r1c-ms-06` | edit | curator |
| `r1c-ms-07` | edit | curator |
| `r1c-ms-10` | edit | curator |
| `r1c-play-01` | author | curator |
| `r1c-play-02` | edit | curator |
| `r1c-play-03` | edit | curator |
| `r1c-play-05` | edit | curator |
| `r1c-play-06` | edit | curator |
| `r1c-play-07` | edit | curator |
| `r1c-play-08` | edit | curator |
| `r1c-play-09` | edit | curator |
| `r1c-shb-01` | edit | curator |
| `r1c-shb-02` | edit | curator |
| `r1c-shb-03` | author | curator |
| `r1c-shb-04` | edit | curator |
| `r1c-shb-06` | edit | curator |
| `r1c-shb-07` | edit | curator |
| `r1c-shb-08` | edit | curator |
| `r1c-sup-02` | edit | curator |
| `r1c-sup-10` | edit | curator |
| `r1c-terse-01` | edit | curator |
| `r1c-terse-02` | edit | curator |
| `r1c-terse-03` | edit | curator |
| `r1c-terse-05` | edit | curator |
| `r1c-terse-06` | edit | curator |
| `r1c-terse-08` | edit | curator |
| `r1c-terse-09` | edit | curator |
| `r1c-terse-10` | edit | curator |
| `r1c-terse-11` | edit | curator |
| `r1c-terse-14` | edit | curator |
| `r1c-terse-15` | edit | curator |
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

- unit: semantic scenario family; 221 families total
- validation families (41), one per stratum with >=2 families:

  - `ack-plain/domestic-3` (5)
  - `ack-plain/good-news` (5)
  - `ack-plain/routine-done` (4)
  - `agreement-ordinary/endorsement-2` (2)
  - `agreement-ordinary/observation-3` (2)
  - `agreement-ordinary/plan-endorsement` (2)
  - `correction-genuine/object-property` (4)
  - `correction-genuine/who-said` (2)
  - `correction-genuine/wrong-ownership` (2)
  - `correction-user-owned/self-place` (2)
  - `correction-user-owned/self-quantity` (2)
  - `epistemic-clarify/game` (6)
  - `epistemic-unknown/nature-3` (2)
  - `epistemic-unknown/personal-2` (2)
  - `epistemic-unknown/term` (3)
  - `knowledge-provenance/taught-then-used` (4)
  - `knowledge-provenance/use-2` (3)
  - `knowledge-provenance/use-3` (3)
  - `mandatory-clarify/ambiguous-option` (4)
  - `mandatory-clarify/object-2` (10)
  - `multi-obligation/kitchen-sink` (6)
  - `must-state/multi-item` (3)
  - `must-state/self-3` (2)
  - `no-invented-experience/media-preference` (10)
  - `optional-question-unasked/moment-2` (3)
  - `optional-question-unasked/wind-down` (4)
  - `playful-absurd/hypo-2` (1)
  - `playful-absurd/running-bit` (3)
  - `playful-absurd/wry-3` (4)
  - `question-contrast/run` (2)
  - `shared-history-boundary/false-attribution` (2)
  - `shared-history-boundary/real-shared` (2)
  - `shared-history-boundary/tenure-2` (3)
  - `silence-palette/heavy-load` (12)
  - `silence-palette/simple-question` (6)
  - `superseded/number-changed` (2)
  - `superseded/people-3` (2)
  - `superseded/quantity-changed` (2)
  - `terse/banter` (4)
  - `terse/banter-3` (4)
  - `terse/confirm-2` (3)

- train: 581 examples across 180 families
- validation: 149 examples across 41 families
- full manifest: `splits.json`

## 11. Leakage check

Permanently held out, never trained on:

- the eleven original benchmark fixtures (training/renderer/fixtures.jsonl)
- the entire false-correction / agreement-inversion family
- epistemic leakage: quokka, axe-with-provenance
- palette contamination: Epcot/pizza, Precious
- one scenario family to be authored only AFTER training completes

**No leaks found.** No accepted example mentions a held-out subject (quokka, Cheshire/Mad Hatter, Epcot/pizza, Precious, shatterproof, rabbit hole), and no accepted example pairs an agreement-confirmed plan with a correction-shaped user message — the held-out inversion composition is absent from training by construction, not by filtering.

Near-duplicate check across all accepted targets: FOUND 3.

## 12. Findings that need a decision before training

**Teacher acceptance by stratum** — where the teachers systematically could not render the behavior, the corpus is thin precisely where the experiment needs it:

| stratum | accepted / scenarios | draws spent | draws per accepted |
|---|---|---|---|
| optional-question-unasked | 20/25 | 258 | 12.9 |
| epistemic-unknown | 52/60 | 347 | 6.7 |
| shared-history-boundary | 30/33 | 106 | 3.5 |
| correction-genuine | 53/58 | 206 | 3.9 |
| must-state | 24/26 | 79 | 3.3 |
| agreement-ordinary | 27/29 | 64 | 2.4 |
| knowledge-provenance | 29/31 | 102 | 3.5 |
| correction-user-owned | 17/18 | 62 | 3.6 |
| ack-plain | 47/49 | 104 | 2.2 |
| terse | 39/40 | 102 | 2.6 |
| playful-absurd | 29/29 | 72 | 2.5 |
| silence-palette | 69/69 | 262 | 3.8 |
| superseded | 33/33 | 104 | 3.2 |
| mandatory-clarify | 78/78 | 187 | 2.4 |
| multi-obligation | 40/40 | 123 | 3.1 |
| no-invented-experience | 35/35 | 71 | 2.0 |
| epistemic-clarify | 48/48 | 167 | 3.5 |
| question-contrast | 60/60 | 177 | 3.0 |

The two starved strata are the same two defect classes the round-2 review named. Both teachers reach for pretrained knowledge when the plan says Ava has not learned something, and both tack a question onto a turn the plan closed. Re-sampling has reached diminishing returns; the honest options are (a) accept the thinner representation, (b) human-author targets for those strata, or (c) accept that these two behaviors may need more than run 1a to move. This is a judgment call, not a pipeline bug.

**Curation shortened the corpus.** Median length fell from ~22 words (raw teacher output) to 14, and only 12 rows now exceed 45 words (`nix-food-09`, `nix-travel-02`, `nix-travel-04`, `nix-travel-06`, `r1c-shb-05`, `shb-phys-02`, `sil2-load-01`, `sil2-load-07`, `sil2-pref-02`, `sil2-pref-09`, `sil2-pref-10`, `tu-play-03`). Most teacher length was sludge — restatement, coaching, invented color — so trimming it was correct; but the result is that genuinely longer-licensed replies are thinly represented, which is exactly the 'length must emerge from content' concern. The registers in this corpus mostly license short replies, so the profile is not dishonest — but if run 1a should also teach the occasional full-paragraph turn, a handful of longer-licensed scenarios (a recap request, a told story, a thinking-out-loud answer) would need authoring. Decision left open.

**Real-derived share is 1/730 (0.1%), far below the designed 15-25%.** The cause is inventory, not policy: the durable TurnRecords banked so far hold 19 plans, and 14 of them belong to permanently held-out benchmark families (Cheshire, quokka, Epcot, Precious, DON'T BREAK). Of the handful that remain, the gates rejected some. Run 1a is therefore an almost entirely constructed corpus. The fix is time and normal use, not a different pipeline: every conversation now persists a plan, and the share should rise on its own before the 400- and 730-example checkpoints.
