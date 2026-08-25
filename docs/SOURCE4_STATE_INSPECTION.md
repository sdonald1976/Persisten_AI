# Source 4 — inspection: mood, relationship, working context

Read-only audit. No contributor was built, nothing was changed. Each source is
reported separately, because they are in genuinely different health and a
summary would flatter two of them.

**Verdicts up front.**

| source | typed state | provenance | confidence | lifecycle | verdict |
|---|---|---|---|---|---|
| working context | **yes, rich** | yes | **yes, real** | turn-local, explicit | **PROCEED** |
| mood (Ava's own) | yes, scalars | **no** | no | decay, real | **STOP — small layer** |
| relationship | mixed | yes (signals) | no | **no expiry** | **STOP — small layer** |

## 0. What the authority model already gets right

Worth stating before the problems, because it changes what the missing layers
have to do. All five register sources are already registered as
**votes-only, restriction-incapable**:

```
Cap("persona",  "derived", [], register: true)
Cap("relationship", "derived", [], register: true)
Cap("mood", "derived", [], register: true)
Cap("working-context-register", "derived", [], register: true)
Cap("mirror", "observed", [], register: true)
```

Zero item grants means none of them can put a word into a plan — no
`must_not_express`, no mandatory claim, no expression restriction, no tool
authorization, no epistemic authority, no user preference. `MayProposeRegisterRestrictions`
is false for all five, and `ResolveRegister` rejects a restrictive vote from a
source lacking that flag as a recorded violation. **The boundary the instruction
draws is already enforced by the contract**; Source 4's job is to feed it
honestly, not to widen it.

Precedence is also already correct for the time requirement. §5.4 order is
`user-preference → hosting-config → privacy-audience → tool-authorization →
epistemic-integrity → persona → relationship → mood → working-context → mirror`.
A transient mood therefore **cannot** outrank a durable persona or an explicit
user preference — it loses to both by contract, and the loss is recorded.
Mixed dimensions survive independently because `ResolveRegister` groups by
dimension: warm+blunt, tender+plain, playful+skeptical each resolve in their own
slot with no cross-talk.

---

# Source 4a — Working context

**Verdict: healthy. Proceed when authorized.** This is the one source that needs
no new producer layer.

**1. Typed state vs prose.** Genuinely typed, and the richest in the codebase:
`Move` (required `ConversationMove` enum — NewTopic / ContinuesThread /
AnswersOpenQuestion / ResolvesReference / Correction / ConfirmsClaim),
`ResolutionConfidence` (Exact / Unambiguous / Guess), `CorrectionTarget`
(`ErrorOwner`), `OpenQuestions` (typed `OpenQuestionState` records with
`MessagesAgo`), `SalientEntities`, `Topic`, `BoundQuestion`, `RawQuery` /
`RetrievalQuery`, `ReferentSourceMessageId` + `ReferentSourceExcerpt`.

One prose field: `InterpretationNote`, and it is **producer-authored coaching**
by construction — e.g. *"The project reference is ambiguous — ask to clarify
rather than guessing"*. It must never become register authority. The guard
already exists: `PlanV3Codec.CoachingViolation` lists `working-context` among
`AuthoredSources`, so authored coaching from this source is lint-rejected at
assembly. No parsing of that note into votes, ever.

**2. Ownership.** The current turn. Not Ava's, not the user's.

**3. Lifetime.** Turn-local by explicit design — "computed, used, traced, and
discarded", never written to durable stores. Documented in the type itself.

**4. Identity / provenance / confidence / expiry / correction.** No stable id,
and it does not need one: the turn's `TraceId` identifies it and the state does
not outlive the turn. Provenance is real for references
(`ReferentSourceMessageId` + bounded excerpt). **Confidence is real and already
load-bearing** — `Consumable` gates extraction to Exact/Unambiguous, and `Guess`
is treated as a veto rather than a weak signal, which is exactly the
"low-confidence contributes nothing" discipline the instruction asks for.
Expiry is not applicable. Correction semantics exist for the conversation
(`CorrectionTarget`) but not for the reading itself — acceptable for turn-local
state.

**5. Production use.** Yes, heavily: `WorkingContext.Read(...)` feeds
`ResponsePlanner.Build`, the retrieval query, and extraction gating.
`InterpretationNote` reaches the packet as prose. It does **not** currently
reach `ToneGuidance`.

**6. Isolation.** Best of the three — built from one conversation's `recent`
window, so it is conversation-isolated as well as user-isolated.

**Honest vote surface.** Only two dimensions have a real typed signal:
`verbosity` from `Move` (a clarify/acknowledge-shaped turn is short), and
arguably `bluntness` from `CorrectionTarget`. Nothing else. `Topic`,
`SalientEntities`, and `InterpretationNote` are **not** register signals and
must not be turned into any.

---

# Source 4b — Mood (Ava's own internal modulation)

**Verdict: STOP. Genuine state and genuine decay, but no provenance and no
confidence.** Small producer layer proposed in §4b.7.

**First, a naming hazard.** Two unrelated things are called "mood":

- `CompanionStateSnapshot` — **Ava's** spirits/energy. This is the source the
  instruction means, and it is what `packet.MoodNote` actually carries.
- `MoodReading` / `MoodDetector` — a reading of **the user's** sentiment. This
  is not Ava's mood at all; it is relationship input (§4c) and it is the thing
  that risks claiming what the user feels.

Confirmed at the call site: `MoodNote = companionMood`, i.e.
`CompanionStateSnapshot.Describe()`. So the field flowing into
`ToneGuidance.MoodNote` is Ava's own state, correctly.

**1. Typed state vs prose.** Typed: `Spirits` (double, [-1,1]), `Energy`
(double, [0,1]), `Rested` (bool). Prose: `Describe()` and `SelfReport()`, both
template-selected from private `Tone`/`Pace` buckets. The buckets are
deterministic functions of the scalars — usable — but the prose is not, and must
not be parsed back.

**2. Ownership.** Ava. Cleanly so.

**3. Lifetime.** `Spirits` is **durable** (`UserProfile.CompanionSpirits` +
`CompanionSpiritsNudgedAt`) with real exponential decay — 4-day half-life toward
contentment, applied on read *and* before each nudge. `Energy` is turn-local,
derived from hour-of-day. `Rested` is derived from reflection recency (12h).
This is the strongest lifecycle story of the three.

**4. Identity / provenance / confidence / expiry / correction.**
- Identity: **none.** Spirits is a scalar column on the profile, not a record.
- Provenance: **none.** Nudges are `read → blend → write`; which turns moved
  spirits, and by how much, is unrecoverable. There is nothing for a vote's
  `evidenceRef` to point at.
- Confidence: **none.**
- Expiry: decay serves this, honestly.
- Correction: none.

**5. Production use.** Yes. `Describe()` → `packet.MoodNote` → the prompt **and**
`ToneGuidance.MoodNote` on the V2 plan. `NudgeAsync` is privacy-gated behind
`extractFacts` (`remember && !inCharacter`), so private and in-character turns do
not move her spirits — correct.

**6. Isolation.** Per **user**, not per conversation: spirits live on
`UserProfile`. Two simultaneous conversations with the same user share one mood,
and a nudge in one is visible in the other. Defensible (she is one person) but it
must be stated, not discovered later.

**7. The smallest missing layer.** Not a new subsystem — the scalars and decay
are real. What a lawful register vote needs and cannot get today:

- **A resolvable `evidenceRef`.** Proposal: a typed `CompanionMoodState` read
  model exposing `Spirits`, `Energy`, `Rested`, `NudgedAt`, and a deterministic
  `StateRef` derived from `(userId, nudgedAt)` — so a vote cites the state that
  produced it and the citation resolves, without inventing a record table.
- **A contribute-nothing floor.** With no confidence field, "low-confidence
  contributes nothing" needs a typed basis. The honest one already exists:
  decayed magnitude. Below a declared threshold (contentment), **no vote at
  all** — silence, not a "neutral" vote that would still displace a lower-ranked
  source.
- Optionally a bounded nudge ledger if provenance richer than "when" is ever
  wanted. Not required for a vote; noted, not proposed.

Honest vote surface if approved: `intensity` from decayed spirits, `verbosity`
from energy. Not warmth — warmth toward the user is a persona/relationship
concern, and sourcing it from Ava's spirits would make her coldness the user's
problem.

---

# Source 4c — Relationship

**Verdict: STOP. Identity and provenance exist; expiry, confidence, correction,
and — most importantly — the source/claim boundary do not.**

**1. Typed state vs prose.** Three layers, unequal:

- `EmotionalSignal` (durable table, append-only). Typed: `Sentiment` enum,
  `Valence` double, `Topic`, `ProjectId`, `FollowedUp` bool, `MessageId`,
  `Timestamp`, `Id`. **Also carries `Evidence` — the user's verbatim cue phrase
  — and `Label`.** Produced by `MoodDetector`, a deterministic offline lexicon
  (~90 words, intensifiers, negation). Not a model call, but *is* inferred
  sentiment.
- `RelationshipSnapshot` — derived on demand, never stored. Typed:
  `AverageValence`, `RecentMood`, `Trend`, `SignalCount`. Plus `RecentEmotion` /
  `RecentTopic` (strings from the signals).
- `FamiliaritySnapshot` — derived from two honest counts (`DaysKnown`,
  `UserMessages`), stage = the **lower** of the two reads. The cleanest typed
  signal in this group.

**2. Ownership.** Split, and this is the crux. `FamiliarityStage` describes the
**relationship** (supported interaction history — legitimate).
`EmotionalSignal` and everything derived from it describes **the user's
feelings** (inferred, not stated).

**3. Lifetime.** `EmotionalSignal` is **durable and unbounded**. The snapshot
windows to the last 10 signals, but that is a *read* cap, not a lifetime: a
signal from a year ago still counts if the user has been quiet since.
`FollowedUp` closes a concern for surfacing purposes only.

**4. Identity / provenance / confidence / expiry / correction.**
- Identity: yes (`Id`). Provenance: yes (`MessageId`, plus `Evidence`).
  Timestamps: yes.
- Confidence: **none.** Lexicon magnitude is intensity, not confidence — "I
  hate that this took so long" scores like real distress.
- Expiry / validity: **none.** This directly violates "relationship evidence
  must not become permanent merely because it was once observed."
- Correction: **none.** There is no path for the user to say "that's not how I
  felt"; the log is documented as never rewritten.

**5. Production use.** Yes. `RelationshipSnapshot.Describe()` →
`packet.RelationshipNote`; `FamiliaritySnapshot.Describe()` →
`packet.FamiliarityNote`. Both reach the prompt as prose. **Neither reaches the
V2 `ResponsePlan`** — only register/mood/persona do.

**6. Isolation.** Per user, not per conversation — same shared-state note as
mood. Cross-user isolation is enforced at the store.

**7. The boundary problem, stated plainly.** `RelationshipSnapshot.Describe()`
emits lines like *"The user has seemed stressed about the interview lately"* and
*"The user has been in good spirits lately."* Those are **claims about what the
user feels**, derived from lexicon inference. Today they are prompt prose, which
is the status quo and out of scope to change. But they must never become a
register vote's reason, an item, or anything a renderer could recite as fact.
A relationship vote may say *how Ava should modulate*; it may never assert the
user's inner state, and it may not carry `RecentEmotion`/`RecentTopic` text.

**8. A retention finding, incidental but real.** `EmotionalSignals` rows are
**not swept by `/forget` and not pruned by database maintenance** (verified
against `MemoryCurator` and `DatabaseMaintenance`). They durably hold the user's
verbatim cue phrases (`Evidence`) and extracted topics. Forgetting the memory a
feeling was attached to leaves the feeling — and its quoted words — behind.
This is independent of Source 4 and of whether relationship may vote; it is a
disclosure/retention gap in an existing durable store, and I am flagging rather
than fixing it because it is outside this inspection's authorization.

**9. The smallest missing layer.** Two pieces, both on the producer side:

- **Validity on `EmotionalSignal`:** a `Confidence` (the detector knows whether
  a cue fired vs. only aggregate valence — that distinction exists today and is
  thrown away) and an explicit decay or `ExpiresAt`, so stale and thin signals
  contribute nothing by rule rather than by window luck.
- **A typed `RelationshipReadModel` for V3:** `FamiliarityStage`, a
  decay-weighted valence, signal counts, and validity status — carrying **no
  user-sentiment prose, no `RecentEmotion`, no `RecentTopic`, no `Evidence`**.
  The prose `Describe()` stays exactly where it is, feeding the packet,
  untouched.

Honest vote surface if approved: `warmth` and `teasing` from `FamiliarityStage`
(supported interaction history — the legitimate half), and at most a gentle
`intensity` from decay-weighted valence. Nothing from `RecentEmotion` or
`RecentTopic`.

---

# Cross-cutting notes

**Do not parse prose back into votes.** Four prose producers exist here —
`CompanionStateSnapshot.Describe()`, `RelationshipSnapshot.Describe()`,
`FamiliaritySnapshot.Describe()`, `WorkingContextState.InterpretationNote`. All
four are template or authored output. Every proposed vote above sources from the
scalar or enum *behind* the prose, never the prose.

**No new regex.** Nothing proposed here adds inference. `MoodDetector`'s lexicon
already exists and is not being widened; the proposals only preserve information
it currently discards.

**Diagnostics.** `ResolveRegister` already records dimension, value, winning
source, reason code, and losers — source/dimension/winner/loser/reason are
covered. What is missing for the instruction's list is **confidence/validity
status**, which cannot be reported until §4b.7 and §4c.9 exist. That is another
reason the two stops are real rather than procedural.

**Sensitive state.** Both durable stores here (spirits, emotional signals) are
written only behind the existing privacy gate. Disclosure/retention for any V3
row must be decided from the item's own classification, independent of whether
the signal was allowed to affect this turn's register — the two are separate
axes and the proposals keep them separate.

# Recommendation

1. **Working context** may proceed to contributor work on the narrow surface
   named in §4a — `verbosity` from `Move`, nothing from prose.
2. **Mood** and **relationship** need the small producer layers in §4b.7 and
   §4c.9 before either may vote. Both are additive read-model/field changes, not
   subsystems.
3. The `EmotionalSignals` `/forget` gap (§4c.8) needs a decision independently of
   Source 4.

Nothing in this inspection changed any code. V2, Run-1c, routing, and displayed
output are untouched.
