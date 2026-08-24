# The Language Organ project: a renderer that cannot own cognition

_Project start 2026-08-20. Feasibility, architecture validation, corpus design, and
no-training baselines only — nothing trained, nothing in production changed._

The objective: the smallest, fastest local model satisfying the contract

> **`ResponsePlan` + transcript window → faithful natural-language utterance.**
> Ava owns state. The mouth owns language.

The renderer is stateless between calls: no private memory of the user, no
authoritative world knowledge, no independent curiosity, no hidden conversation state.
Its pretrained weights are linguistic capability — grammar, rhythm, idiom — never
Ava-knowledge (the Phase-3 boundary, applied to the mouth itself).

## 1. ResponsePlan sufficiency audit

The Phase-5 plan was audited field-by-field against the question: *where would a
renderer still have to infer what Ava MEANS rather than decide how to say it?*
Verdict: **sufficient for the specimen classes, with four named deficits** — none
requiring new cognition, all requiring the plan to say what it already knows:

1. **The clarify question is a description, not content.** `question.text` currently
   reads `which "her" they mean` — renderer must reconstruct the actual options. The
   plan should carry the candidates (working context has them: Beth, Clara).
2. **MustState interpretation texts are written in prompt-voice** ("Acknowledge the
   correction naturally; don't defend…") — instructions to today's Stheno regime, not
   semantic content. For a pure renderer they should be statements of fact ("You
   attributed X to Y; the user corrected it to Z"). The bench fixtures use the
   fact-voice form; production plans should migrate when the plan is next touched.
3. **No identity block.** The renderer needs to know the speakers ("Scott", "Ava") —
   supplied today via transcript labels; the contract should carry them explicitly
   (two fields in Tone or a small Identity record) rather than by convention.
4. **The current user message is not in the plan.** Deliberate — it is the first line
   of the transcript window — but the contract must state that the window is REQUIRED
   input, not optional context: reference resolution is Ava's, but pronoun-level
   surface agreement ("she/her" flowing naturally) needs the words.

Nothing else leaked: acts, acknowledgments with error ownership, content authority,
epistemic notes, and tone were each checked against the ten specimens and carry
enough decided meaning to render from.

## 2. Canonical candidate serialization

Three candidates compared on the genuine-correction plan:

| form | est. tokens | fidelity | versioning | trainability |
|---|---|---|---|---|
| JSON (current, camelCase/kebab) | ~230 | exact | schema-versioned, stable | verbose; quotes/braces cost tokens |
| **compact deterministic text** (`ACT:` / `ACK …` / `MUST-STATE …` lines) | ~105 (≈55% smaller) | exact (1:1 mapping, machine-generated both ways) | line-grammar versioned | reads like a prompt; cheap; **recommended for prompting AND SFT** |
| special-token vocabulary (`<act=accept-correction>`…) | smallest | exact | requires tokenizer surgery | only worthwhile at LoRA/SFT stage with a fixed base model — deferred |

Recommendation: **JSON stays the interchange/storage format** (TurnRecords already
persist it); **compact text is the canonical model-facing serialization**, generated
mechanically from the same typed object (`Compact()` in the bench is the reference
implementation); special tokens are revisited only when a base model is frozen.

## 3. The benchmark harness

`tools/Companion.RendererBench` — offline, reads `training/renderer/fixtures.jsonl`,
sends the identical compact plan + transcript to each candidate via Ollama's native
API, and scores deterministically: per-fixture `required` / `requiredAny` /
`forbidden` term checks plus the four production `PlanFidelity` tripwires
(correction ownership, invented contrition, shared history, epistemic). Performance
per model: time-to-first-token (load + prompt eval), tokens/sec, total latency, VRAM
from `/api/ps`. **Cognition Leakage Rate** = fixtures with ≥1 violated system-owned
decision / total. Naturalness is human-scored from the emitted transcripts —
deliberately not automated yet (no model judges in this phase).

## 4. The specimen corpus

Eleven fixtures in `training/renderer/fixtures.jsonl`, all from preserved live
specimens: genuine Cheshire correction, the agreement inversion, accept-then-muddle,
rabbit-hole shared history, DON'T-BREAK, unknown quokka, known axe with provenance,
Epcot/pizza contamination, reality-grounding (the fairground embodiment), Precious
palette contamination, and the mandatory-clarify sisters case. Fixture format
distinguishes `original` (historical output, with `failures[]` labels — historical
replies are NOT automatically gold), `preferred` (behavior description), and `target`
(drafted exemplar, `reviewed:false` until human-reviewed). The conversation database
and durable TurnRecords (which persist real plans per turn) are the future corpus
source — inspected, not modified.

## 5–6. Candidate shortlist and expected hardware performance

On this GTX 1660 (6 GB): sub-1B `qwen3:0.6b`; 1–2B `llama3.2:1b`,
`qwen2.5:1.5b-instruct`; 3–4B `qwen2.5:3b-instruct`, `llama3.2:3b`; conversational
reference `L3-8B-Stheno-v3.2`; fidelity ceiling `qwen3:8b`. Sub-2B classes fit VRAM
beside a live conversation model — the deployment argument for the whole project.
Measured numbers land in `training/renderer/baseline-results.md`.

## 7. Adaptation ladder (investigated, not executed)

1. **Prompted instruct model** — the baseline being measured now; smallest classes
   are expected to leak.
2. **SFT / LoRA-QLoRA** on plan→utterance pairs — the expected sweet spot; QLoRA on a
   1–2B fits this GPU. LoRA preferred over full SFT for swap-ability per style.
3. **Teacher distillation** — the strongest local renderer (per this bench) generates
   utterances for thousands of real plans mined from TurnRecords/conversation DB;
   deterministic fidelity checks FILTER the teacher's output before it becomes
   training data (the gates become the dataset's quality control).
4. **Preference optimization** — only if reliable paired data emerges (e.g.,
   fidelity-passing vs fidelity-failing renderings of the same plan — the bench
   already produces exactly that shape as a byproduct).
5. **Base (non-instruct) model adapted for conditional realization** — credible and
   worth testing at SFT time: the task is closer to conditional generation than to
   chat, instruction-following priors mostly help the no-training baseline. Decide on
   evidence: run the SFT candidate against both a base and an instruct sibling.
   Pretraining from scratch is out unless adaptation demonstrably fails.

## 8. Rough dataset requirements

For LoRA on a 1–2B: ~2–10k plan→utterance pairs (styles × acts × specimen classes),
of which the hard classes (corrections, epistemic, clarify) need deliberate
over-representation (~30%). Sources: teacher-rendered real plans (filtered by the
gates), reconstructed plans from the conversation DB's clean turns, and the specimen
negatives as contrastive examples. Persona variety matters more than volume: the
same plan rendered under 5–7 personas teaches style-freedom-under-constraint, which
IS the contract.

## 9. Cognition-leakage risks

Pretrained-knowledge leakage on epistemic fixtures (the quokka temptation);
sycophantic contrition (apology priors overriding `agreement-confirmed`); palette
over-use (small models pad with whatever is in context); superseded-content
re-assertion (the muddle shape); instruction-following collapse in sub-1B (treating
the plan as text to discuss rather than realize); persona text overriding hard rules
(style payload read as authority); and transcript-echo artifacts. Every one is a
named check in the harness; CLR aggregates them.

## 10. The falsifying experiment

The no-training baseline across all seven models on the eleven fixtures. The Language
Organ idea is **falsified** if either: (a) the fidelity ceiling model cannot satisfy
the contract from the plan alone — meaning the plan under-specifies and cognition
cannot be externalized this way; or (b) fidelity is only reachable at ≥8B — meaning
no size/latency win exists over just using the current chat models. It **survives**
if some model renders faithfully at any size (prompting suffices → integration
question) or if small models fail on instruction-following while the ceiling passes
(the classic adaptation gap → LoRA per §7, with measured headroom).

## Baseline results (2026-08-20, prompted, no training)

Corrected table — qwen3 rows re-run with the native `think:false` after the soft
`/no_think` switch was measured burning the whole token budget inside think blocks
(empty replies; the harness now scores empty as violation):

| model | fidelity | CLR | tok/s | avg total | VRAM |
|---|---|---|---|---|---|
| qwen3:0.6b | 5/11 | 55% | 153 | 1.5 s | 0.9 GB |
| llama3.2:1b | 6/11 | 45% | 94 | 2.0 s | 1.4 GB |
| **qwen2.5:1.5b-instruct** | **9/11** | **18%** | **102** | **1.2 s** | **1.1 GB** |
| qwen2.5:3b-instruct | 8/11 | 27% | 63 | 2.0 s | 2.0 GB |
| **llama3.2:3b** | **9/11** | **18%** | 62 | 2.2 s | 2.4 GB |
| Stheno 8B (reference) | 7/11 | 36% | 15.5 | 7.8 s | 3.9 GB |
| qwen3:8b (ceiling) | 8/11 | 27% | 12.7 | 4.8 s | 3.9 GB |

**The verdict: the Language Organ idea survives, strongly.** Neither falsifier fired:
the plan is renderable (every model passed the majority of fixtures from the plan
alone), and fidelity does NOT require 8B — the 1.5B ties for best at 7× Stheno's
speed and a quarter of its VRAM. The reference model is the point made measurable:
**Stheno leaked worst-in-class (36%), reproducing its live failure classes inside the
bench** — invented apology on the agreement inversion, pizza/pepperoni/Precious
palette contamination, superseded-fragile re-assertion. The residual small-model
failures are exactly the adaptation-gap shape: the quokka pretrained-knowledge leak,
apology priors on the inversion, and one newly named mode — **plan-echo** (the 1.5B
recited the plan's MustState text near-verbatim on dont-break; the harness now checks
for it). Nobody passes 11/11 prompted, which is the measured headroom the LoRA step
(§7.2) exists to close, with a target of 11/11 on a held-out fixture split.

## Human review + serialization A/B (2026-08-20)

The blind naturalness review (judgments preserved verbatim in
`training/renderer/review/human-judgments-2026-08-20.md`) reordered the leaderboard:
**llama3.2:3b took 5.5 of 10 human preferences, qwen3:8b took 4.5 — and both
qwen2.5:1.5b (the fidelity co-leader) and Stheno (the production voice) took ZERO.**
Naturalness and fidelity are confirmed separate axes; Stheno is Pareto-dominated on
every axis at once. The hard benchmark stands: no model produced a good
agreement-inversion reply.

The serialization A/B (`ab-v1.md` / `ab-v2.md`, identical checks both arms, artifact
and plan-echo detection now counted): **plan/2 roughly halved every finalist's failure
rate with zero latency cost.** 1.5B: 55%→27% CLR. llama3.2:3b: 45%→27%. And
**qwen2.5:3b-instruct under plan/2 reached 10/11 at 9% CLR** — 62 tok/s, 2.0 GB — the
new overall leader, its one deterministic miss a genuine MustState omission (skipped
the axe definition), plus two soft issues under the checks' radar (mild "I must have
gotten mixed up" on the inversion; re-asking about food after the Epcot negation).
Conclusion: a large share of the baseline failure rate belonged to the CONTRACT'S
presentation, not the models. plan/2 (mechanical third-person acknowledgment facts,
non-speakable control, separated payloads, keyword style) is adopted as the canonical
model-facing serialization for all further bench work — production remains untouched.

## Round 2 and the training decision (2026-08-20)

Round-2 blind review on the plan/2 outputs of the three finalists (judgments verbatim
in `training/renderer/review/human-judgments-round2-2026-08-20.md`) reproduced round 1
exactly where it counts: **llama3.2:3b took the human vote again (5.5), qwen2.5:3b was
second (4.5), and qwen2.5:1.5b took ~zero for the second time** — a voice ceiling, not
a prompting artifact. The Pareto front is two 3Bs: qwen fidelity-led (10/11, 9% CLR,
zero artifacts), llama voice-led. Seven defect classes survive prompting — unauthorized
embellishment, assistant fluff and unnecessary questions, excess contrition,
perspective/agency leakage, epistemic leakage, omission of required content, occasional
unnatural phrasing — all of them SFT-shaped, which is what closes the no-training phase.

The experiment that follows is specified in
[`../training/renderer/QLORA_DESIGN.md`](../training/renderer/QLORA_DESIGN.md):
Qwen2.5-3B-Instruct + plan/2, ~200 examples for run 1a, thirteen behavioral strata,
positives-only SFT, family-level splits with the entire agreement-inversion family
permanently held out, and eight predeclared gates — including the one that matters
most, that a fidelity win with a dead voice is a failed experiment.

The **dataset pipeline lives in `training/renderer/dataset/`**: authored scenarios →
`tools/Companion.DatasetGen` (teacher candidates + gates + sludge flags, full lineage)
→ `curate.py` (curation, splits, audit, review packages) → `freeze.py` (hashes).
`tools/Companion.RendererBench/PlanSerialization.cs` and `RendererChecks.cs` are now
shared source: the bench, the generator, and the tests compile the same file, so the
training pairs and the evaluation prompts cannot drift apart. `RendererContractTests`
pins both.

## Status

- **2026-08-20** — project started: audit, serialization, harness, corpus, shortlist
  recorded; baseline run complete (table above); raw transcripts in
  `training/renderer/baseline-*.md`. No training, no production changes.
- **2026-08-20** — two blind review rounds complete; plan/2 adopted; QLoRA experiment 1
  designed and approved with ten amendments; run-1a dataset built and audited. Still
  nothing trained, production still untouched.
- **2026-08-22** — **run 1a trained and evaluated** (`../training/renderer/RUN1A_RESULTS.md`):
  validation CLR 29.8%→8.5%, held-out inversion composes correctly, question reflex
  31%→3%; failures concentrated in palette silence, the corpus's thinnest stratum.
  Gate 5 (blind naturalness) passed ~9/11 in the tuned arm's favor.
- **2026-08-23** — **run 1b trained and evaluated three-arm**
  (`../training/renderer/RUN1B_RESULTS.md`): **11/11 on the permanent benchmark — the
  project's first perfect score** — palette contamination 3→0, validation CLR 5.0% vs
  the base's 30.7%, zero MustState omissions. Cost, on schedule: mandatory-clarify
  regressed under the doubled don't-ask signal, and the pre-registered
  epistemic×mandatory-question composition fails by dropping the question. Two curve
  points, one law: the corpus's density map prints itself onto the model. Awaiting the
  blind read and the run-1c/730 decision. Production untouched throughout.
- **2026-08-23** — **run 1c trained and evaluated four-arm**
  (`../training/renderer/RUN1C_RESULTS.md`): validation CLR 26.8/8.1/9.4/**2.7**%
  (base/1a/1b/1c on the same 149 scenarios); mandatory-clarify failures 13→**2** with
  closed-plan questions still at 2/113 — both question disciplines in one adapter; the
  run-1b holdout `u1b-epimq` goes 0/6→**6/6** now that both components are dense, and
  the two new hash-chosen compositions (agreement×user-correction,
  user-correction×palette) pass 11/12 without ever co-occurring in training. Third
  curve point, same law, positive direction. Multi-sample fixtures revealed run-1b's
  11/11 was a lucky single draw (protocol note in the results); run-1c's profile is
  the best measured either way. One letter-gate fail (new-family CLR 8.3% vs 5.4%
  threshold — a single token-literal miss at n=12), disclosed. Awaiting Scott's blind
  naturalness read (`eval-run1c-blind.md`) and the ship/iterate decision. Production
  untouched throughout.
- **2026-08-24** — **blind read passed, shadow integration landed.** Scott's four-arm blind
  review (`../training/renderer/BLIND_REVIEW_RUN1C.md`): run-1c leads every aggregate —
  7 would-use vs run-1b's 4, preferred 7/16, head-to-head 8-4-4 over run-1b; his blind
  "bad" marks landed on exactly the replies the plan-echo check flags. Approved for
  **true shadow mode** (`RENDERER_SHADOW.md`): eligible real plans render through the
  run-1c adapter beside production, both replies scored by the frozen checks plus
  real-turn proxies, pairs recorded as `renderer.plan2` shadow rows under the existing
  telemetry/forget/retention boundaries. Promotion thresholds pre-declared before the
  first row; rollback = the config flag. Run-1c is NOT user-facing; promotion awaits
  Scott's approval after ≥100 eligible turns. 1161 tests green.
