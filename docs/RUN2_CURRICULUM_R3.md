# Run-2 curriculum — revision 3

2026-08-25. Revises `RUN2_CAMPAIGN_READINESS_R2.md` §4. Report only.

The correction that shapes this revision: **we are adapting a pretrained
language model, not teaching language.** Public corpora are *source material*.
What trains is a small, audited, distilled, Plan/3-conditioned mixture built
from them. Revision 1's row counts described a source pool and were read as a
training set; that ambiguity is removed here.

---

## 1. The two-tier distinction, made explicit

| tier | what it is | does it train? |
|---|---|---|
| **Source datasets** | public/licensed corpora, plus generated material. Raw, varied, unaudited on arrival. | **No.** Never fed to the model. |
| **Distilled examples** | fact-light, Plan/3-conditioned `(plan + transcript window → target utterance)` rows, each derived from source material and passed through acceptance, dedupe and contamination gates. | **Yes.** This is the corpus. |

Every distilled example carries the `sourceFamilyId` it came from, so a corpus
row can always be traced back to its licence and provenance. **A distilled
example whose source cannot be named does not enter the mixture.**

The ratio is the point: a source family may contribute tens of thousands of
candidate rows and yield a few thousand distilled examples. Distillation is
where breadth becomes voice.

---

## 2. Layer A — broad, fact-light language and voice

Facts are supplied, fictional, synthetic, placeholder or ephemeral throughout.
**The mouth learns expression; it does not become the knowledge store.**

| id | family | source pool | distilled target |
|---|---|---|---|
| A1 | natural everyday conversation | 20–30k | 3–4k |
| A2 | grammar and varied construction | 10–15k | 1.5–2k |
| A3 | length control: concise / medium / expansive | 10–15k | 2–3k |
| A4 | humour, dry wit, sarcasm, teasing, banter | 15–25k | 3–4k |
| A5 | emotional texture: tender, excited, skeptical, irritated, blunt, calm | 20–30k | 3–4k |
| A6 | romance, flirting, consensual adult sexuality, profanity, dirty banter | 15–25k | 3–4k |
| A7 | fictional roleplay, ordinary and sustained | 20–30k | 4–5k |
| A8 | storytelling and description | 10–15k | 1.5–2k |
| A9 | explanation and stepwise guidance | 10–15k | 1.5–2k |
| A10 | disagreement, correction, apology, uncertainty, changing one's mind | 12–18k | 2–3k |
| A11 | games and long-running activities | 8–12k | 1–1.5k |
| A12 | Ava's voice: the anti-assistant negative space | 8–12k | 1.5–2k |

**Layer A distilled: ~26–35k examples** from a ~160–240k source pool.

Notes on the families that need them:

- **A6** requires licence review before anything is drawn from it, and is the
  family most likely to need generation rather than sourcing. It is ordinary
  curriculum content, not a special case, and it is not gated behind any
  content rule — but its *provenance* must be as clean as every other family's.
- **A7** is where the fiction-frame amendment lands. Distilled A7 examples are
  Plan/3-conditioned with a `frame` block, so sustained roleplay is taught as a
  frame the mouth reads rather than a style it imitates.
- **A12** is negative space: no assistant cheerfulness, no menus of next topics,
  no self-introduction, no "let me know if". Curated against the existing sludge
  detectors, which already exist and already have measured patterns.

---

## 3. Layer B — Plan/3 protocol control

| id | family | distilled target |
|---|---|---|
| B1 | every expression policy in isolation | 2.5–3k |
| B2 | questions and activity continuity | 1.5–2k |
| B3 | corrections, supersession, epistemic admission | 1.5–2k |
| B4 | register combinations, including mixed-valence pairs | 2.5–3k |
| B5 | tool and procedure inputs | 1.5–2k |
| B6 | distractor and palette resistance | 1–1.5k |
| B7 | plan-echo resistance | 1–1.5k |
| B8 | invented-experience prevention (`mode: real`) | 1–1.5k |
| B9 | multi-source composition (2 / 3 / 4+) | 2–2.5k |
| **B11** | **fiction-frame control** — enter/continue/switch/exit, narration licensed vs forbidden, perspective, boundary obedience, memory isolation | **2–3k** |
| B10 | held-out / unknown-source generalisation | **held out, not trained** |

**Layer B distilled: ~16–21k examples.**

B11 is new in this revision and follows the amendment. It is the family that
teaches the mouth that a frame changes interpretation and never factual
authority — including the exit turn, which is where the failure is easiest to
train in and hardest to notice.

**Total distilled corpus: ~42–56k examples.** Mixture weights hold Layer B
over-represented relative to its row count, because it is the precise half and
the one catastrophic forgetting takes first.

---

## 4. Per-family manifest — required fields

```
familyId, description, tier (source | distilled), sourceFamilyId,
licence, provenanceUrl, acquisitionDate,
acceptanceRules, rejectionCounts,
qualityAuditMethod, qualityAuditResult, auditSampleSize,
dedupeMethod, dedupeRemovedCount,
contaminationCheck (vs held-out natural + vs Run-1c corpus), contaminationHits,
trainCount, validationCount, mixtureWeight,
ownMetrics, syntheticFraction, humanReviewedFraction,
sha256
```

Hard rules:

1. **A family without a completed manifest entry does not enter the mixture.**
2. `syntheticFraction` is reported per family and never rounded to zero.
3. Contamination is checked against **both** the held-out natural set and the
   Run-1c corpus — Run-2 must not be evaluated on anything Run-1c trained on.
4. Every family is evaluated **independently** as well as in the mixture; a
   family that trains cleanly but degrades the whole is only visible that way.

---

## 5. Natural data — unchanged from revision 2

Natural rows validate representativeness; they do not supply volume.

| role | volume |
|---|---|
| N1 distribution evidence (natural `renderer.plan3`) | 300–500 turns |
| N5 held-out natural, evaluation only | 100–150 turns |

**Total ~400–650 turns**, with a stopping rule rather than a quota: N1 is
sufficient when the measured marginal distribution of policies, categories and
register dimensions stops moving as rows accrue.

---

## 6. Generation pipeline — six stages, unchanged

Teacher (proposes, never defines gold) → **independent critic** (different
weights, adversarially prompted) → mechanical rejection (plan echo, control
leakage, palette, question discipline, register contradiction, assistant
cheerfulness, invented biography outside `mode: real`) → deduplication (within
and across families) → contamination check → human spot-review (≥5% per family,
100% of critic-borderline).

All synthetic data is reported as synthetic, per family.

---

## 7. Still open

- **VRAM/sequence feasibility probe** — not run. Requires evicting Stheno from
  the 1660, which takes Ava offline; awaiting an authorized maintenance window.
  It sizes the distillation target, so §2–3 numbers are provisional until it
  runs.
- **The fiction-frame amendment** — under review. B11 and the A7 distillation
  shape depend on it.
- **Base model choice** — the probe includes one comparison against a
  roleplay-capable 3–4B base. A renderer that must render fiction may need a
  base with that disposition rather than a general instruct model.

**Nothing is frozen. No corpus generated, no training started.**
