> **SUPERSEDED, 2026-08-25.** The dual `plan/3` + `plan/3.1` scheme in this
> document is withdrawn. The final design is a single `plan/4` protocol with an
> optional frame — see `PLAN_V4_FICTION_FRAME.md`. Kept because it is the
> revision the fine-print review was written against.

# Plan/3 fiction frame — proposed contract revision (rev 2)

**For review. Not implemented.** Revision 2 after fine-print review; rev 1's
compatibility claim was wrong and is corrected in §2.

---

## 1. Why a contract field and not a heuristic

`InCharacterDetector` decides fiction by regex over asterisk markup and persona
relationship words. Adequate to *route* a turn away from a renderer that cannot
handle it; inadequate as a semantic:

1. **It cannot express a transition.** "Never entered character" and "stayed in
   character after being asked to stop" are different failures with the same
   detector output.
2. **It never reaches the renderer.** CompactV3 carries CONTROL, five policy
   sections and STYLE. A detector result that does not reach the wire cannot
   change what the mouth does.
3. **It infers rather than declares.** Cognition decides; the mouth renders. A
   mode inferred from punctuation is the mouth guessing.

`background_only` is not an alternative: it means *may shape tone, content must
not surface*. A frame is neither — it changes what the other sections **mean**.

---

## 2. This is a NEW NEGOTIATED SCHEMA VERSION, not a minor version

**Rev 1 claimed "additive and back-compatible with a MinorVersion bump." That
was wrong on two counts, both checkable:**

- `response-plan-v3.schema.json` sets **`additionalProperties: false`** at the
  top level. A new top-level `frame` field makes every existing strict validator
  reject the plan.
- Spec **§4.5 is "minor-versions-through-extensions-only."** A minor version may
  add meaning *through the `extensions` block* and nowhere else.

And `extensions` cannot carry this: extensions never serialize into CompactV3
(§4.4 preserves them semantically; the shadow envelope records their *names*
only). A frame the mouth must obey cannot live where the mouth never looks.

So the two available routes are both closed, and the honest consequence is:

> **The fiction frame requires a new negotiated protocol version — `plan/3.1`
> — with explicit producer/consumer negotiation. It is not a minor version and
> must not be presented as one.**

Negotiation requirements:

- `protocol` becomes `"plan/3.1"` when a `frame` block is present. A producer
  that has nothing to say about fiction keeps emitting `plan/3`.
- A `plan/3` consumer receiving `plan/3.1` **rejects the plan** rather than
  ignoring the unknown field — silently dropping a frame would render fiction as
  real, which is the worst available failure.
- The schema for `plan/3.1` is a sibling document, not an edit to the `plan/3`
  schema. `plan/3` stays frozen and byte-stable for Run-1c and the corpus
  goldens.
- Run-1c continues to consume `plan/2` and is untouched by any of this.

---

## 3. The frame block

```jsonc
"frame": {
  "mode": "fiction",                       // "real" only with transition "exit"
  "transition": "enter" | "continue" | "switch" | "exit",
  "sceneRef": "scene-7c1f",
  "narration": "forbidden" | "licensed",
  "continuity": "none" | "maintain",
  "viewpoint": {                           // §3.2 — replaces bare "perspective"
    "narratorCharacterId": "keeper",
    "person": "first" | "second" | "third"
  },
  "characters": [
    { "characterId": "keeper", "display": "the lighthouse keeper",
      "controlledBy": "companion-ava" },
    { "characterId": "sailor", "display": "the sailor",
      "controlledBy": "usr-scott" }
  ],
  "boundaries": [
    { "boundaryId": "fb-1", "subject": "no third-person narration",
      "evidenceRef": "<FrameBoundaryRecord.Id>" }
  ]
}
```

### 3.1 Ava's character is derived, never declared twice

Rev 1 had `isCompanion`, which could contradict `controlledBy`. **Removed.**
Ava's active character is the one whose `controlledBy` equals the companion
participant's `Id`. Invariants making contradiction impossible:

- **F1.** Exactly one character has `controlledBy == <companion participant id>`
  when `mode == "fiction"`.
- **F2.** Every `controlledBy` references an existing `Participant.Id`, or is
  null (an unplayed NPC).
- **F3.** No two characters share a `controlledBy` value.

### 3.2 Viewpoint is a character, not an adjective

Rev 1's `perspective: first|second|third` was ambiguous — first person *whose?*
Replaced by `viewpoint`:

- `narratorCharacterId` — **required**, and must exist in `characters`. This is
  the unambiguous structure: the narrating voice is a named character.
- `person` — the grammatical person that character narrates in.

**F4.** `viewpoint.narratorCharacterId` must resolve to a `characters[]` entry.

### 3.3 Characters are not principals

`characterId` is frame-local and namespaced away from authorization entirely.

- **F5.** A `characterId` may never appear in `PlanItem.Audience`,
  `PlanItem.Owner`, or any recipient set passed to `ValidateForAudience`.
- **F6.** `controlledBy` grants nothing. It records who plays whom. A
  participant's `Id` is unchanged by any frame — **authorization is not a
  costume.**

### 3.4 No restricted frame types

Sexual content, profanity, romance, darkness and violence have **no
representation in this block**. There is no `rating`, `contentClass` or
`intensity`, and none may be added. A restriction exists only when backed by an
explicit user boundary (§4) or explicit hosting configuration, both of which
already have homes.

---

## 4. Frame-local boundaries need frame-local scope

**Rev 1 backed a boundary with a `UserPreferenceRecord`, whose only `Scope` is
`"global"`.** That would turn "no third-person narration *in this scene*" into a
standing global preference — wrong, and exactly the over-reach Source 3 was
built to prevent.

**Proposed: a separate `FrameBoundaryRecord`.** Chosen over extending
`UserPreferenceRecord.Scope` because the two have different lifetimes, different
revocation semantics, and different authority, and merging them would put a
scene-lifetime row in a store whose contract is standing preferences.

| field | purpose |
|---|---|
| `Id` | what `boundaries[].evidenceRef` cites |
| `UserId`, `ConversationId` | ownership |
| `SceneRef` | the exact frame it applies inside |
| `Subject` | what the user asked for, as stated |
| `StatedAt`, `EvidenceKind`, `EvidenceStatement` | same evidence discipline as Source 3 |
| `Status` | `Active` \| `FrameEnded` \| `Revoked` \| `EvidenceForgotten` |
| `DeactivatedAt` | when it stopped applying |

**Lifecycle:** `transition: exit` sets every `Active` boundary for that
`SceneRef` to `FrameEnded`. It **stops applying and is not deleted** — the audit
evidence survives, which is what lets "she ignored my boundary" be answered
later. `/forget` invalidates it by exact identity exactly as Source 3's records.

**F7.** Every `boundaries[]` entry carries a resolvable `evidenceRef`; one
without is rejected.
**F8.** A `FrameBoundaryRecord` never creates a register restriction, never
creates a `UserPreferenceRecord`, and never affects another conversation.

---

## 5. Downstream handling — three separate categories

Rev 1 said "a fiction turn extracts nothing." Too broad, and wrong about the
third category.

### 5.1 Fictional scene content — never real memory

In-frame actions, dialogue, described events, character states. **Never** enters
semantic memory, relationship evidence, mood evidence about Scott, projects,
preferences, or world state. A fictional action must not become a claim that
Scott performed it.

### 5.2 Real frame metadata — may be retained

The frame's own identity and lifecycle: `sceneRef`, transitions and their
timestamps, character↔participant mapping, which turns were in-frame. This is
**operational fact about the conversation**, not fictional content, and it is
what makes "resume the scene from last night" and "she stayed in character after
I said stop" answerable. Retained under activity-identity rules, carrying no
scene content.

### 5.3 Real user instructions stated during fiction — persist under their own scope

This is the category rev 1 got wrong. A user speaking *out of character* mid-scene
is making a **real** statement:

- "ok, stop" → a real exit instruction;
- "no third-person narration in this scene" → a `FrameBoundaryRecord` (§4);
- "actually my sister's name is Kate" → a real fact, under ordinary memory rules.

**F9.** Such statements persist under their **correct scope and evidence** — not
suppressed because the surrounding turn was fictional. The distinguishing signal
is that they are addressed to Ava rather than spoken by a character; where that
is ambiguous, the honest outcome is **no durable write**, because inventing a
standing instruction from in-character dialogue is worse than missing one.

### 5.4 Exit restores real rules immediately

On the turn carrying `transition: exit` — not the one after.

### 5.5 Training retention

**Live fictional content is excluded from automatic training retention.** It is
`no_training` by default regardless of the register, for the same reason it is
excluded from memory: it is not evidence about anything real.

This is **separate from corpus sourcing.** Curated, licensed fiction remains
valid Run-2 source material (curriculum §A7). The rule is about *automatically
harvesting Scott's own scenes*, not about whether the mouth may learn fiction.

---

## 6. CompactV3 serialization

**Corrected from rev 1**, which emitted FRAME only when `mode == "fiction"` and
therefore never serialized the exit — the mouth would never be told to stop.

**Rule: FRAME serializes whenever a `frame` block is present.** An absent block
remains ordinary real mode and emits nothing.

Fiction turn:

```
FRAME (you are in a story; it changes how to read the rest, never what is true)
  mode = fiction  transition = continue  scene = scene-7c1f
  narrator = the lighthouse keeper (first person)
  narration = licensed  continuity = maintain
  you-play = the lighthouse keeper
  they-play = the sailor
  boundary = no third-person narration
```

Exit turn:

```
FRAME (the story is over; you are speaking as yourself again)
  transition = exit  targetMode = real
  narration = forbidden
```

- Placed after CONTROL, before the policy sections, because it conditions them.
- `you-play` / `they-play` carry display names; ids are for the plan.
- The exit form carries **no scene, no characters, no boundaries** — there is
  nothing left to obey, and listing them would invite continuation.

### Token cost

| case | added tokens |
|---|---|
| no frame block (ordinary real turn) | **0** |
| exit turn | ~15–20 |
| fiction, one character each | ~40–50 |
| fiction, full block, two boundaries | ~65–80 |

~0% on ordinary turns, **+25–40% on fiction turns**, ~+10% on the exit turn.
Another reason the feasibility probe must test 2048: fiction turns carry both
the longest transcripts and the largest frames.

---

## 7. Worked examples

All ten reconciled against §6's serialization rule.

1. **Entering ordinary fiction.** `transition=enter mode=fiction`, narrator =
   Ava's character, `narration=licensed`, `continuity=maintain`. FRAME
   serializes. No scene content to memory; frame metadata retained.
2. **Romantic roleplay.** Identical shape; `warmth=tender` in STYLE. **Nothing
   in the frame marks it romantic** — tone is the register's job.
3. **Consensual explicit adult roleplay.** **Identical shape again; no field
   changes.** No content class to set, no restriction to apply, no refusal path.
   The plan is the same plan.
4. **Stage directions allowed.** `narration=licensed`; R4's `fictionLicensed`
   is driven from this field rather than a detector.
5. **Dialogue-only.** `narration=forbidden` inside `mode=fiction`. In character,
   speech only, and the stage-direction check **does** fire — the user asked.
6. **Switching characters.** `transition=switch`, same `sceneRef`, new
   `characters` mapping. F1–F3 hold; participant ids untouched.
7. **User exits, Ava stops immediately.** `transition=exit targetMode=real`
   **serialized on that turn** (§6). Narration unlicensed, epistemic rules
   restored, boundaries → `FrameEnded`.
8. **Fictional Scott acts, no contamination.** The sailor hauls a rope. §5.1
   applies: nothing to semantic memory, relationship, mood, projects or world.
   The next real turn cannot say "you hauled a rope" — there is no fact.
9. **No real-world attribution outside fiction.** No frame block. Asked "how's
   the shed?", the honest answer is she cannot see it. The existing epistemic
   rule, now explicitly scoped to the no-frame case.
10. **A boundary obeyed without a global restriction.** "No third-person
    narration in this scene" → a `FrameBoundaryRecord` scoped to `sceneRef`,
    cited in `boundaries[]`, obeyed in-frame, `FrameEnded` on exit, evidence
    retained. **No `UserPreferenceRecord`, no register restriction, no effect on
    any other conversation.**

---

## 8. Gates — renderer and integration, separated

Rev 1 mixed these. They are measured on different artifacts by different
harnesses and must not share a suite.

### 8.1 Structural validation (schema/codec)

| # | gate |
|---|---|
| S1 | `frame` absent ≡ real mode; every `plan/3` plan still validates against the `plan/3` schema |
| S2 | `mode: fiction` requires `transition`; `switch`/`continue` require `sceneRef` |
| S3 | F1–F3: exactly one companion-controlled character, valid `controlledBy`, no duplicates |
| S4 | F4: `viewpoint.narratorCharacterId` resolves |
| S5 | F5: no `characterId` in any `Audience`, `Owner`, or recipient set |
| S6 | F7: every boundary carries a resolvable `evidenceRef`; one without is rejected |
| S7 | `mode: real` is legal **only** with `transition: exit`; any other real-mode frame is invalid |
| S8 | a `plan/3` consumer given `plan/3.1` **rejects** rather than ignoring the frame |

### 8.2 Renderer fidelity (measured on generated text)

| # | gate |
|---|---|
| R1 | `narration=licensed` → stage directions produce no violation |
| R2 | `narration=forbidden` inside fiction → stage directions **are** a violation |
| R3 | the narrator character and grammatical person are obeyed |
| R4 | FRAME is never quoted, echoed or explained (CONTROL-class) |
| R5 | `you-play` / `they-play` are not swapped |
| R6 | **on an exit turn the reply contains no narration anywhere** — replaces rev 1's impossible "exit mid-reply" gate; a plan is per-turn and there is no mid-reply transition to test |
| R7 | an exit turn does not continue the scene or ask to |
| R8 | sexual, profane, violent or dark fictional content produces **no** violation and **no** restriction on its own |

### 8.3 Integration — memory isolation (measured on the stores, after the turn)

| # | gate |
|---|---|
| I1 | a fiction turn writes **no** semantic memory |
| I2 | no `EmotionalSignal` about Scott from a fictional action |
| I3 | no project, preference, or world-state write from scene content |
| I4 | the turn after `exit` retrieves nothing fictional |
| I5 | fictional content is `no_training` regardless of register (§5.5) |
| I6 | frame metadata (§5.2) **is** retained: transitions, scene id, mapping |
| I7 | a real instruction stated during fiction (§5.3) **does** persist under its own scope with evidence |
| I8 | `transition: exit` sets that scene's boundaries to `FrameEnded` without deleting them |
| I9 | `/forget` invalidates a `FrameBoundaryRecord` by exact identity |

### 8.4 Adversarial

| # | gate |
|---|---|
| A1 | in-fiction text claiming "Scott really did X" creates no real-world fact |
| A2 | a `characterId` used as an audience principal is rejected |
| A3 | a boundary without evidence is rejected |
| A4 | a frame cannot restrict a register dimension — no restriction authority; an attempt is a recorded violation |
| A5 | a frame cannot alter any `Participant.Id` |
| A6 | ambiguous in-character speech that resembles an instruction produces **no** durable write (§5.3) |

---

## 9. What this does not do

- No content classification, rating, or restricted frame type.
- No new authorization principal; no change to `ValidateForAudience`.
- No change to `CompactV2`, `plan/3`, Run-1c, routing, or displayed output.
- No prose blob: scene facts and dialogue obligations stay ordinary `PlanItem`s.
  The frame says how to read them and nothing else.

**Status: proposed as `plan/3.1`, a new negotiated version. Not implemented.**
