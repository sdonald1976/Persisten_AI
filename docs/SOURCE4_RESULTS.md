# Source 4 — results, reported per source

Run 2026-08-25 against the four frozen plans. Four independently revertible
commits. **Each source is reported on its own; none of them inherits another's
status.**

Suite: **1384 passing** in `Companion.Tests` (59 of them Source 4) plus **31**
prototype goldens including the 804-plan corpus. Zero failures.

| phase | status | live / constructed |
|---|---|---|
| Phase 0 — EmotionalSignal retention | **COMPLETE** | live (real store, real `/forget`) |
| Source 4a — working context | **COMPLETE** | live (real shadow call site) |
| Source 4b — mood | **COMPLETE, with energy BLOCKED** | live (real tracker, real call site) |
| Source 4c — familiarity | **COMPLETE, with sentiment BLOCKED** | live (real call site) |

---

## Phase 0 — EmotionalSignal retention: COMPLETE

Commit `66f11c0`. Not part of any source; a privacy repair to an existing
durable store.

`MessageId` existed but was documented as a soft hint and nothing linked
through it. It is now an exact forgetting handle, joined by a durable
`EvidenceEventId` assigned at capture. `ForgetByEvidenceAsync` takes **ids and
nothing else** — a reflection test asserts the signature carries no string but
the user id, because a path that *can* compare text eventually will.

Forgetting redacts rather than deletes: `Evidence` and `Topic` (the user's own
words) are purged; timestamp, sentiment, valence and the lexicon `Label` stay as
privacy-permitted metadata. `RelationshipTracker` excludes forgotten rows, so a
redacted signal contributes **nothing** — not a neutral reading, no reading.

Retention is now declared: `EmotionalSignalRetention = 180 days`, swept from
`SleepCycle` beside the diagnostics and experience sweeps.

**Live**, 12 tests against the real store and the real `MemoryCurator` path.
The adversarial cases are 2 and 3: overlapping cue text, and byte-identical cue
text from a different message. Neither is touched unless its own id was
forgotten. Plus cross-user with deliberately colliding ids, two simultaneous
conversations, missing evidence both ways, and double-forgetting.

---

## Source 4a — working context: COMPLETE

Commit `dc0f2da`.

**Authority in full:** verbosity, from `ConfirmsClaim` and `Correction`, both →
`short`. Every other move votes nothing.

**Prose is unreachable by construction.** The constructor takes a trace id, a
`Move`, a `ResolutionConfidence?` and an optional referent message id — no
string at all. `InterpretationNote`, `Topic`, `SalientEntities`, `RawQuery` and
the rest are not in scope to be parsed. Asserted by reflection.

**`Guess` suppresses the entire contribution**, not just the reference: if the
turn does not know what the user referred to, its read of what kind of turn this
is does not get authority either.

**Turn-local validity expires automatically** — built for one trace id, returns
empty for any other.

**Live** through the real shadow call site: a two-turn exchange where the second
turn genuinely reads as a correction, and the native row carries the register
decision naming `working-context-register`.

---

## Source 4b — mood: COMPLETE, with energy BLOCKED

Commit `7a645a5`.

**The amendment was right.** The inspection proposed a `StateRef` derived from
`(userId, nudgedAt)`. That is a hash of mutable state: it identifies nothing and
resolves to nothing. Replaced with `CompanionMoodTransitions`, an append-only
versioned log; the `StateRef` a vote cites is a row id that resolves forever.
**No transition, no vote.**

**Concurrency is now guarded, and it was not before.** Spirits were a
read-blend-write scalar, so two simultaneous nudges silently lost one. The
`(UserId, Version)` unique index turns a lost update into a conflict the append
retries onto the winner's value. Twelve concurrent nudges produce twelve
contiguous versions that **compose** — each landing on the last. Tested.
Deterministic replay from the log is tested separately.

**Mapping:** spirits → `intensity`; `flat` at ≤ −0.3, `raised` at ≥ +0.3,
silence between. Deliberately **not warmth** — warmth toward the user belongs to
persona and relationship, and sourcing it from her spirits would make her low
mood the user's problem, which her own contract forbids.

**Floor = 0.3, and it produces silence** rather than `intensity=even`, which
would still displace a lower-ranked source. Decay carries every mood back under
the floor on its own, so nothing has to expire it.

**BLOCKED: energy votes nothing.** It is derived fresh from the clock and has no
transition event, so it has no resolvable provenance. Rather than invent one or
mix two evidence kinds in one contributor, its dimension set is declared empty.
This is a real limitation, not a completed item.

**Live** through the real tracker and the real call site.

---

## Source 4c — familiarity: COMPLETE, with sentiment BLOCKED

Commit `a94b437`.

**Integrated:** `FamiliarityStage` only, from days known and message count,
taking the lower read.

**Mapping — restrains, never grants:**

| stage | vote |
|---|---|
| `New` | `verbosity=short`, `teasing=off` |
| `Acquainted` / `Familiar` / `Close` | **nothing** |

A long interaction history is evidence that conversations happened, not that
affection, trust, consent, flirtation permission, or ownership exist. Five years
and nine thousand messages unlocks exactly as much as day one: nothing. A stage
that grants nothing cannot accumulate permissions, which also answers the
inspection's concern that familiarity only ever moves forward.

`short` rather than `cool` deliberately: brevity with someone barely met is
caution; a coldness vote would be a claim about the relationship. `teasing=off`
matches the canonical default — voting it explicitly makes the restraint
auditable rather than accidental.

**BLOCKED: all EmotionalSignal sentiment.** `AverageValence`, `RecentMood`,
`Trend`, `RecentEmotion`, `RecentTopic` and every derived user-emotion claim
remain excluded from register voting until expiry, confidence, correction and
privacy semantics exist for them. Phase 0 gave that store privacy and a
lifecycle; it did not give it confidence or correction, so the exclusion stands.

**Live** through the real call site.

---

## Findings surfaced by the work

1. **The native row's register line reported six of nine dimensions**, silently
   omitting `intensity`, `teasing` and `skepticism` — so a shadow row could
   contain a decision it could not report. Source 4b is the first source to vote
   one of the missing three, which is how it surfaced. Fixed; all nine now.
2. **Her spirits start at the profile default of 0.2, not zero.** My own
   arithmetic expectation was wrong before the code was; the test now states the
   real starting point.
3. **4c outranks 4a on verbosity** (§5.4 relationship > working-context), so in
   a brand-new conversation the turn's read of the moment loses to the state of
   the relationship. This broke 4a's live assertion, which had assumed it would
   win. The contract is correct, so the assertion changed rather than the
   precedence: it now requires the vote to have been *adjudicated* — winner or
   recorded loser — which is the honest claim about a vote reaching the
   assembler.

## Boundaries verified across all three sources

- None of them can create an item, an expression restriction,
  `must_not_express`, a mandatory claim, tool authorization, epistemic
  authority, or a user preference. Zero item grants each; forged attempts are
  recorded violations, tested per source.
- None can propose a restrictive register value.
- Mixed dimensions resolve independently — warm + blunt + short, from three
  authorities at once, no cross-talk.
- Every diagnostic carries source, dimension, winner, loser and reason code, and
  **no prose**: asserted by serialization in each source's suite.
- Sexual subject matter, profanity, flirtation, annoyance and emotional
  intensity create no restriction and no consent assumption anywhere in this
  phase — none of these sources can produce a restriction at all.

## Remaining blockers

- **4b energy** — no resolvable provenance; votes nothing.
- **4c sentiment** — no expiry, confidence, or correction; excluded entirely.
- **Mood and relationship state remain per-user, not per-conversation.** Two
  simultaneous conversations with one user share both. Defensible (she is one
  person) but stated rather than discovered later.
- Source 5 (world / vision / embodiment) untouched. Sources 2 and 3 blockers
  stand as recorded.
