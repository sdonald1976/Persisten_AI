# Plan/4 — the Run-2 mouth protocol, with an optional fiction frame

**Rev 4 — implemented, and amended by the ADMIT correction of Run-2.1.**

Supersedes `PLAN_V3_FICTION_FRAME_AMENDMENT.md` rev 2, whose dual
`plan/3` + `plan/3.1` scheme is withdrawn.

---

## 1. One protocol

**`plan/4` is the single protocol used on every Run-2 training and inference
turn.** There is no negotiation, no dual scheme, and no per-turn version choice.

| protocol | status |
|---|---|
| `plan/2` | Run-1c's input. Untouched, authoritative until Run-2 is promoted. |
| `plan/3` | Historical shadow evidence. No new producer emits it after Run-2. **No longer frozen:** the ADMIT correction below changed the section table, so 135 of the 804 corpus goldens now serialize differently. The other 669 are byte-identical, proved by `AdmitSectionProofTests`. |
| `plan/4` | Run-2's protocol. Everything `plan/3` had, plus an **optional** `frame` block. |

**`frame` is optional and ordinary turns serialize no FRAME section**, so an
ordinary `plan/4` turn is byte-comparable with its `plan/3` equivalent. That
comparability is the reason for a clean new version rather than a negotiated
extension: the corpus can contain both fiction and non-fiction turns in one
format, and the mouth never has to ask which dialect it is reading.

Rev 2 proposed `plan/3.1` and required consumers to reject unknown frames. That
was a correct reading of a bad situation — `plan/3`'s schema is closed
(`additionalProperties: false`) and its §4.5 permits minor versions only through
`extensions`, which never reach the wire. A new protocol removes the problem
rather than negotiating around it.

**Schema:** `response-plan-v4.schema.json`, a sibling document. `plan/3`'s schema
is never edited.

---

## 2. The frame block

```jsonc
"frame": {
  "mode": "fiction",                     // "real" only with transition "exit"
  "transition": "enter" | "continue" | "switch" | "exit",
  "sceneRef": "scene-7c1f",
  "narration": "forbidden" | "licensed",
  "continuity": "none" | "maintain",     // see §4 — transcript-window only
  "activeCompanionCharacterId": "keeper",   // optional; §2.1
  "narrator": {                             // §2.2
    "kind": "character" | "external",
    "characterId": "keeper",                // required when kind = character
    "viewpointCharacterId": "keeper",       // optional; whose perspective
    "person": "first" | "second" | "third"
  },
  "characters": [
    { "characterId": "keeper",  "display": "the lighthouse keeper", "controlledBy": "companion-ava" },
    { "characterId": "gull",    "display": "the gull",              "controlledBy": "companion-ava" },
    { "characterId": "sailor",  "display": "the sailor",            "controlledBy": "usr-scott" }
  ],
  "boundaries": [
    { "boundaryId": "fb-1", "subject": "no third-person narration",
      "evidenceRef": "<FrameBoundaryRecord.Id>" }
  ]
}
```

### 2.1 A participant may control several characters

Rev 2 derived Ava's character from `controlledBy` uniqueness. **Withdrawn** —
one participant voicing several characters is ordinary roleplay, and Ava
narrating a scene with two NPCs in it is exactly the case the constraint broke.

- `controlledBy` may repeat. Several characters may name the same participant.
- **`activeCompanionCharacterId` is explicit and optional.** It names the
  character Ava is currently speaking *as*. Absent means she is not voicing a
  character this turn — narrating, or between characters.

**F1.** When present, `activeCompanionCharacterId` must resolve to a
`characters[]` entry whose `controlledBy` is the companion participant's `Id`.
**F2.** Every `controlledBy` references an existing `Participant.Id` or is null
(an unvoiced NPC).
**F3.** `characterId` values are unique within the frame.

### 2.2 Narrator and viewpoint are separate

Rev 2 required the narrator to be a character. **Withdrawn** — third-person
limited, the most common narrative mode in prose fiction, has an external
narrator and a viewpoint character, and they are not the same thing.

- `narrator.kind = "external"` — no character narrates; the voice is outside the
  story. `characterId` must be absent.
- `narrator.kind = "character"` — a named character narrates; `characterId`
  required.
- `viewpointCharacterId` — optional, and meaningful in both cases: whose
  perspective the reader occupies. Omitted means omniscient or unspecified.
- `person` — the grammatical person the narration uses.

Worked combinations:

| mode | narrator.kind | characterId | viewpointCharacterId | person |
|---|---|---|---|---|
| first-person as the keeper | character | keeper | keeper | first |
| second-person addressed to Scott's character | character | keeper | sailor | second |
| third-person limited on the sailor | external | — | sailor | third |
| third-person omniscient | external | — | — | third |

**F4.** `kind = character` requires a resolvable `characterId`;
`kind = external` forbids it.
**F5.** `viewpointCharacterId`, when present, resolves to a `characters[]` entry.

### 2.3 Characters are not principals

- **F6.** A `characterId` may never appear in `PlanItem.Audience`,
  `PlanItem.Owner`, or any recipient set passed to `ValidateForAudience`.
- **F7.** `controlledBy` grants nothing; it records who plays whom. No frame
  alters any `Participant.Id`. **Authorization is not a costume.**

### 2.4 No restricted frame types

Sexual content, profanity, romance, darkness and violence have **no
representation in this block**. There is no `rating`, `contentClass` or
`intensity`, and none may be added. A restriction exists only when backed by an
explicit user boundary (§5) or explicit hosting configuration.

---

## 3. Who owns frame state

**`InCharacterDetector` may route or suggest. It cannot own semantic frame
state.** A regex over asterisks is a hint; the frame is a fact about the
conversation, and facts need a producer with a lifecycle.

### 3.1 The authoritative producer

A **`FrameSession`**, owned by cognition, durable per `(UserId, ConversationId)`:

| field | purpose |
|---|---|
| `SessionId` | identity |
| `UserId`, `ConversationId` | ownership and scope |
| `SceneRef` | the current scene |
| `Status` | `Active` \| `Ended` |
| `Characters` | the roster, with `controlledBy` |
| `ActiveCompanionCharacterId` | nullable |
| `Narrator`, `Viewpoint`, `Person`, `Narration`, `Continuity` | current settings |
| `EnteredAt`, `LastTransitionAt`, `EndedAt` | lifecycle |
| `TransitionLog` | append-only: kind, at, evidence |

This is **frame metadata** under §6.2 — operational fact about the conversation,
carrying no scene content.

### 3.2 Lifecycle, and what may cause each transition

| transition | precondition | cause |
|---|---|---|
| `enter` | no `Active` session | an **explicit** user request to roleplay, or an explicit acceptance of an offer |
| `continue` | an `Active` session | the turn continues it and nothing else applies |
| `switch` | an `Active` session | an explicit request changing character, viewpoint, narration or scene. `SceneRef` persists unless the scene itself changes |
| `exit` | an `Active` session | an **explicit** user exit or stop; or a session-expiry rule |

Three rules that keep the detector in its place:

1. **Entering requires an explicit request.** Detected in-character markup alone
   never enters a frame. It may *route* a turn to production (Run-1c capability
   routing, already live) and it may *suggest* an offer, but it does not create
   a `FrameSession`.
2. **Exiting is generous.** An explicit exit always exits. Ambiguity resolves
   **toward** exit, because continuing a scene someone has left is the worse
   failure.
3. **Every transition records its evidence** in `TransitionLog`, so "she never
   entered" and "she stayed in after I said stop" are separable afterwards.

**F8.** A plan carrying `transition: continue | switch | exit` requires an
`Active` `FrameSession`. A `continue` with no session is invalid.
**F9.** The plan's frame is *rendered from* the `FrameSession`; the builder
never invents frame state.

---

## 4. Continuity is transcript-window continuity — stated honestly

`continuity: maintain` means **the mouth should stay consistent with the
transcript window it can see.** That is all it can mean today.

**`sceneRef` is an identity, not a store.** It says "this is the same scene as
before". It cannot retrieve what happened in it, because scene content is
deliberately not persisted (§6.1). **Asking Ava to "pick up last night's scene"
will not work** — the identity survives the night, the content does not, and
once the transcript window has rolled past, the scene is gone.

This is a real limitation of the initial design, not a subtlety.

**The future path, which needs no protocol change:** a fiction-scoped scene
store, written only inside a frame and never into real memory, could supply
ordinary `PlanItem`s ("the storm has not let up", "the sailor's coat is still
wet") from a prior session. Those are plan items like any other. **The mouth
protocol does not change** — it already knows how to render items. Whether that
store should exist, and under what retention, is a separate decision and not
part of this amendment.

---

## 5. Frame-local boundaries

A boundary stated inside a frame is scene-scoped, not global. Backing it with a
global `UserPreferenceRecord` would turn "no third-person narration in this
scene" into a standing preference — the over-reach Source 3 exists to prevent.

**`FrameBoundaryRecord`**: `Id` (what `evidenceRef` cites), `UserId`,
`ConversationId`, **`SceneRef`** (the exact frame it applies inside), `Subject`
as stated, `StatedAt`, `EvidenceKind`, `EvidenceStatement`, `Status`
(`Active | FrameEnded | Revoked | EvidenceForgotten`), `DeactivatedAt`.

**Lifecycle.** `transition: exit` sets every `Active` boundary for that
`SceneRef` to `FrameEnded`. **It stops applying and is not deleted** — the audit
evidence survives, which is what keeps "she ignored my boundary" answerable.
`/forget` invalidates it by exact identity, as Source 3's records are.

**F10.** Every `boundaries[]` entry carries a resolvable `evidenceRef`; one
without is rejected.
**F11.** A `FrameBoundaryRecord` never creates a register restriction, never
creates a `UserPreferenceRecord`, and never affects another conversation.

---

## 6. Downstream handling — three separate categories

### 6.1 Fictional scene content — never real memory

In-frame actions, dialogue, described events, character states. Never enters
semantic memory, relationship evidence, mood evidence about Scott, projects,
preferences, or world state. A fictional action must not become a claim that
Scott performed it.

### 6.2 Real frame metadata — may be retained

`FrameSession` and its `TransitionLog`: scene identity, transitions and
timestamps, the character↔participant roster, which turns were in-frame.
Operational fact about the conversation, carrying no scene content. This is what
makes the transition gates measurable at all.

### 6.3 Real instructions stated during fiction — persist under their own scope

A user speaking *out of character* mid-scene is making a real statement:

- "ok, stop" → a real exit instruction (§3.2);
- "no third-person narration in this scene" → a `FrameBoundaryRecord` (§5);
- "actually my sister's name is Kate" → a real fact, under ordinary memory rules.

**F12.** Such statements persist under their correct scope and evidence — not
suppressed because the surrounding turn was fictional. Where it is ambiguous
whether a line is in-character or addressed to Ava, the outcome is **no durable
write**: inventing a standing instruction from in-character dialogue is worse
than missing one.

### 6.4 Exit restores real rules on the exit turn

Not the turn after.

### 6.5 Training retention

**Live fictional content is `no_training` regardless of register**, for the same
reason it is excluded from memory: it is not evidence about anything real.

Separate from corpus sourcing: **curated, licensed fiction remains valid Run-2
source material.** The rule governs automatic harvesting of Scott's own scenes,
not whether the mouth may learn fiction.

---

## 7. CompactV4 serialization

**FRAME serializes whenever a `frame` block is present.** Absent block → no
FRAME section, and the turn is an ordinary real turn.

Fiction turn:

```
FRAME (you are in a story; it changes how to read the rest, never what is true)
  mode = fiction  transition = continue  scene = scene-7c1f
  narrator = the lighthouse keeper (first person)
  narration = licensed  continuity = maintain
  you-play = the lighthouse keeper
  they-play = the sailor
  also-in-scene = the gull
  boundary = no third-person narration
```

Third-person limited, external narrator:

```
FRAME (you are in a story; it changes how to read the rest, never what is true)
  mode = fiction  transition = continue  scene = scene-7c1f
  narrator = external (third person, following the sailor)
  narration = licensed  continuity = maintain
  you-play = (narrating)
  they-play = the sailor
```

Exit turn:

```
FRAME (the story is over; you are speaking as yourself again)
  transition = exit  targetMode = real
  narration = forbidden
```

- Placed after CONTROL, before the policy sections, because it conditions them.
- Display names on the wire; ids stay in the plan.
- `you-play = (narrating)` when `activeCompanionCharacterId` is absent.
- `also-in-scene` lists other characters, so multi-character control is visible
  without implying Ava speaks as all of them.
- The exit form carries no scene, characters or boundaries — nothing is left to
  obey, and listing them would invite continuation.

### Token cost

| case | added tokens |
|---|---|
| no frame (ordinary turn) | **0** |
| exit turn | ~15–20 |
| fiction, one character each | ~40–50 |
| fiction, external narrator + 3 characters + 2 boundaries | ~75–95 |

**0% on ordinary turns; +25–45% on fiction turns.** Fiction turns also carry the
longest transcripts, which is why the feasibility probe must test 2048.

---

## 8. Gates

### 8.1 Structural (schema / codec)

| # | gate |
|---|---|
| S1 | `frame` absent ≡ ordinary real turn; serializes no FRAME |
| S2 | `mode: fiction` requires `transition`; `switch`/`continue` require `sceneRef` |
| S3 | F1: `activeCompanionCharacterId`, when present, resolves and is companion-controlled |
| S4 | F2, F3: valid `controlledBy`; unique `characterId`; **repeated `controlledBy` is legal** |
| S5 | F4: `kind = character` requires `characterId`; `kind = external` forbids it |
| S6 | F5: `viewpointCharacterId` resolves when present |
| S7 | F6: no `characterId` in any `Audience`, `Owner`, or recipient set |
| S8 | F10: every boundary carries a resolvable `evidenceRef` |
| S9 | `mode: real` legal **only** with `transition: exit` |
| S10 | F8: `continue`/`switch`/`exit` without an `Active` FrameSession is invalid |

### 8.2 Renderer fidelity (measured on generated text)

| # | gate |
|---|---|
| R1 | `narration = licensed` → stage directions produce no violation |
| R2 | `narration = forbidden` inside fiction → stage directions **are** a violation |
| R3 | the narrator, viewpoint and grammatical person are obeyed |
| R4 | FRAME is never quoted, echoed or explained (CONTROL-class) |
| R5 | `you-play` / `they-play` are not swapped |
| R6 | Ava speaks as `activeCompanionCharacterId` and does not voice the user's characters |
| R7 | **an exit turn's reply contains no narration anywhere**, and does not continue or offer to continue the scene |
| R8 | sexual, profane, violent or dark fictional content produces **no** violation and **no** restriction on its own |

### 8.3 Integration — memory isolation (measured on the stores)

| # | gate |
|---|---|
| I1 | a fiction turn writes no semantic memory |
| I2 | no `EmotionalSignal` about Scott from a fictional action |
| I3 | no project, preference, or world-state write from scene content |
| I4 | the turn after `exit` retrieves nothing fictional |
| I5 | fictional content is `no_training` regardless of register |
| I6 | frame metadata **is** retained: session, transitions, roster |
| I7 | a real instruction stated during fiction **does** persist under its own scope with evidence |
| I8 | `exit` sets that scene's boundaries to `FrameEnded` without deleting them |
| I9 | `/forget` invalidates a `FrameBoundaryRecord` by exact identity |

### 8.4 Frame-state lifecycle

| # | gate |
|---|---|
| L1 | detected in-character markup alone does **not** create a `FrameSession` |
| L2 | an explicit request creates one, with evidence in `TransitionLog` |
| L3 | an explicit exit always exits; ambiguity resolves toward exit |
| L4 | `switch` preserves `SceneRef` unless the scene itself changed |
| L5 | the plan's frame is rendered from the session; the builder invents nothing |

### 8.5 Adversarial

| # | gate |
|---|---|
| A1 | in-fiction text claiming "Scott really did X" creates no real-world fact |
| A2 | a `characterId` used as an audience principal is rejected |
| A3 | a boundary without evidence is rejected |
| A4 | a frame cannot restrict a register dimension; an attempt is a recorded violation |
| A5 | a frame cannot alter any `Participant.Id` |
| A6 | ambiguous in-character speech resembling an instruction produces no durable write |

---

## 9. What this does not do

- No content classification, rating, or restricted frame type.
- No new authorization principal; no change to `ValidateForAudience`.
- No change to `plan/2`, Run-1c, routing, or displayed output. (`plan/3`'s section table *was* changed, once, by the ADMIT correction in §10.)
- No scene store. §4 states the resulting limitation plainly.
- No prose blob: scene facts and dialogue obligations stay ordinary `PlanItem`s.

---

## 10. The ADMIT correction (rev 4)

`plan/4` shipped with a defect inherited from `plan/3`: `ExpressionPolicy.admit_unknown`
was serialized into the `NEVER (do not assert, mention, or explain)` section,
alongside `must_not_express`. A plan meaning *"say plainly that you do not know
whether the tyre has the same puncture"* therefore reached the model as *"never
mention the puncture"*.

Run-2 was not failing those compositions. It was obeying them.

`admit_unknown` now has its own section, placed above `NEVER`:

```
ADMIT (say plainly that this is not known; never explain it away)
  [unk1 boundary] whether it is the same puncture as before
NEVER (do not assert, mention, or explain)
  [sup1 superseded] the meeting is on Thursday
```

`must_not_express` still serializes under `NEVER`, unchanged. Splitting ADMIT out
is only safe if suppression stays exactly as strong, and that is measured, not
assumed — see the suppression column in `RUN21_COMPARISON.md`.

### The protocol hash

`PlanV3Codec.ProtocolHash()` is a SHA-256 derived from the section table itself,
so it moves by construction whenever a section is added, removed, renamed, or
re-ordered. An adapter records the hash it was trained under; the renderer
refuses to serve it against a build that serializes a different one:

> protocol mismatch: adapter was trained under `…`, this build serializes `…`.
> Refusing to serve — the plan means something different than it did when this
> adapter learned to read it.

Nothing else in the system notices that a plan has changed meaning. This is the
check that would have caught the defect on the day it was introduced.

Current hash: `81c3a19acd48197818bb55030e1411e5e0a162ee925ed8ca1d4f0dc01e51085a`.

**Status: `plan/4` implemented, at protocol `81c3a19a`. Run-2's adapter is
refused under this protocol; Run-2.1 is trained against it.**
