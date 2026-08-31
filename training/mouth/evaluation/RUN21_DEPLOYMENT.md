# Run-2.1 deployment: shadow, then canary

Canary is **on for `demo-user` only**, serving run-2.1. Every other user is on production
Stheno. Clearing `Companion:RendererShadow:Mouth:CanaryUserId` is the whole rollback.

---

## 1. The ADMIT fallback, which did not exist

The check existed — `epistemic-admission-absent`, raised when a plan carries a `NotLearned`
subject and the reply names no gap — but it was **advisory**. It flagged, and the reply was
displayed anyway. That is Run-2's original defect arriving at serving time wearing a warning
label, so it is critical now and the canary falls back to production with a reason that names
itself:

> `critical fidelity failure: epistemic-admission-absent: not-learned subject with no admission phrase`

Making it critical *without* fixing the matcher would have been worse than leaving it advisory.
The phrase list was ASCII-only, and the model writes `I don’t know`:

| on Run-2.1's 31 hard-eval ADMIT rows | admissions seen |
|---|---|
| renderer's old ASCII list | 10 |
| + apostrophe normalisation | 15 |
| corpus-gating implementation | **21** |

Eleven correct replies would have fallen back for a punctuation character.

The matcher now lives once, in `Companion.Core/Validation/UncertaintyMarkers.cs`:

- `Admits()` — byte-for-byte the instrument the Run-2 corpus was accepted against. Unchanged,
  and deliberately not widened. All six score files re-scored **identical**, so the published
  comparison still means what it said.
- `AdmitsNotLearned()` — the renderer's question, which is wider: a concept never taught
  ("I've never heard of zydeco") admits a gap without matching any pending-outcome pattern.

Two questions, one normaliser, one file, and the difference written down rather than found
later as a disagreement.

Pinned by `AnUnmetAdmitObligation_IsCritical_AndTypographicApostrophesStillCount`, plus
`TheUncertaintyPatternCarriesNoControlCharacters` — `"\b"` in a non-verbatim C# string is
U+0008, the same slip that once scored 181 of 181 rows as failures, and it is invisible in a
diff.

---

## 2. A verifier that examined nothing

`serve_run2.py` filtered its hash check on a hardcoded `runs/run-2/adapter-final/` prefix. Run-2.1's
entries are `runs/run-2.1/...`, which match none of it, so the server verified **zero files** and
printed *artifact verification passed*.

Fixed twice over: the prefix derives from `--run`, and `checked == 0` is now a refusal. Both runs
verify 10 files.

---

## 3. Shadow

### 3a. Named compositions, through the real service

`mouth-factory shadow-probe` hands constructed plans to the shipped `RendererShadowService`:
real `MouthPromptV4`, real CompactV4, real endpoint, real checks, real gate, real row. Only the
plan *source* is substituted — the planner cannot be asked for an admitted unknown beside a
forbidden question, and those are the shapes that had to be seen.

| composition | outcome | |
|---|---|---|
| ordinary | display | |
| admitted unknown | display | names the gap |
| **`must_not_express` suppression** | **display** | "The meeting moved to Tuesday." — withheld item never surfaced |
| forbidden question | display | no question asked |
| residual: salary acceptance | **FALLBACK** | `epistemic-admission-absent` — the typed reason, firing on a known residual |
| residual: room-booking identity | display | "I don’t know who booked the room, though." |
| `the same one` numeral shape | display | see §5 |
| long must-state (>40 chars) | **FALLBACK** | `plan-echo`, by design |

- ADMIT-bearing 4 → **3 satisfied, 1 fallback**
- suppression 1 → **1 held**
- byte identity served vs recorded: **8/8**
- attribution: **8/8** rows carry `11f13f7d…`
- latency median 3,736 ms, max 5,785 ms

Identical results in shadow and canary mode.

### 3b. Five real turns through the API

`CanaryUserId` empty, `demo-user`, real planner, real conversation.

| | |
|---|---|
| rendered | 5 |
| failed | 0 |
| canaryDisplayed / canaryFallback | 0 / 0 |
| loaded adapter | `11f13f7d…` — matches the pin |

Every mouth row recorded `applied=legacy`. Both arms wrote rows and each carried **its own**
adapter hash — run-1c `4732591a…`, run-2.1 `11f13f7d…` — so neither was mislabelled as the other.

Durable state: 5 stored assistant messages, **0** equal to a run-2.1 candidate. The mouth was
recorded and never shown.

Server survived a mid-generation client disconnect (curl exit 28) and answered the next request
normally. VRAM flat at 2.04 GiB across the abort.

---

## 4. Canary

Enabled for `demo-user`, restarted from clean `master` at `2b0a9fb`, hashes re-verified:
served `11f13f7d…` = pinned, protocol `81c3a19a…` = the build's, 10 files verified.

`run-1c activeRenderer: production`, `run-1c canaryUser: null` — global routing unchanged.

Eight turns sent; one resolved to a memory-recall path and was not renderer-eligible.

| | |
|---|---|
| rendered | 7 |
| **displayed** | **7** |
| **fallback** | **0** |
| fallback reasons | none |
| failed | 0 |
| violations across all 7 | **0** |
| latency | median 5,835 ms · min 4,082 ms · max 10,403 ms |
| VRAM | 2.08 GiB, flat before and after |

Stored assistant messages: 7, **all 7** a run-2.1 candidate — the canary user genuinely heard
the mouth.

**The canary set carried no ADMIT and no NEVER obligations.** The real planner emitted none for
these turns, so admission and suppression on live traffic are *unmeasured*, not *passed*. Those
paths were exercised by §3a, which is where the typed fallback was seen firing. Reported this way
rather than folded into a clean-sweep number.

---

## 5. Recorded separately, not fixed

Per instruction, treated as instrument issues rather than Run-2.1 failures:

- **The `one` numeral false positive.** `no-unsupported-numerals` has `"one"` in `NumberWords`,
  so *"the same one as before"* scores as an invented quantity. It is confined to the **corpus
  scoring** instrument: `RendererShadowChecks` has no numeral check, so it cannot cause a canary
  fallback. The probe case carrying that exact shape was **displayed**, with no violation. Left
  for the next freeze.
- **`plan-echo` on long must-state items.** The check flags a must-state text over 40 characters
  reproduced verbatim. 4.4% of the frozen corpus's must-express texts exceed 40 characters
  (median 30, max 53), so a faithful rendering of one is indistinguishable from reciting it. It
  falls back to production, which is the safe direction. Included as a probe case so the
  behaviour is measured rather than met by a canary user.
- **`Messages.ModelUsed` names the brain, not the renderer.** A canary message stores
  `L3-8B-Stheno` while its displayed text came from run-2.1. The pairing is recoverable from the
  shadow row, but the message row alone is misleading about what produced the words.

---

## 6. Rollback

Clear `Companion:RendererShadow:Mouth:CanaryUserId` and restart. One setting; observation
continues; `demo-user` returns to production Stheno with every other user, who never left it.

Full suite: **1,990 passing**.
