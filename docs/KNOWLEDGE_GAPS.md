# Knowledge gaps: what Ava knows she doesn't know

_Phase 4 design (language-organ plan). Recorded 2026-08-20, before any implementation._

Phase 3 created the first system-owned epistemic event: `ConceptLookup("quokka") →
Unknown`, recorded with provenance. Phase 4 gives such events somewhere to live — a typed
**KnowledgeGap** — and a governed path into the curiosity subsystem that already exists.
The rule this design serves: **curiosity is system-owned motivation with provenance,
never personality prompt text, and never something the chat model can mint because it
finds a topic interesting.** There is no code path from model output to a gap.

## 0. What the existing Curiosity subsystem already provides (inspected first)

The persisted `Curiosity` is a complete asking machine, and nothing here duplicates it:

- **Entity**: `Question`, `About` (dedupe key), `Reason` (explainability, never shown
  verbatim), `ReflectionId` (provenance), lifecycle `Open → Voiced | Dismissed |
  Satisfied`.
- **Budget, enforced in code**: `MarkVoicedAsync` fires the moment a question is
  *injected* — asked once is the whole budget, answered or not.
- **Cooldown**: `GetNextToVoiceAsync` returns nothing within `CuriosityCooldownHours`
  (1h) of any voicing — a spark, never an interrogation.
- **Caps and hygiene**: at most `ReflectionMaxCuriosities` (2) minted per reflection
  pass; SleepCycle dismisses curiosities older than 14 days; reflection closes ones the
  conversation answered.
- **Four surfacing channels**, all with restraint rules: in-reply ("ask it only if it
  fits… if it doesn't, let it go"), greeting opener, outreach push (no curiosity, no
  message), and "what's on your mind?".
- **It drives her body too**: open curiosities are the strongest roaming preoccupation.

What it does NOT have: a typed representation of the underlying epistemic state (a
curiosity is a *question*, not the *gap* that warranted it), any producer other than the
LLM reflection pass, scoring beyond newest-first, and any notion of pursuit routes
(ask vs research). Those are Phase 4's additions.

## 1. The smallest typed representation of a gap

```
KnowledgeGap
  Id, UserId
  Kind             : GapKind
  Subject          : string      — the language handle ("quokka"; the reference text)
  SubjectConceptId : Guid?       — typed link when the gap is about a concept
  Source           : GapSource   — which SYSTEM observed it
  SourceRef        : Guid?       — provenance: the TurnRecord traceId, memory/assertion ids
  Occurrences      : int         — recurrence is salience
  FirstSeen, LastSeen : DateTimeOffset
  Status           : GapStatus
  Pursuit          : GapPursuit  — how it would be pursued, if pursued
  CuriosityId      : Guid?       — link once promoted (one gap → at most one curiosity, ever)
  ResolutionNote   : string?     — how it closed ("learned from teaching <assertion>")
```

```
GapKind    { UnknownConcept, UncertainKnowledge, ConflictingEvidence, UnresolvedReference }
GapSource  { KnowledgeLookup, WorkingContext, MemoryReview }
GapStatus  { Open, Pursuing, Satisfied, Declined, Expired }
GapPursuit { AskUser, Research, Observe, Defer }
```

All enums typed with kebab labels (strings represent language — `Subject` is language;
everything decisional is a type).

**The six distinctions, mapped.** Four are `GapKind`s. The other two are deliberately
NOT kinds, because they are dispositions of the lifecycle:

| distinction | representation |
|---|---|
| I don't know this | `UnknownConcept`, Open |
| I am uncertain about this | `UncertainKnowledge`, Open |
| I have conflicting evidence | `ConflictingEvidence`, Open |
| I don't know what this reference means | `UnresolvedReference`, Open |
| I know this; nothing to ask | **no gap** — `ConceptFamiliarity.Known` mints nothing |
| I don't know this, but it isn't worth pursuing | a gap that scores below the pursuit floor → `Declined` — recorded, not forgotten, not asked |

## 2. Sources (v1) — observable system state with provenance, nothing else

| source event | gap minted | provenance |
|---|---|---|
| `knowledge.lookup → unknown` (Phase 3) | `UnknownConcept` / `KnowledgeLookup` | the turn's TraceId |
| `knowledge.lookup → learning` or a `Disputed` familiarity | `UncertainKnowledge` / `KnowledgeLookup` | TraceId + assertion id |
| Memory `NeedsReview` parking / `Disputed` status (the supersession review path) | `ConflictingEvidence` / `MemoryReview` | the two memory ids |
| Working context: marker detected, referent null — or `withheld-guess` on a turn where clarify did NOT fire | `UnresolvedReference` / `WorkingContext` | TraceId |

Explicitly excluded from v1, recorded for later evidence-gated addition: every noun the
user mentions (concept-encounter gaps — too noisy without a corpus), model-proposed
gaps (never — no path exists), perception-driven gaps (Phase 4 of the world, not this).

## 3. Deduplication and lifecycle

Dedupe key: `(Kind, normalized Subject)` — the same normalization `ConceptKnowledge.
Canonical` already uses. Re-observation of an existing gap increments `Occurrences`,
updates `LastSeen`, and never creates a row — recurrence IS the salience signal, the
same move `AttentionItem.Strength` makes.

Lifecycle: `Open` → (`promoted`) `Pursuing` → `Satisfied` when the answer arrives; or
`Declined` when scoring says not worth pursuing; or `Expired` by the SleepCycle sweep
(30 days stale, the diagnostics retention). A gap whose curiosity was voiced but never
answered stays `Pursuing` until expiry — **it never re-mints**: the ask-once budget is
inherited transitively (one gap → at most one curiosity → voiced at most once).

## 4. Promotion into the EXISTING curiosity system

A `GapPromoter` runs inside the existing reflection cadence (SleepCycle, off the
request path), immediately after the reflection pass:

1. Select the top-scored `Open` gaps (v1: `UnknownConcept` only — see §5's honesty note).
2. Mint at most **one** gap-sourced `Curiosity` per pass (`MaxGapCuriositiesPerPass=1`),
   under the existing per-pass cap — reflection-born curiosities are not crowded out.
3. The question text is a deterministic template per kind, from `Prompts` keys
   (typed gap in, language out): *"What's a {subject}? You mentioned it once and I
   realized I've never learned what it is."* `About` = subject (the existing dedupe
   key does the rest); `Reason` = the gap's provenance, human-readable;
   `ReflectionId` = the pass that promoted it (reusing existing provenance), plus a
   new nullable `Curiosity.GapId` linking back (one small migration on Curiosity).
4. The gap moves to `Pursuing` with `CuriosityId` set.

From that point the EXISTING machinery owns everything: cooldown, ask-once-at-
injection, the four channels, the restraint prompt rules, expiry, roaming. No second
asking system exists.

## 5. Scoring: whether a gap deserves a question

Deterministic, from observable features only: `Occurrences` (recurrence), recency of
`LastSeen`, Kind weight (an explicit "do you know X?" the user asked is near-certain
worth; an unresolved pronoun from days ago is near-certain NOT worth asking about
later), and the existing global costs (cooldown, budget, caps) which are preserved
unchanged. Below the floor for `N` days → `Declined`, which is itself an epistemic
statement Ava can stand behind.

**Honesty note (the v1 restriction):** only `UnknownConcept` gaps promote in v1.
`UnresolvedReference` gaps age badly ("who did you mean by 'her' on Tuesday?" is a
worse conversation than letting it go) and `ConflictingEvidence` questions need careful
wording to not feel like interrogation — both kinds are RECORDED and SCORED from day
one, and their promotion is a capture-informed later decision, not an intuition today.

## 6. Satisfaction

When `ConceptKnowledge.LearnFromAsync` learns a concept, the pipeline closes any
matching `UnknownConcept`/`UncertainKnowledge` gap: `Satisfied`, `ResolutionNote`
citing the new assertion — and, transitively, its linked curiosity → `Satisfied`. That
completes the loop the design exists for: *unknown → recorded → wondered → asked →
taught → known → satisfied*, every arrow a row with provenance. (Reference-gap
satisfaction via later resolution, and conflict-gap satisfaction via review resolution,
are wired when those kinds promote — not before.)

## 7. Ask-Scott versus research/tools

`GapPursuit` is typed now, one arm live: v1 rules set `AskUser` for everything
promotable, `Defer` otherwise. `Research` is reserved for the future trusted-knowledge
tool (its output would enter the store as `KnowledgeOrigin.ToolVerified` — the enum
value already exists and nothing writes it), `Observe` for the world link. The route is
a system decision recorded on the gap — when autonomous research eventually exists, the
question "may Ava research this herself?" is answered by policy on a typed field, not
by the model's mood.

## 8. Safeguards against the question machine

All existing restraints preserved unchanged: ask-once-at-injection, 1h cooldown, 2 per
reflection pass, 14-day curiosity expiry, "only if it fits" prompt rules, outreach
gates. New, gap-side: one gap-sourced curiosity per pass; one curiosity per gap EVER;
promotion floor with `Declined` as the recorded outcome; kinds that age badly don't
promote at all in v1; and gaps never inject questions into a live turn directly — only
through the existing channels. The five-irrelevant-details scenario from the original
proposal becomes testable: five unknowns in one conversation yield five Open gaps and
at most one question, hours later, about the best of them.

## 9. Diagnostics and capture

- Decision stages: `gap.observed={kind}:{subject}`, `gap.satisfied={subject}` — flowing
  into the ring and the durable TurnRecord automatically.
- Promoter records: a `gap.promotion` capture row per considered gap (promoted or
  suppressed, with the score) — the corpus for judging the floor and the kind weights.
- `GET /gaps` (open + recent closed, with provenance) beside `/curiosities`.
- The measurement set the design owes: created / deduped / suppressed / promoted /
  voiced / satisfied / declined / expired — every count derivable from the gap table,
  the curiosity linkage, and the capture rows.

## 10. The smallest live experiment

1. Ask *"Do you know what a quokka is?"* → reply says not-learned (Phase 3);
   `gap.observed=unknown-concept:quokka` recorded with the turn's TraceId.
2. Trigger the reflection pass (`POST /reflect`) → the promoter mints the curiosity;
   `/curiosities` shows *"What's a quokka? …"* with gap provenance; gap → `Pursuing`.
3. Next greeting or fitting turn surfaces it through the existing channel (cooldown
   respected); voicing marks it Voiced — asked once.
4. Teach *"A quokka is a small wallaby native to Western Australia."* → concept learned
   (Phase 3), gap → `Satisfied` with a resolution note citing the assertion, curiosity
   → `Satisfied`.
5. Negative control: five unknown, irrelevant terms in one conversation → five Open
   gaps, at most one question ever asked, the rest `Declined` or `Expired` on schedule.

A permanent soak stage (`gaps`) automates 1–4 against the live model, faulting on
system decisions and noting model behavior — the same discipline as `knowledge` and
`context`.

## Explicitly not building

Autonomous web research; the Developmental Mind; concept-relation expansion or book
ingestion; model-minted gaps (structurally impossible, kept that way); promotion of
`UnresolvedReference`/`ConflictingEvidence` kinds (recorded, scored, not asked);
perception-sourced gaps; any change to the curiosity cooldown or ask-once budget —
the evidence says they work.

## Status

- **2026-08-20** — design recorded; awaiting approval before implementation.
