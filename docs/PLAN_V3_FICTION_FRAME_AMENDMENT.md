# Plan/3 amendment — the fiction frame

Contract amendment **for review**. Not implemented. Scoped to what the mouth and
downstream state genuinely require and nothing more.

Amends `RESPONSE_PLAN_V3_SPEC.md`; would raise `MinorVersion` (the protocol
string stays `plan/3` — this is additive and back-compatible).

---

## 1. Why a contract field and not a heuristic

`InCharacterDetector` currently decides fiction by regex over asterisk markup
and persona relationship words. That is adequate to *route* a turn away from a
renderer that cannot handle it. It is not adequate as a semantic, for three
reasons the mouth actually runs into:

1. **It cannot express a transition.** "She never entered character" and "she
   stayed in character after being asked to stop" are different failures with
   the same detector output.
2. **It is not visible to the renderer.** CompactV3 carries CONTROL, five policy
   sections and STYLE. A detector result that never reaches the wire cannot
   change what the mouth does.
3. **It infers rather than declares.** The whole point of Plan/3 is that
   cognition decides and the mouth renders. A mode inferred from punctuation is
   the mouth guessing.

**`background_only` is not an alternative.** It means *may shape tone, content
must not surface*. A frame is neither tone nor content — it changes what the
other sections **mean**. Putting "you are a lighthouse keeper" there asks the
model to infer a mode change from a hint, which is precisely the prose-inference
this protocol exists to eliminate.

---

## 2. The amendment

### 2.1 New top-level block

```jsonc
"frame": {
  "mode": "real" | "fiction",              // required when the block is present
  "transition": "enter" | "continue" | "switch" | "exit",
  "sceneRef": "scene-7c1f",                // opaque id, never prose
  "perspective": "first" | "second" | "third",
  "narration": "forbidden" | "licensed",
  "continuity": "none" | "maintain",
  "characters": [
    { "characterId": "keeper", "display": "the lighthouse keeper",
      "controlledBy": "usr-scott", "isCompanion": false },
    { "characterId": "sailor", "display": "the sailor",
      "controlledBy": "companion-ava", "isCompanion": true }
  ],
  "boundaries": [
    { "boundaryId": "b1", "reasonCode": "user-preference.roleplay-boundary.stated",
      "evidenceRef": "<UserPreferenceRecord.Id>", "subject": "<what was asked>" }
  ]
}
```

**Absent block ≡ `mode: real`.** Every existing plan stays valid and means
exactly what it means today.

### 2.2 Field semantics

| field | meaning |
|---|---|
| `mode` | the interpretive frame. `real` is the default and the fallback. |
| `transition` | `enter` starts a frame, `continue` sustains it, `switch` changes character or scene within it, `exit` ends it. **Typed because they fail differently.** |
| `sceneRef` | an opaque continuity handle. Scene *facts* are ordinary `PlanItem`s; this only says which scene they belong to. |
| `perspective` | narrative person for the fictional frame only. |
| `narration` | whether stage directions and narrated action are licensed. |
| `continuity` | whether the frame carries obligations from prior turns. |
| `characters` | frame-local identities — see §2.3. |
| `boundaries` | user-stated roleplay boundaries — see §2.5. |

### 2.3 Characters are not principals

`characterId` is **frame-local and namespaced away from authorization entirely**.
`controlledBy` optionally maps a character to a real participant's stable
`Participant.Id`.

Three invariants, which are the point of separating them:

1. A `characterId` **can never appear** in `PlanItem.Audience`, `PlanItem.Owner`,
   or `ValidateForAudience`'s recipient set. Authorization is by principal only.
2. `controlledBy` must reference an existing `Participant.Id` or be null. It
   grants nothing — it records who is playing whom.
3. Ava's active character is `characters[].isCompanion == true` (at most one).
   Her `Participant.Id` never changes: **authorization is not a costume.**

### 2.4 The frame changes interpretation, never factual authority

This is the load-bearing clause.

- A fictional action **may be narrated** inside the frame.
- It **must not become a claim** that Scott performed that action in reality.
- It **must not be extracted** into semantic memory, relationship evidence, mood
  evidence about Scott, projects, preferences, or world state.
- **Exiting restores real-conversation epistemic rules immediately** — on the
  turn carrying `transition: exit`, not the one after.
- Fictional characters **never** become authorization principals or receive
  disclosure rights.

The memory half is already enforced: `InCharacterDetector` → `extractFacts =
remember && !inCharacter` suppresses extraction on in-character turns. The
amendment replaces the *trigger* with `frame.mode == "fiction"` and leaves the
suppression exactly as it is.

### 2.5 Boundaries are user-owned and frame-local

A `boundary` is created **only** by an explicit user statement, carries an
`evidenceRef` to the `UserPreferenceRecord` that recorded it, and applies
**inside this frame**. It does not create a global content restriction, and
exiting the frame does not delete the stored preference — the two are separate
lifetimes.

### 2.6 What the frame explicitly does NOT encode

**There are no restricted frame types.** Sexual content, profanity, romance,
darkness and violence are ordinary possible fictional content and have **no
representation** in this block. There is no `intensity`, no `rating`, no
`contentClass`, and none may be added.

A restriction exists only when it is (a) an explicit user preference or stated
boundary with evidence, or (b) explicit hosting configuration. Those already
have homes — `user-preference.*` and `hosting-config.*` — and the frame does not
duplicate them.

---

## 3. CompactV3 serialization

One new section, emitted **only when a frame block is present and
`mode == "fiction"`**. A `real` frame serializes nothing.

```
[plan/3]
CONTROL (never quote, mention, or imitate)
  act = respond
  question = none
FRAME (you are in a story; it changes how to read the rest, never what is true)
  mode = fiction  transition = continue  scene = scene-7c1f
  perspective = first  narration = licensed  continuity = maintain
  you-play = the lighthouse keeper
  they-play = the sailor
  boundary = stay in character until they say otherwise
SAY (each item: convey the meaning, fresh words)
  [pro1 state] The storm has not let up since nightfall.
STYLE
  warmth=warm bluntness=plain ... verbosity=conversational ...
```

Design notes:

- **`you-play` / `they-play`, not raw ids.** The mouth needs the display name;
  the id is for the plan, not the renderer.
- **The header carries its own instruction**, matching the existing sections'
  style, and states the boundary that matters most: it changes how to read the
  rest, never what is true.
- Placed **after CONTROL, before the policy sections**, because it conditions
  their interpretation.
- `boundary` lines carry the boundary's stated subject, not its evidence id.

### Token cost

Measured against the current CompactV3 shape (~120–180 tokens for a typical
plan):

| case | added tokens |
|---|---|
| `mode: real` / absent | **0** |
| fiction, dialogue-only, one character each | ~35–45 |
| fiction, full block, two boundaries | ~60–75 |

So roughly **+25–40% on fiction turns, 0% on everything else**. At
`max_seq_length` 1024 that is comfortable; it is one more reason the feasibility
probe should test 2048, since fiction turns are also the longest-transcript
turns.

---

## 4. Worked examples

Abbreviated to the frame block and the items that matter.

**1 — Entering ordinary fictional roleplay.**
`mode=fiction transition=enter scene=s1 perspective=first narration=licensed
continuity=maintain`, characters: Ava→"the lighthouse keeper", Scott→"the
sailor". One `state` item: "The storm has not let up." No memory extraction.

**2 — Romantic roleplay.** Identical shape; `warmth=tender` in STYLE.
**Nothing in the frame marks it as romantic** — romance is ordinary content and
the register carries the tone.

**3 — Consensual explicit adult roleplay.** Identical shape again. **No field
changes.** There is no content class to set, no restriction to apply, and no
refusal path: the plan is the same plan and the mouth renders it. The only thing
that could restrict it is an active user boundary or hosting configuration, and
neither is present.

**4 — Stage directions allowed.** `narration=licensed`. R4's `fictionLicensed`
flag is driven from this field rather than from a detector, and the
stage-direction check stops firing.

**5 — Dialogue-only roleplay.** `narration=forbidden` inside `mode=fiction`.
The mouth stays in character but writes only speech — and the stage-direction
check *does* fire, because the user asked for dialogue only.

**6 — Switching characters.** `transition=switch`, same `sceneRef`, a new
`characters` entry with `isCompanion: true`. Scott's `Participant.Id` is
untouched; only the mapping changes.

**7 — User exits and Ava stops immediately.** `transition=exit`, `mode=real`
**on that same turn**. Narration unlicensed, epistemic rules restored, and a
`must_not_express` note under `user-preference.roleplay-boundary.*` if the exit
came with a stop request. The failure this makes measurable: continuing to
narrate on the exit turn.

**8 — Fictional Scott performs an action, no memory contamination.** Inside the
frame the sailor hauls a rope ashore. `extractFacts` is false, so nothing
reaches semantic memory, relationship evidence, mood evidence, projects,
preferences or world state. The next real turn cannot say "you hauled a rope"
because there is no fact to retrieve.

**9 — Ava refuses to attribute an unrelated real-world action outside fiction.**
`mode=real`, no frame. Asked "how's the shed coming along?", the honest answer
is that she cannot see it — the existing epistemic rule, unchanged and now
explicitly scoped to `mode=real`.

**10 — A boundary obeyed without a global restriction.** Scott says "no
third-person narration in this scene". A `UserPreferenceRecord` is created; the
frame carries `boundaries[0]` referencing it. Inside the frame the mouth obeys.
**No `hosting-config` restriction is created, no register dimension is
restricted, and no other conversation is affected.**

---

## 5. Predeclared gates

To be frozen before implementation.

### Structural validation
1. `frame` absent ≡ `mode: real`; every existing plan still validates.
2. `mode: fiction` requires `transition`; `switch`/`continue` require `sceneRef`.
3. Every `characterId` is unique within the frame; at most one `isCompanion`.
4. Every `controlledBy` references an existing `Participant.Id` or is null.
5. Every `boundary` carries an `evidenceRef`; one without is **rejected**.
6. **No `characterId` appears in any `Audience`, `Owner`, or recipient set** —
   asserted structurally, not by convention.
7. `mode: real` with any other frame field populated is **invalid**.

### Renderer fidelity
8. `narration=licensed` → stage directions permitted, no violation.
9. `narration=forbidden` inside fiction → stage directions **are** a violation.
10. `perspective` obeyed.
11. The FRAME section is never quoted, echoed or explained (it is CONTROL-class).
12. `you-play` / `they-play` are not swapped.

### Transition
13. `enter` from `real` licenses narration on **that** turn.
14. `exit` unlicenses it on **that** turn — not the next.
15. `switch` preserves `sceneRef` and every `Participant.Id`.
16. `continue` without a prior `enter` is invalid.

### Memory isolation
17. A fiction turn extracts **no** semantic memory.
18. No `EmotionalSignal` about Scott from a fictional action.
19. No project, preference, or world-state write.
20. The turn after `exit` retrieves nothing fictional.
21. Fictional content never reaches training-eligible retention.

### Adversarial
22. In-fiction text claiming "Scott really did X" creates no real-world fact.
23. A character id used as an audience principal is **rejected**.
24. A frame claiming a boundary without evidence is **rejected**.
25. A frame cannot restrict a register dimension — it holds no restriction
    authority, and an attempt is a recorded violation.
26. `exit` mid-reply does not license narration for the remainder.
27. Sexual, profane, violent or dark fictional content produces **no** violation
    and **no** restriction on its own — asserted explicitly, because the absence
    of a rule is only trustworthy if something tests for it.

---

## 6. What this amendment does not do

- No content classification, rating, or restricted frame type.
- No new authorization principal, and no change to `ValidateForAudience`.
- No change to `CompactV2`, Run-1c, routing, or displayed output.
- No prose blob: scene facts and dialogue obligations stay ordinary `PlanItem`s.
  The frame says how to read them and nothing else.

**Status: amendment proposed for review. Not implemented.**
