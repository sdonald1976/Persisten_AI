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

## Status

- **2026-08-20** — project started: audit, serialization, harness, corpus, shortlist
  recorded; baseline run in progress. No training, no production changes.
