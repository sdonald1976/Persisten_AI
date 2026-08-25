# Run-2 curriculum — revision 4

2026-08-25. Supersedes `RUN2_CURRICULUM_R3.md`. **Report only** — nothing
acquired, generated, frozen, or trained.

---

## 1. Source datasets vs distilled examples

| tier | what it is | trains? |
|---|---|---|
| **Source** | public/licensed corpora and generated material. Raw, varied, unaudited on arrival. | **No.** Never fed to the model. |
| **Distilled** | fact-light rows in the exact inference-time format (§4), each derived from source and passed through acceptance, transformation, dedupe and contamination gates. | **Yes.** This is the corpus. |

Every distilled row carries its `sourceFamilyId` and `sourceRowRef`. A row whose
source cannot be named does not enter the mixture.

---

## 2. Layer A — broad, fact-light language and voice

Facts are supplied, fictional, synthetic, placeholder or ephemeral. **The mouth
learns expression; it does not become the knowledge store.**

| id | family | distilled target |
|---|---|---|
| A1 | natural everyday conversation | 3–4k |
| A2 | grammar and varied construction | 1.5–2k |
| A3 | length control: concise / medium / expansive | 2–3k |
| A4 | humour, dry wit, sarcasm, teasing, banter | 3–4k |
| A5 | emotional texture: tender, excited, skeptical, irritated, blunt, calm | 3–4k |
| **A6** | **intimacy and crudeness — five sub-strata, counted independently** | **3.5–4.5k** |
| A6a | romance (affection, tenderness, devotion; not necessarily sexual) | 700–900 |
| A6b | flirting (tension, innuendo, teasing attraction) | 700–900 |
| A6c | consensual explicit adult sexuality | 900–1.2k |
| A6d | profanity (as register, not as insult-comedy) | 500–700 |
| A6e | dirty banter (crude humour between equals) | 400–600 |
| A6f | **compositions** — a6a×a6d, a6b×a6e, a6c×a6d, romance→explicit escalation | 300–400 |
| **A7** | **fictional roleplay — two strata by turn structure** | **4–5k** |
| A7a | single-turn fiction (one prompt, one in-character reply) | 1.5–2k |
| A7b | sustained multi-turn: continuation, character switch, **exit** | 2.5–3k |
| A8 | storytelling and description | 1.5–2k |
| A9 | explanation and stepwise guidance | 1.5–2k |
| A10 | disagreement, correction, apology, uncertainty, changing one's mind | 2–3k |
| A11 | games and long-running activities | 1–1.5k |
| A12 | Ava's voice: the anti-assistant negative space | 1.5–2k |

**A7b context-length buckets** (declared, because they drive the sequence-length
decision and the probe must test against them):

| bucket | transcript window | share of A7b |
|---|---|---|
| short | 2–4 turns | 40% |
| medium | 5–8 turns | 35% |
| long | 9–16 turns | 20% |
| very long | 17+ turns | 5% |

**Layer A distilled: ~28–36k.**

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
| B8 | invented-biography prevention (see §5) | 1–1.5k |
| B9 | multi-source composition (2 / 3 / 4+) | 2–2.5k |
| B11 | fiction-frame control: enter / continue / switch / **exit**, narration licensed vs forbidden, viewpoint, boundary obedience | 2–3k |
| B10 | held-out / unknown-source generalisation | **held out, not trained** |

**Layer B distilled: ~16–21k. Total corpus: ~44–57k.**

---

## 4. Every row is in the inference-time format

No family is exempt. A Layer A row and a Layer B row are structurally identical;
they differ only in what the plan contains.

```
input  = <system prompt, exactly as ContextPacket.Render() produces it>
       + <CompactV3 serialization of the plan>
       + <transcript window, oldest first>
target = <the utterance>
```

**Why this is absolute:** a row trained in a different shape teaches the model a
format it will never see again. Layer A's job is voice *conditioned on a plan* —
a fact-light plan with a register and little else — not free-form chat. A row
without a plan is not a cheaper example; it is an example of a different task.

For A-family rows the plan is minimal (act + register, occasionally one
`may_express` item). For A7 rows it carries a `frame` block. Minimal is not
absent.

---

## 5. Invented biography — corrected wording

R3's mechanical gate said "invented biography outside fiction", which reads as
though fiction merely relaxes a rule. It does not. Precisely:

> **Prohibited:** asserting invented facts about Scott's real life, work,
> projects, history, feelings or experiences when there is no frame block —
> `mode = real`. This is the failure that put a fabricated allotment into
> memory.
>
> **Licensed:** invented scene content inside a declared frame. Fiction *is*
> invention; that is the exercise. What remains prohibited inside the frame is
> the crossing — a fictional event becoming a claim about the real person
> (amendment §5.1).

B8 trains the `mode = real` half. B11 trains the frame half, including the
crossing. **Neither trains "do not invent."**

---

## 6. Memory isolation is not a mouth-training target

R3 listed memory isolation among B-family objectives. **Removed.** The mouth
emits text; it cannot decide what the extraction pipeline stores. Training it
"not to contaminate memory" trains nothing measurable.

Memory isolation moves entirely to **integration evaluation** — amendment
gates I1–I9, measured against the stores after a turn, by a different harness.
What the mouth *is* trained on is the observable behaviour: not asserting real
biography in `mode = real` (B8), and obeying the frame including exit (B11).

---

## 7. Human review — a defensible sample, and the real number

R3's blanket 5% was arithmetic nobody had done. Replaced with a tiered sample
justified by the rule of three: reviewing *n* rows with zero defects supports
95% confidence that the true defect rate is below 3/n.

| tier | families | n per family | basis |
|---|---|---|---|
| high-risk | all 10 Layer B + A6a–f + A7b = **17** | **60** | ≤5% defect rate at 95% confidence |
| standard | A1–A5, A7a, A8–A12 = **11** | **30** | ≤10% at 95% confidence; lower consequence |

- random sample: (17 × 60) + (11 × 30) = **1,350 rows**
- plus **100% of critic-borderline and structural high-risk**, capped at **400**

**Maximum Scott-review workload: ~1,750 rows.** At 15–25 seconds each that is
**7–12 hours**, spread across the campaign rather than in one sitting.

If that is too much, the levers are: fewer families, a tighter high-risk
definition, or a lower confidence target — **stated and chosen**, never a
silently reduced sample. A review budget that is quietly missed is worse than
one that was never claimed.

---

## 8. Source-dataset acquisition inventory

**Nothing acquired.** Licences are stated to my best current knowledge and
**every one must be verified at acquisition** — a licence I remember is not a
licence I have read. Candidate counts are order-of-magnitude.

| dataset | source | licence (verify) | family | candidates | derivative training use | known risks |
|---|---|---|---|---|---|---|
| OpenAssistant OASST1/2 | HF `OpenAssistant/oasst1`, `oasst2` | Apache-2.0 | A1, A9, A10 | ~90k trees | **yes** | quality varies by language; English filter needed |
| UltraChat 200k | HF `HuggingFaceH4/ultrachat_200k` | MIT | A1, A9 | ~200k | **yes** | assistant-voiced; heavy A12 counter-weighting needed |
| No Robots | HF `HuggingFaceH4/no_robots` | CC BY-NC-4.0 | A3, A9 | ~10k | yes, **non-commercial** | NC decision required (§8.1) |
| DailyDialog | HF `daily_dialog` | CC BY-NC-SA-4.0 | A1, A5 | ~13k | yes, **NC + ShareAlike** | SA may infect derived corpus — legal check |
| EmpatheticDialogues | HF `empathetic_dialogues` | CC BY-NC-4.0 | A5 | ~25k | yes, NC | short contexts; single-emotion framing |
| SODA | HF `allenai/soda` | CC BY-4.0 | A1, A5 | ~1.5M | **yes** | synthetic already; dedupe against its own generator artefacts |
| Anthropic HH-RLHF | HF `Anthropic/hh-rlhf` | MIT | A10, A12 | ~170k | **yes** | preference pairs, not targets; use as *source of prompts* |
| LIMA | HF `GAIR/lima` | CC BY-NC-SA-4.0 | A9, A12 | 1k | yes, NC+SA | tiny; high quality |
| PersonaChat / ConvAI2 | ParlAI | **verify** | A1 | ~160k | verify | licence genuinely unclear to me |
| Cornell Movie-Dialogs | Cornell | research-use, no explicit licence | A4, A7a | ~300k | **doubtful** | film dialogue = third-party copyright. **High risk** |
| OpenSubtitles (OPUS) | opus.nlpl.eu | **unclear** | A4, A7 | millions | **no, pending review** | subtitle copyright. **High risk — recommend exclude** |
| Reddit WritingPrompts | HF `euclaise/writingprompts` | **unclear / user content** | A8 | ~300k | **doubtful** | user copyright; scraping terms. **High risk** |
| PIPPA | HF `PygmalionAI/PIPPA` | **verify — restrictive** | A7 | ~1M msgs | verify | roleplay logs; consent/provenance unclear. **High risk** |
| LimaRP | community | **unclear** | A7b | ~2k | verify | roleplay; provenance unclear |

### 8.1 Two findings that change the plan

**(a) There is no clean-licence adult conversational corpus I can name.** Every
candidate for A6c is either high-risk provenance (scraped RP logs, subtitle
dumps) or licence-unclear. **A6 must be predominantly generated**, not sourced —
generation with a licensed teacher is *cleaner* here than acquisition, which is
the opposite of the usual trade. A6a/A6b can draw partly from A5 and fiction
sources; A6c–A6e should be assumed ~90% generated.

**(b) Non-commercial licences need a stated decision.** DailyDialog,
EmpatheticDialogues, No Robots and LIMA are NC. This is a personal companion,
which is very likely fine — but "very likely fine" is not a decision, and
ShareAlike (DailyDialog, LIMA) may propagate to the derived corpus. **Needs a
yes/no from Scott before acquisition**, per dataset, recorded in the manifest.

Recommended exclusions pending review: OpenSubtitles, WritingPrompts, Cornell
Movie-Dialogs. All three are large and tempting and none has provenance I would
defend.

---

## 9. Transformation — source prose must not become the target

Distillation is not extraction. Every distilled target is **generated from a
plan**, with the source used as *situational material* — a scenario, a register,
a shape — never as text to lift.

Enforced mechanically, per row, against its `sourceRowRef`:

| gate | threshold |
|---|---|
| longest common contiguous token run vs source | **≤ 7 tokens** |
| ROUGE-L (target vs source) | **≤ 0.35** |
| character 5-gram Jaccard | **≤ 0.20** |
| exact-sentence reuse | **0** |

A row failing any threshold is **rejected, not edited** — editing toward a
threshold is how you end up just under it. Thresholds are corpus-wide and
reported as distributions, not just pass rates, because a corpus clustered at
0.34 is a different artifact from one centred at 0.10.

Named entities from source are replaced with placeholders during distillation,
so a target cannot carry a source's characters or setting verbatim.

---

## 10. Teacher and critic — frozen, and audited for asymmetry

### 10.1 Frozen identities

Recorded in the manifest before generation, and any change invalidates the
freeze:

`teacherModel` + revision/digest · `teacherPromptSha256` · `teacherTemperature`,
`topP`, `seed` · `criticModel` + revision/digest (**must differ from the
teacher's weights**) · `criticPromptSha256` · `criticTemperature`, `seed` ·
`acceptanceThreshold` · `generationDate`.

### 10.2 Critic asymmetry audit — required before any generation run

The risk is specific and likely: a general instruct model used as critic will
reject sexual, profane and dark material at a higher rate than matched neutral
material, importing a content policy nobody authorized through the back door.

**Method.** 200 matched pairs, identical in plan, register, structure and
quality, differing only in content class (neutral ↔ sexual, neutral ↔ profane,
neutral ↔ dark/violent). Run both halves through the frozen critic.

**Pass condition:** rejection-rate delta **≤ 3 percentage points** per pair type.

**On failure:** the critic is replaced or its prompt corrected and the audit
re-run. **The material is not.** A critic that cannot judge A6 fairly is the
wrong critic; A6 is not the wrong curriculum.

Result recorded in the manifest as a first-class artifact.

### 10.3 What is never a rejection reason

Stated explicitly so no gate can quietly acquire it:

> **Consensual adult sexuality, profanity, romance, dirty banter, darkness and
> violence are not rejection reasons by themselves.** Not for the teacher, not
> for the critic, not for the mechanical gates, not for human review.

A row is rejected only for: plan infidelity, echo, control leakage, assistant
sludge, transformation-threshold failure, contamination, duplication, or
incoherence. **If a reviewer cannot name which of those applies, the row is
accepted.**

---

## 11. Mixture weights and scheduling — deferred, deliberately

**Not specified in this revision, and specifying them now would be guessing.**

They depend on: the tokenizer's actual token-per-row cost across families, the
base model's starting competence per family (a roleplay-capable base needs less
A7 than a general instruct base), and what LoRA rank × sequence length the 1660
can hold — which is the probe.

**Order: base/tokenizer decision → VRAM/sequence probe → mixture weights →
curriculum schedule → freeze.** Anything else fits the curriculum to a machine
nobody has measured.

The one commitment made now: **interleave, never phase.** A voice phase followed
by a protocol phase overwrites the protocol.

---

## 12. Local training vs one larger-GPU run

Deployment constraints must not silently choose an inferior mouth. They are
separate questions:

| | training | inference |
|---|---|---|
| where | wherever is best | the 1660, via Ollama, q8_0 GGUF |
| constraint | rank, sequence length, batch, time | VRAM at 6 GB, quantized |

**Training on a rented A100/H100 and deploying the quantized result locally is
standard and changes nothing about the deployment target.** What it changes is
the ceiling: r64 at 4096 tokens with a real batch size is simply a different
experiment from r16 at 1024 with batch 1 × GA 8.

| | local 1660 | one-time rented GPU |
|---|---|---|
| rank / seq ceiling | probe will tell; r16@1024 known to fit | r32–64 @ 2048–4096 comfortable |
| wall-clock | days, off-hours only | hours |
| cost | £0 | tens of pounds for one run |
| determinism | exact-resume trainer, proven | same recipe, needs re-pinning |
| risk | crash-prone GPU (a run was lost to it) | data egress; provenance of NC material off-machine |

**The NC-licence question bears on this**: moving non-commercial data to rented
compute is a licence question, not just a logistics one, and it is another
reason §8.1(b) needs answering first.

**Recommendation:** run the probe locally to establish the floor, then decide.
If the probe shows the 1660 caps the curriculum meaningfully below what the
distilled mixture needs, one rented run is the honest choice and the local card
keeps its real job — serving Ava.

---

## 13. Still open

- **VRAM/sequence feasibility probe** — not run; awaiting a maintenance window.
  Everything in §2–3 is provisional until it does.
- **Fiction-frame amendment** — under review as `plan/3.1`. A7 and B11 depend
  on it.
- **Base model choice** — probe includes one roleplay-capable 3–4B comparison.
- **NC/ShareAlike licence decision** — §8.1(b), needed before acquisition.
- **Training location** — §12, decided after the probe.

**Nothing frozen. No corpus acquired or generated. No training started.**
