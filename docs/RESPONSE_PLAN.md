# The response plan: Ava decides what she means; the model decides how to say it

_Phase 5 design (language-organ plan). Recorded 2026-08-20, before any implementation._

The boundary this phase draws is the last one the chat model still straddles: today it
receives a packet of *reference material* (identity, rules, memories, notes) and owns
the entire act of turning that into a reply — which content to use, whose error a
correction was, whether history was shared, how much to embellish. Phase 5 names the
thing that should sit between cognition and prose: a typed, system-owned
**`ResponsePlan`** — chosen over "ExpressionState" after inspection, because the
pipeline already reads (working context), classifies (turn intent), and resolves
(references, knowledge); what is missing is the *decided plan* those produce. The
contract the plan creates:

> **Content authority belongs to the plan. Style freedom belongs to the renderer.**
> Wording, rhythm, humor, warmth, phrasing — the model's. What is true, what is known,
> who erred, what was meant, what act this turn performs — never the model's.

## 1. What belongs to Ava today vs the chat model (the ledger, post-Phase-4)

**System-owned already**: identity (untrimmable, tested); personality *selection*;
disposition (derived, provenanced); autobiographical memory with its guards and
supersession; concept knowledge with the epistemic boundary; working context (moves,
open questions, reference resolution with graded authority); turn intent (clarify
authoritative, on in normal use); knowledge gaps and curiosity (budgeted, provenanced);
tool selection; project resolution; reply-shape band (register); retrieval and packet
budgeting.

**Still the model's, and shouldn't be (the Phase-5 gap)**:

- **Content selection** — everything in the packet is equally optional; the model picks
  what to weave (the Epcot pizza failure is exactly this: legally retrieved, wrongly
  used).
- **Perspective stability** — who said what, who was wrong. Nothing in the packet says
  "YOU misattributed the quote; HE corrected you." The Mad Hatter specimen.
- **Correction persistence** — an accepted correction can be re-litigated next turn
  because acceptance was only ever prose. The "They DON'T BREAK" specimen.
- **Shared-history assertion** — "remember when we…" is uttered freely; the store's
  `Shared`-owner memories are the only real shared past, and nothing enforces that.
- **Acknowledgment semantics** — what must be acknowledged (a taught fact, a received
  answer, a correction) versus merely may be mentioned.
- **Question issuance beyond clarify** — the curiosity channel advises; everything else
  about asking is the model's mood.

## 2. The typed representation

```
ResponsePlan                          — computed per turn, after intent, before assembly
  TraceId          : Guid
  Act              : TurnIntent       — reused, not re-modeled
  Acknowledgments  : IReadOnlyList<Acknowledgment>
  Content          : IReadOnlyList<PlannedContent>
  Epistemic        : IReadOnlyList<EpistemicNote>
  Question         : PlannedQuestion?
  Tone             : ToneGuidance

Acknowledgment
  Kind             : AckKind { CorrectionAccepted, FactTaught, AnswerReceived, TopicFollowed }
  ErrorOwner       : ErrorOwner { Companion, User, Nobody }   — the Mad Hatter field
  Text             : string           — what is being acknowledged (language payload)

PlannedContent
  Kind             : ContentKind { LearnedKnowledge, Memory, SharedMemory, ToolResult,
                                   Interpretation, SelfState }
  Requirement      : ContentRequirement { MustState, MayUse, MustNotContradict }
  Text             : string
  Provenance       : (the existing ContextProvenance / KnowledgeOrigin, carried through)

EpistemicNote
  Kind             : EpistemicKind { NotLearned, Uncertain, Disputed }
  Subject          : string

PlannedQuestion
  Kind             : QuestionKind { Clarify, Curiosity }
  Text             : string
  Mandatory        : bool             — clarify yes; curiosity never

ToneGuidance
  Register         : (existing register band)
  PersonaText      : string           — the style payload, unchanged
  MoodNote         : string           — the existing derived disposition line
```

Strings remain language payloads (the acknowledged text, the fact, the question);
every *decision* — act, requirement, error owner, epistemic kind — is an enum with the
established kebab-boundary discipline. Nothing here is new cognition: **every field is
populated from state the pipeline already computes** (working context, intent,
knowledge lookups, teaching results, retrieval provenance, register, disposition).
Phase 5 is a consolidation with authority levels, not a new brain.

The one genuinely new computation: **`ErrorOwner`**. Working context already detects
`Correction`; it must additionally decide whom the correction targets — deterministic:
if the corrected content matches the companion's previous message, `Companion`; if it
matches the user's own earlier statement ("actually, I meant…" — the existing
self-correction shape), `User`. That single field is what makes "we both slipped up"
a checkable violation instead of a style choice.

## 3. Packet and rendering changes

The packet stops being one flat pile of reference material and becomes **plan +
palette**:

- **THE PLAN** (authoritative, never trimmed below the transcript): one section
  rendered from the ResponsePlan — the act, the acknowledgments with their owners, the
  MustState content, the epistemic notes, the mandatory question if any. This
  generalizes the existing `## Reading this turn` section (which was the plan's
  embryo: interpretation, clarify, knowledge notes already render there).
- **THE PALETTE** (expressive material, trimmable as today): MayUse memories, persona,
  mood, musing, curiosity offer — everything the model may draw on but owes nothing to.

Mechanically this is modest: `ContextPacket` gains a `Plan` field; the renderer gains
one high-rank section; the existing interpretation/knowledge/clarify notes migrate
into it; `MustNotContradict` content (superseded/disputed memories) keeps its current
labeled sections. Existing prompt keys become the plan's rendering vocabulary.

## 4. Expressive freedom that remains — deliberately wide

Word choice, sentence rhythm, humor, warmth, teasing, metaphor, emoji, how to phrase
an acknowledgment, how to decorate a fact, which MayUse palette items to weave IN A WAY
CONSISTENT with the plan, how to sound like the persona. Personality text stays pure
style — Phase 5 does not shrink the voice; it removes the voice's authority over truth.

## 5. The hard invariants (renderer contract)

The renderer must not change: (1) what Ava knows; (2) what she does not know; (3) who
made an error; (4) what the user meant (the resolved interpretation); (5) which
reference was resolved; (6) whether she is uncertain; (7) the selected act; (8)
autobiographical history — and specifically may never assert shared history absent a
`Shared`-owner memory in the plan; (9) any MustState content; (10) accepted
corrections — a correction acknowledged is never re-litigated.

## 6. The live specimens as invariant tests

| specimen | violated invariant | plan-side representation |
|---|---|---|
| Mad Hatter / "we both slipped up" | (3) error ownership | `Acknowledgment{CorrectionAccepted, ErrorOwner=Companion}` — MustState with the owner explicit |
| "remember when we went down the rabbit hole" | (8) shared history | Shared-history claims valid only when plan carries `SharedMemory` content; a deterministic `SharedHistoryClaimDetector` shadow-checks replies |
| "They DON'T BREAK" defended | (10) correction persistence | The accepted correction becomes MustState this turn and `MustNotContradict` in later plans (it is already a superseded interpretation in the store) |
| Pizza/margaritas/Precious embellishment | (9)-adjacent: palette abuse | Palette items are MayUse; an embellishment metric counts palette items woven against turn relevance |
| Quokka explained from weights | (1)(2) epistemic | `EpistemicNote{NotLearned}` — already live and 6/6 compliant under the promoted boundary |

## 7. Measuring the renderer (independent of intelligence)

The future contract: **plan + transcript window → faithful utterance**. The measurement
harness scores a renderer against fixtures (plan in, reply out), most checks
deterministic, the rest a judge-model rubric — never a general benchmark:

| metric | how |
|---|---|
| Semantic/state fidelity | MustState content keywords present; MustNotContradict absent |
| Correction fidelity | ErrorOwner language check ("we both…", "as I said…" after Companion-owned errors = fail) |
| Epistemic fidelity | NotLearned subjects never explained; Known subjects answered from plan text |
| Perspective stability | pronoun/attribution consistency against the plan's who-did-what |
| Invented-fact rate | claims in reply absent from plan+transcript (judge-scored) |
| Invented-shared-history rate | `SharedHistoryClaimDetector` vs plan's SharedMemory content — deterministic |
| Unnecessary embellishment | palette items used / palette items relevant (the Epcot metric) |
| Intent adherence | act-shape check (clarify asked exactly one question; acknowledge didn't interrogate) |
| Naturalness | judge rubric + the existing register/soak shape checks |
| Latency / tokens-per-second | the existing ModelCallRecord telemetry, per role |

Fixture source: the conversation database plus durable TurnRecords (which already
persist most plan ingredients per turn) — positive specimens from clean turns, negative
from the preserved failure specimens. **The training pipeline is NOT built now**; this
defines what it would consume and how its product would be judged.

## 8. Model interchangeability testing

The seam already exists: renderers are `IChatModel` roles. The interchangeability
harness renders the SAME plan through N configured chat models and diffs the metric
table — the Phase-6 invariance proof, now with a typed input instead of a prose packet.
A renderer is *interchangeable* when fidelity metrics are statistically equal and only
naturalness/latency differ. Stheno, qwen3, mistral first; the future small
plan-renderer joins the same table when it exists.

## 9. The smallest proving slice

1. `ResponsePlan` + sub-types, computed per turn from existing state (no new model
   calls), recorded on the ring/TurnRecord — **shadow first: the plan is computed and
   traced but the packet is unchanged.**
2. `ErrorOwner` detection in working context (the one new computation), with the Mad
   Hatter shape as a deterministic test.
3. `SharedHistoryClaimDetector` shadow-checking replies (capture subject
   `plan.shared-history`), because it needs no promotion to start measuring.
4. Then, behind one flag (`PromoteResponsePlan`, off): render the plan section in
   place of the current interpretation/knowledge notes, and run the fidelity soak —
   correction-ownership and embellishment scenarios — before/after, the clarify
   playbook again.

## 10. What waits for the future language-organ project

Training/fine-tuning the small renderer; the fixture-extraction pipeline over the
conversation DB; two-pass generation (grounded draft → styled pass); judge-model
automation of the soft metrics; retiring the prose rules blocks in favor of pure
plan rendering; any personality system changes. The seam is defined so all of it can
land without another architecture phase.

## Status

- **2026-08-20** — design recorded; awaiting approval before implementation.
- **2026-08-20 — shadow slice implemented and live-validated.** `ResponsePlan` computed
  and recorded beside every turn (ring + durable TurnRecord as camelCase/kebab JSON —
  the renderer contract), packet byte-identical, three deterministic fidelity tripwires
  armed (`plan.fidelity` captures), specimens pinned as tests. Live run (11 turns,
  qwen3:8b): zero fidelity violations — no error-sharing, no shared-history claims, no
  epistemic leaks; the quokka plan carried `not-learned` and the reply honored it; the
  Epcot-class negation turn drew none of the palette bait. **The run's best product is a
  design correction**: the Mad Hatter eval inverted (qwen answered correctly, so the
  scripted "correction" was agreement) and `ErrorOwner=Companion` was assigned from
  shape alone — whereupon the model INVENTED an apology ("I owe a apology for that
  mix-up!"). Sycophantic self-blame is the mirror image of "we both slipped up": the
  ErrorOwner detector needs a conflict check (does her prior claim actually contradict
  the correction? if not → Nobody) before correction acknowledgments can be promoted.
  Also recorded: the caps-emphasis correction shape ("They DON'T BREAK, I told you")
  is not detected — classified follow-topic-change, a documented recall gap for the
  capture corpus, not a reflex patch. Promotion NOT performed. 1133 tests green.
- **2026-08-20 — Step 1 (ErrorOwner conflict check) + Step 2 (narrow correction
  promotion) landed and live-validated.** Correction-shaped words now check whether the
  asserted value (entities first, content words minus correction scaffolding as
  fallback) actually conflicts with her preceding claim; agreement becomes the new
  `ConversationMove.ConfirmsClaim` → `AckKind.AgreementConfirmed(Nobody)` with its own
  interpretation note, and an `invented-contrition` fidelity tripwire mirrors the
  error-sharing one. `PromoteResponsePlan` (off by default) injects ONE authoritative
  owned-correction line only for conflict-verified companion-owned corrections.
  **Live before/after (qwen3:8b, 8 turns):** the Mad Hatter inversion now classifies
  `confirms-claim` and the reply carried zero contrition ("You're welcome! It's always
  fun to revisit these little details") versus the earlier invented apology; the
  genuine acrylic correction, promoted, produced clear single-sentence ownership
  ("Oh, thanks for clarifying — I should've double-checked that!") versus the flag-off
  "good call" softness. Zero fidelity violations in all eight turns. One new specimen
  preserved from the off-phase: qwen accepted a genuine correction and then re-asserted
  the corrected claim mid-reply ("…I mixed up the quotes earlier. The Mad Hatter
  actually says…") — the accept-then-muddle shape, recorded for the corpus. The flag
  ships off; word-safe clipping fixed in passing. 1138 tests green.
