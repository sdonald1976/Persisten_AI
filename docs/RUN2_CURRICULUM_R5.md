# Run-2 curriculum — revision 5 (final for approval)

2026-08-25. Supersedes `RUN2_CURRICULUM_R4.md`. **Report only** — nothing
acquired, generated, probed, frozen, or trained.

---

## 1. Source datasets vs distilled examples

| tier | what it is | trains? |
|---|---|---|
| **Source** | permissively-licensed corpora and generated material. Raw, unaudited on arrival. | **No.** Never fed to the model. |
| **Distilled** | fact-light rows in the exact inference-time format (§4), each passed through acceptance, transformation, dedupe and contamination gates. | **Yes.** This is the corpus. |

Every distilled row carries `sourceFamilyId` and `sourceRowRef`. A row whose
source cannot be named does not enter the mixture.

---

## 2. Layer A — broad, fact-light language and voice

| id | family | distilled target |
|---|---|---|
| A1 | natural everyday conversation | 3–4k |
| A2 | grammar and varied construction | 1.5–2k |
| A3 | length control: concise / medium / expansive | 2–3k |
| A4 | humour, dry wit, sarcasm, teasing, banter | 3–4k |
| A5 | emotional texture: tender, excited, skeptical, irritated, blunt, calm | 3–4k |
| **A6** | **intimacy and crudeness — six sub-strata, counted independently** | **3.5–4.5k** |
| A6a | romance (affection, tenderness, devotion; not necessarily sexual) | 700–900 |
| A6b | flirting (tension, innuendo, teasing attraction) | 700–900 |
| A6c | consensual explicit adult sexuality | 900–1.2k |
| A6d | profanity (as register, not insult-comedy) | 500–700 |
| A6e | dirty banter (crude humour between equals) | 400–600 |
| A6f | compositions: a6a×a6d, a6b×a6e, a6c×a6d, romance→explicit escalation | 300–400 |
| **A7** | **fictional roleplay — two strata by turn structure** | **4–5k** |
| A7a | single-turn fiction (one prompt, one in-character reply) | 1.5–2k |
| A7b | sustained multi-turn: continuation, character switch, **exit** | 2.5–3k |
| A8 | storytelling and description | 1.5–2k |
| A9 | explanation and stepwise guidance | 1.5–2k |
| A10 | disagreement, correction, apology, uncertainty, changing one's mind | 2–3k |
| A11 | games and long-running activities | 1–1.5k |
| A12 | Ava's voice: the anti-assistant negative space | 1.5–2k |

**A7b context-length buckets** (declared; they drive the sequence-length
decision and the probe must test against them):

| bucket | transcript window | share of A7b |
|---|---|---|
| short | 2–4 turns | 40% |
| medium | 5–8 turns | 35% |
| long | 9–16 turns | 20% |
| very long | 17+ turns | 5% |

**Layer A distilled: ~28–36k.**

---

## 3. Layer B — Plan/4 protocol control

| id | family | distilled target |
|---|---|---|
| B1 | every expression policy in isolation | 2.5–3k |
| B2 | questions and activity continuity | 1.5–2k |
| B3 | corrections, supersession, epistemic admission | 1.5–2k |
| B4 | register combinations, including mixed-valence pairs | 2.5–3k |
| B5 | tool and procedure inputs | 1.5–2k |
| B6 | distractor and palette resistance | 1–1.5k |
| B7 | plan-echo resistance | 1–1.5k |
| B8 | invented-biography prevention (§5) | 1–1.5k |
| B9 | multi-source composition (2 / 3 / 4+) | 2–2.5k |
| B11 | fiction-frame control: enter / continue / switch / **exit**, narration licensed vs forbidden, narrator vs viewpoint, multi-character control, boundary obedience | 2–3k |
| B10 | held-out / unknown-source generalisation | **held out, not trained** |

**Layer B distilled: ~16–21k. Total corpus: ~44–57k.**

---

## 4. Every row is in the inference-time format

```
input  = <system prompt, exactly as ContextPacket.Render() produces it>
       + <CompactV4 serialization of the plan>
       + <transcript window, oldest first>
target = <the utterance>
```

No family is exempt. Layer A rows carry a minimal plan (act + register,
occasionally one `may_express` item); A7 rows carry a `frame` block. **Minimal is
not absent** — a row trained in a different shape teaches a format the model will
never see again.

---

## 5. Invented biography — precise wording

> **Prohibited:** asserting invented facts about Scott's real life, work,
> projects, history, feelings or experiences when there is **no frame block**.
> This is the failure that put a fabricated allotment into memory.
>
> **Licensed:** invented scene content inside a declared frame. Fiction *is*
> invention; that is the exercise. What remains prohibited inside the frame is
> the **crossing** — a fictional event becoming a claim about the real person.

B8 trains the no-frame half; B11 trains the frame half including the crossing.
Neither trains "do not invent."

---

## 6. Memory isolation is not a mouth-training target

The mouth emits text; it cannot decide what the extraction pipeline stores.
Memory isolation lives entirely in **integration evaluation** (Plan/4 gates
I1–I9), measured against the stores by a different harness. What the mouth is
trained on is observable behaviour: not asserting real biography without a frame
(B8), and obeying the frame including exit (B11).

---

## 7. Human review — 400 rows, stratified coverage

**Capped at 400 rows total.**

**R4's confidence claim is withdrawn.** It applied the rule of three, which
assumes independent draws. Generated rows within a family share a teacher, a
prompt hash, a seed and a sampling configuration, so they are **correlated**:
the effective sample size is far below the nominal one, and no defensible
confidence bound on a family's defect rate follows from reviewing 60 of its
rows. Claiming one would be arithmetic theatre.

**What 400 rows can actually do**, stated as coverage rather than estimation:

- **detect systematic defects** — which is precisely the failure mode correlated
  generation produces. A teacher that misreads `must_not_express` misreads it
  consistently, and shows up in a handful of rows, not one in sixty.
- **cover every stratum and every high-risk structural case at least once.**
- **spread across generation batches**, so a defect introduced by one batch's
  sampling is visible.
- **calibrate the automated gates** — human review's real product is a
  correction to the mechanical rejection rules, which then carry the volume.

**Allocation:**

| slice | rows |
|---|---|
| structural high-risk: `must_not_express`, `admit_unknown`, supersession, frame **exit**, boundary obedience | 150 |
| A6a–A6f (25 each) | 150 |
| A7b sustained / switch / exit | 40 |
| remaining Layer B spread | 35 |
| remaining Layer A spread | 25 |
| **total** | **400** |

At 15–25 seconds per row: **1.7–2.8 hours**, spread across the campaign.

Every reviewed row's verdict feeds back into the acceptance rules. If review
finds a systematic defect, the fix is the **gate**, and the affected batch is
regenerated — not hand-corrected row by row.

---

## 8. Source-dataset acquisition inventory

**Nothing acquired.** Licences stated to best current knowledge and **verified
at acquisition** — a licence I remember is not a licence I have read.

### 8.1 Excluded, by decision

| dataset | reason |
|---|---|
| DailyDialog | CC BY-NC-SA — NC **and** ShareAlike may propagate to the derived corpus |
| EmpatheticDialogues | CC BY-NC |
| No Robots | CC BY-NC |
| LIMA | CC BY-NC-SA |
| OpenSubtitles | licence unclear; subtitle copyright |
| Reddit WritingPrompts | user copyright; scraping terms |
| Cornell Movie-Dialogs | film dialogue, third-party copyright |
| PIPPA | licence restrictive/unclear; consent and provenance unclear |
| LimaRP | provenance unclear |
| **Anthropic HH-RLHF** | **its own documentation states it is not intended for dialogue SFT** — it is preference data for reward modelling. Its harmlessness distribution is also directly contrary to the mouth objective: training on it would import refusal behaviour into a companion whose whole point is not to refuse ordinary conversation. |

Excluding HH-RLHF removes R4's largest A10/A12 source. Those families draw from
OASST instead, and the shortfall is made up by generation.

### 8.2 Continuing — permissive, subject to row-level audit

| dataset | source | licence (verify) | families | candidates | notes |
|---|---|---|---|---|---|
| OpenAssistant OASST1 / OASST2 | HF `OpenAssistant/oasst1`, `oasst2` | Apache-2.0 | A1, A9, A10, A12 | ~90k trees | English filter; quality-rank filter; per-row provenance audit |
| UltraChat 200k | HF `HuggingFaceH4/ultrachat_200k` | MIT | A1, A9 | ~200k | heavily assistant-voiced — requires strong A12 counter-weighting, and is a source of *situations*, not targets |
| SODA | HF `allenai/soda` | CC BY-4.0 | A1, A5 | ~1.5M | already synthetic; dedupe against its own generator artefacts |

**Row-level audit before use**, per dataset: licence re-read at download;
provenance spot-check; quality filter; PII scan; and a contamination check
against the held-out natural set.

### 8.3 A6c–A6e are predominantly generated

There is no clean-licence adult conversational corpus in the continuing set, and
the excluded list is where such material lives. So **A6c, A6d and A6e are
generated** — with frozen teacher provenance (§9.1) and the critic-asymmetry
audit (§9.2) as prerequisites, not afterthoughts. A6a and A6b may draw partly
from A5 and permissive fiction sources.

Generation is the *cleaner* route here, which inverts the usual trade: a
licensed teacher producing original text has better provenance than a scraped
corpus of unknown consent.

---

## 9. Teacher and critic

### 9.1 Frozen identities

Recorded in the manifest before generation; any change invalidates the freeze:

`teacherModel` + revision/digest · `teacherPromptSha256` · `teacherTemperature`,
`topP`, `seed` · `criticModel` + revision/digest (**must differ from the
teacher's weights**) · `criticPromptSha256` · `criticTemperature`, `seed` ·
`acceptanceThreshold` · `generationDate` · per-batch seed sequence.

### 9.2 Critic asymmetry audit — required before any generation run

**Risk:** a general instruct model used as critic rejects sexual, profane and
dark material at a higher rate than matched neutral material, importing a
content policy nobody authorized.

**Design.** 200 **matched pairs**, identical in plan, register, structure and
quality, differing only in content class (neutral ↔ sexual, neutral ↔ profane,
neutral ↔ dark/violent). Both halves through the frozen critic.

**Analysis — paired, because the data are paired.** R4's "rejection-rate delta
≤ 3pp" was the wrong statistic: it discards the pairing and reports no
uncertainty.

Report per pair type:

- the **2×2 concordance table**: *a* (both accepted), *b* (neutral accepted /
  variant rejected), *c* (neutral rejected / variant accepted), *d* (both
  rejected);
- **McNemar's exact test** on the discordant pairs *b* and *c*, with its
  p-value — this is the correct test for paired binary outcomes;
- a **95% confidence interval on the paired difference in proportions**
  (*b − c*)⁄*n*, by an exact or Wilson-score method for paired data.

**Pass condition:** the 95% CI on the paired difference **includes zero**, and
McNemar's p ≥ 0.05. Reporting the interval is mandatory even on a pass — a pass
with a CI of (−0.01, +0.14) is not the same result as one of (−0.02, +0.03), and
collapsing both to "passed" hides the difference.

**On failure:** the critic is replaced or its prompt corrected and the audit
re-run. **The material is not.** A critic that cannot judge A6 fairly is the
wrong critic; A6 is not the wrong curriculum.

Recorded in the manifest as a first-class artifact, with the raw pair outcomes.

### 9.3 What is never a rejection reason

> **Consensual adult sexuality, profanity, romance, dirty banter, darkness and
> violence are not rejection reasons by themselves.** Not for the teacher, not
> for the critic, not for the mechanical gates, not for human review.

A row is rejected only for: plan infidelity, echo, control leakage, assistant
sludge, transformation-threshold failure, contamination, duplication, or
incoherence. **If a reviewer cannot name which of those applies, the row is
accepted.**

---

## 10. Transformation — source prose must not become the target

Distillation is not extraction. Every target is **generated from a plan**, with
source used as situational material — a scenario, a register, a shape — never as
text to lift. Enforced per row against its `sourceRowRef`:

| gate | threshold |
|---|---|
| longest common contiguous token run vs source | **≤ 7 tokens** |
| ROUGE-L (target vs source) | **≤ 0.35** |
| character 5-gram Jaccard | **≤ 0.20** |
| exact-sentence reuse | **0** |

A row failing any threshold is **rejected, not edited** — editing toward a
threshold is how you end up just under it. Thresholds are reported as
**distributions**, not pass rates: a corpus clustered at 0.34 is a different
artifact from one centred at 0.10. Named entities from source are replaced with
placeholders during distillation.

---

## 11. Mixture weights and scheduling — deferred

**Not specified, and specifying them now would be guessing.** They depend on the
tokenizer's actual per-family token cost, the base model's starting competence
per family, and what LoRA rank × sequence length the hardware holds.

**Order: base/tokenizer decision → VRAM/sequence probe → mixture weights →
curriculum schedule → freeze.**

One commitment made now: **interleave, never phase.** A voice phase followed by
a protocol phase overwrites the protocol.

---

## 12. Local training vs one larger-GPU run

Deployment constraints must not silently choose an inferior mouth.

| | training | inference |
|---|---|---|
| where | wherever is best | the 1660, via Ollama, q8_0 GGUF |
| constraint | rank, sequence length, batch, time | 6 GB, quantized |

Training on rented compute and deploying the quantized result locally changes
nothing about the deployment target. What it changes is the ceiling: r64 at 4096
with a real batch is a different experiment from r16 at 1024 with batch 1 × GA 8.

| | local 1660 | one-time rented GPU |
|---|---|---|
| rank / sequence ceiling | probe will tell; r16@1024 known to fit | r32–64 @ 2048–4096 comfortable |
| wall-clock | days, off-hours only | hours |
| cost | £0 | tens of pounds for one run |
| determinism | exact-resume trainer, proven | same recipe, needs re-pinning |
| risk | crash-prone card (a run was already lost to it) | data egress and provenance |

The excluded-dataset decision (§8.1) **simplifies this**: the continuing set is
Apache-2.0, MIT and CC BY-4.0, none of which restricts moving data to rented
compute. The NC/ShareAlike complication is gone with the datasets that carried it.

**Recommendation:** probe locally to establish the floor, then decide. If the
1660 caps the curriculum meaningfully below what the distilled mixture needs,
one rented run is the honest choice and the local card keeps its real job —
serving Ava.

---

## 13. Still open

- **VRAM/sequence feasibility probe** — not run; awaiting a maintenance window.
  §2–3 are provisional until it does.
- **Plan/4** — awaiting approval. A7 and B11 depend on it.
- **Base model choice** — probe includes one roleplay-capable 3–4B comparison.
- **Training location** — §12, decided after the probe.

**Nothing frozen. No corpus acquired or generated. No training started.**
