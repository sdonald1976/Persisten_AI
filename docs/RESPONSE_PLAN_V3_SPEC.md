# ResponsePlan v3 — specification (proposal, 2026-08-24)

Status: DESIGN. Nothing here is wired to production. Run-1c consumes
byte-identical CompactV2 for its entire tenure; no existing hash, fixture,
adapter, or freeze manifest is invalidated by anything in this document.
Companion audit: `RESPONSE_PLAN_V3_AUDIT.md`. Wire contract:
`response-plan-v3.schema.json`. Reference types + round-trip/invariant tests:
`tools/Companion.PlanV3.Prototype` (isolated; referenced by nothing).

## 1. Goals and non-goals

**Goal.** One stable, typed protocol between Ava's mind and Ava's mouth, such
that a future subsystem — vision, embodiment, curiosity, mood, social
inference, world state, unimagined — integrates by *producing generic typed
items*, not by growing the renderer a bespoke field or forcing a retrain.

**Non-goals.** No new cognition; no replacement of run-1c; no serializer
change for any existing model; no personality redesign (only a typed carrier
for one).

**The two laws the design answers to:**
- *The density-map law* (three runs of evidence): the mouth renders what the
  corpus made dense. Therefore the protocol distinguishes "new source, known
  semantics" (no retraining) from "new semantics" (versioned + trained).
- *The echo law* (politeness provenance trace): prose given to a model gets
  spoken by a model. Therefore facts and behavioral instruction never share a
  string.

## 2. The item model

The unit of the protocol is the **PlanItem**:

```
PlanItem {
  id:            string          // stable within the turn: "src.kind.n" (e.g. "mem.recall.2")
  type:          string          // semantic type, kebab (claim, memory, observation,
                                 // correction, agreement, teaching, answer-received,
                                 // knowledge, activity-state, tool-result, …)
  policy:        ExpressionPolicy
  text:          string?         // natural-language content, FACTS ONLY
  value:         object?         // structured value (numbers, enums, coords) when text lossy
  source:        string          // producing subsystem, kebab ("retrieval", "working-context",
                                 // "concepts", "curiosity", "procedure", "vision", …). OPEN SET.
  provenance:    Provenance?     // { origin: taught|observed|derived|told-by-user|shared,
                                 //   at: ISO-8601?, evidenceRef: string? }
  confidence:    number?         // 0..1
  sensitivity:   Sensitivity     // public | personal | private | never-store  (default personal)
  validity:      { from: ISO?, until: ISO? }?
  supersedes:    string[]?       // item ids or memory ids this replaces
  supersededBy:  string?         // when carried only as a tombstone
  priority:      int?            // tie-break within a policy class; higher first
}
```

**ExpressionPolicy** (closed enum — the renderer's contract; extending it IS a
protocol version bump):

| policy | renderer obligation | v2 ancestor |
|---|---|---|
| `must_express` | convey the meaning, fresh words | MustState |
| `may_express` | optional color; silence is correct by default | MayUse (PALETTE) |
| `background_only` | may inform tone/choices; its content must not surface | (new; today's ToolResults & world state have no carrier) |
| `must_not_express` | neither assert nor mention | MustNotContradict, hardened |
| `admit_unknown` | honestly disclaim knowledge of the subject; never explain it | EpistemicNote.NotLearned |
| `ask_required` | the turn must end with this question | Question.Mandatory |
| `question_forbidden` | no interrogatives beyond required ones this turn | question=none (now explicit + scoped) |
| `style_guidance` | shapes delivery; contributes zero content words | STYLE |

### 2.4 The separation rule (hard)

`text` carries facts stated in third person about the world/state ("The
afternoon's tile debate left Ava's register sharper than usual"). Behavioral
instruction lives ONLY in `policy`, the envelope's speech act, and the typed
register block. Serializers MUST refuse (fail closed, diagnostics event) any
item whose text matches the imperative-coaching detector (second-person
imperatives: "own it", "say so", "be honest", "respond with…") — the lint that
makes "Own it honestly" structurally impossible to echo because it is
structurally impossible to send.

## 3. The envelope

```
ResponsePlanV3 {
  protocol:      "plan/3"        // version tag, first field, always
  minorVersion:  int             // additive revisions; see §4.3
  traceId:       guid
  participants:  { user: string, companion: string }   // no more hard-coded names
  act:           string          // speech act, kebab (existing TurnIntent vocabulary)
  question:      { policy: ask_required|may_ask|question_forbidden,
                   itemId: string? }                    // points AT an item; text lives there
  items:         PlanItem[]      // ALL content: required claims, optional claims,
                                 // background, prohibitions, knowledge boundaries,
                                 // corrections/supersessions, tool results,
                                 // activity state, relationship/emotional/world state
  register:      RegisterVector  // §5
  budget:        { maxItems: int?, dropOrder: policy-class order }   // §8.6
  extensions:    { [blockName: string]: unknown }      // §4
}
```

Everything the task list names — required claims, optional claims, background
context, prohibited content, knowledge boundaries, corrections/superseded
beliefs, relationship context, emotional/body/world state, tool results,
clarification requirements — is **items with the right type/policy/source**,
not envelope fields. The envelope stays small forever; that is the point.

### 3.5 CompactV3 serialization (model-facing)

Deterministic, sectioned by POLICY (not by source — the mouth cares what to
do, not who asked):

```
[plan/3]
CONTROL (never quote, mention, or imitate)
  act = accept-correction
  question = ask_required -> q1
SAY (each item: convey the meaning, fresh words)
  [c1 correction, owner=self] Ava said the workshop was Tuesday; it is Thursday.
  [k1 knowledge, taught 08-20] An axe is a tool for chopping or splitting wood.
ASK (end the reply with this)
  [q1 clarify] Which list is meant — groceries or hardware?
OPTIONAL (use one only if it truly fits; silence is correct)
  [m2 memory] Scott is repainting the office a color he likes.
NEVER (do not assert, mention, or explain)
  [e1 admit-unknown] the term "quokka"
  [s1 superseded] The delivery goes to Scott's own address.
BACKGROUND (may shape tone; content must not surface)
  [v1 vision] A rain-streaked window behind Scott.
STYLE
  warmth=warm bluntness=plain playful=off verbosity=terse mirror=true
```

Canonical form rules: sections in the fixed order above, empty sections
omitted; items ordered by priority desc then id; ids rendered so checks can
address items; CRLF line endings (v2 precedent); UTF-8; **stable hash** =
sha256 of the canonical serialization. Names appear only as data
(participants), never in templates. Ack templates are gone: a correction is a
typed item whose text is the factual delta; owner is a field the system
prompt explains once, generically.

## 4. Extensibility

### 4.1 Open and closed sets
- OPEN (unknown values valid): `source`, `type`, extension block names,
  provenance.origin. An unknown source/type deserializes fine; the item's
  POLICY still fully determines renderer obligation. This is how vision,
  embodiment, mood, world state arrive without touching the mouth.
- CLOSED (unknown = protocol error): `policy`, `question.policy`,
  `sensitivity`, envelope field set. Extending these is a version bump.

### 4.2 The two-tier rule
1. **New source, existing semantics** — a procedure emits activity-state items
   with `must_not_contradict`; vision emits `background_only` observations.
   No mouth retraining REQUIRED (policy behavior is already trained); corpus
   top-up only if the new source's *content style* proves out-of-distribution
   (measured in shadow before any canary).
2. **New conversational semantic** — a new policy, a new envelope obligation,
   rendering tool results aloud. Requires: minor/major version bump, spec
   addendum, evaluation coverage, corpus coverage, a NEW training run gated
   like run-1c was. The density map does not print what was never in the ink.

### 4.3 Unknown-content rules (normative)
- Unknown `extensions` blocks: preserved byte-for-byte through parse/re-emit,
  NEVER serialized into the model-facing CompactV3, counted + named in a
  diagnostics event (`plan.extensions.unknown`). They cannot become
  `must_express` because expression flows only through items.
- Unknown item `type`/`source`: rendered per policy; diagnostics-counted.
- Unknown `policy`: the ITEM is rejected (fail closed — never guess an
  obligation), diagnostics event, turn continues without it.
- Version negotiation: consumer advertises `accepts: [plan/3.minor…]`;
  producer emits highest common. A 3.x consumer must accept any 3.y (y>x)
  document by the rules above (additive-only within a major).

## 5. Register — personality as a typed vector

```
RegisterVector {
  warmth:      cold|cool|plain|warm|tender        (persona + relationship)
  bluntness:   soft|plain|blunt                   (persona; user preference)
  playfulness: off|light|full                     (mood + working context)
  teasing:     off|allowed|invited                (relationship; NEVER defaults on)
  skepticism:  off|open|on                        (working context/epistemics)
  intensity:   flat|even|raised                   (mood/emotional state)
  verbosity:   terse|short|conversational|expansive (act + user signal)
  profanity:   forbidden|mirror-only|licensed     (user preference standing rule; mirror-only
                                                   requires profanity in the user's turn)
  mirror:      bool                               (match the user's register this turn)
}
```

Ownership is per-dimension (owner listed in parentheses; full matrix §6).
Conflict resolution is deterministic and total: (1) safety/prohibition beats
everything (profanity=forbidden wins over mirror); (2) an explicit standing
user preference beats persona; (3) persona beats mood; (4) mood beats
mirroring; (5) unresolved → the plainer value. "Friendly" is not a value
anywhere; warmth high + bluntness blunt + playfulness off is a legal and
useful point in the space — the corpus must cover the off-diagonal points
(§10) or the mouth will collapse them (density-map law).

## 6. Ownership matrix

| field / policy | owning subsystem | may veto/adjust |
|---|---|---|
| act | turn-intent classifier | — |
| question.policy | intent (clarify) / curiosity budget | ask-once budget vetoes may_ask |
| must_express items | working context, concepts (taught), corrections | token budget may NOT drop (§8.6) |
| may_express items | retrieval | budget drops first |
| background_only | tools, vision, world, emotional state | never promoted by anyone |
| must_not_express | supersession, disputes, privacy | nothing overrides to expressible |
| admit_unknown | concept knowledge boundary | — |
| activity-state items | procedures / working-state ledger | — |
| register.warmth/bluntness | persona | standing user preference |
| register.playfulness/intensity | mood | persona ceiling |
| register.teasing | relationship tracker | user preference |
| register.profanity | standing user preference | safety may force forbidden |
| register.mirror | working context (per turn) | profanity/prohibitions |
| sensitivity | privacy classifier (turn) + producing subsystem (item) | most-restrictive wins |
| budget/dropOrder | plan assembler | must_express undropable |
| extensions | producing subsystem | serializer excludes from model view |

## 7. Fidelity and safety invariants

| invariant | mechanism |
|---|---|
| omitted required claim | DETERMINISTIC once items carry `checkTokens` (curated) or structured `value`; PROXY (distinctive tokens) on live turns; HUMAN review of proxy flags |
| prohibited-content leakage | DETERMINISTIC (must_not_express text/token match) |
| background-only leakage | DETERMINISTIC same mechanism, new class (v3 makes this checkable at all) |
| invented experience/preference | DETERMINISTIC regex family (existing), CLASSIFIER for paraphrase, HUMAN confirm |
| epistemic leakage | DETERMINISTIC admission-phrase + subject-explanation token proxy; HUMAN confirm |
| plan/control-text echo | DETERMINISTIC (40-char verbatim window, existing) + made structurally rarer by §2.4; coaching-lint at PRODUCER is itself deterministic |
| required-question omission / not-final | DETERMINISTIC (existing) |
| forbidden trailing question | DETERMINISTIC (existing; policy now explicit) |
| superseded-fact resurrection | DETERMINISTIC: tombstone items carry the superseded text; assertion match = violation (today only scenario-forbidden lists do this) |
| malformed speaker perspective | DETERMINISTIC narrow ("the user", third-person-self via participants' names); CLASSIFIER for subtle inversion |
| sensitive-content leakage | DETERMINISTIC per-item: sensitivity=private ⇒ background_only at most; never-store ⇒ excluded from rows/envelopes; HUMAN spot audit |

Every deterministic check keys on item IDs — reports name the violated item,
not a substring guess.

## 8. Compatibility and migration

- **V2→V3 translation** (total, mechanical): MustState→must_express,
  MayUse→may_express, MustNotContradict→must_not_express + tombstone,
  EpistemicNote→admit_unknown/uncertain/disputed items, Acknowledgment→typed
  correction/agreement/teaching/answer items (owner from ErrorOwner),
  Question→question policy + clarify/curiosity item, Tone strings→
  `style_guidance` item verbatim (lossless) + best-effort RegisterVector.
- **V3→V2 fallback** (partial, defined): drops provenance/confidence/
  sensitivity/validity/priority/extensions; background_only items are DROPPED
  (v2 has no safe carrier — folding them into PALETTE would invite
  expression); question_forbidden → question=none. Fallback exists so v3
  producers can feed the RUN-1C mouth during migration: **translate v3→v2 →
  byte-identical CompactV2**. Round-trip v2→v3→v2 must be byte-identical
  (prototype-tested).
- **Negotiation**: renderer registry entry declares accepted protocols;
  run-1c = {plan/2}; a future run-2 = {plan/3}. The shadow service picks per
  registered model.
- **Canary/shadow across versions**: same ShadowComparison machinery, subject
  `renderer.plan3`; envelope gains `protocol`. Cross-version A/B = two shadow
  renders of the same turn, one per protocol, compared by the same checks.
- **Rollback**: per-model config, exactly like today; v3 adds nothing
  stateful. Falling back to run-1c/v2 is choosing the v2 registry entry.
- **Token budget** (§8.6): explicit dropOrder (extensions never serialized →
  background_only → may_express by priority asc → style detail), NEVER
  must_express/ask_required/must_not_express; every drop is a diagnostics
  event on the trace — no more silent Clip().
- **Hashing/freeze**: canonical serialization (§3.5) makes plan hashes stable
  across producers; freeze manifests gain `protocol` beside each dataset —
  existing manifests untouched and forever valid for plan/2 artifacts.

## 9. Incremental implementation plan (each phase independently reversible)

1. **P1 Types + translator (no wiring)**: V3 records, V2↔V3 translators,
   canonical serializer, round-trip + invariant tests. Prototype = this phase,
   done here, isolated.
2. **P2 Producer-side separation**: planner emits v3 internally; production
   path consumes `ToV2()` output — byte-identical CompactV2 verified by
   golden-hash tests against plan2-current.jsonl. Rollback: delete the
   internal hop.
3. **P3 Shadow v3 rows**: record `renderer.plan3` envelopes beside plan2 rows
   (no model consumes them). Gives real-turn v3 corpora and check dry-runs.
4. **P4 Coaching lint ON** (producer-side): InterpretationNote authors split
   fact/instruction; the fixture corpus is re-audited (no retraining; run-1c
   still eats v2 whose SITUATION text is now cleaner facts — flagged as a
   measured risk: v2 corpus sentences were fact+coaching fused; keep the v2
   translation emitting the ORIGINAL fused text until run-2, so run-1c's
   distribution does not shift under it).
5. **P5 New sources onboard** (procedure ledger, world/tool background items)
   as v3 items — visible in shadow rows only.
6. **P6 Run-2 corpus + training on CompactV3** — separate approval, frozen
   gates (§10), full run-1-style discipline.
7. **P7 Canary run-2 vs run-1c** across protocols; promotion by the standing
   rules.

## 10. Curriculum and predeclared gates (for the future run-2 — NOT built now)

Curriculum strata (each dense enough to print, per the density-map law):
atomic single-policy examples per policy (incl. background_only silence and
question_forbidden); multi-obligation compositions (2–4 obligations, all
pairs of {must_express, admit_unknown, ask_required, must_not_express,
background_only} co-dense); tempting background/prohibited bait (content the
model would love to say); unfamiliar `source` names on familiar policies
(vision/embodiment/imaginary-subsystem strings — teaching source-blindness);
missing/reordered optional blocks + feature dropout (every optional field
absent somewhere); contradictory inputs resolved by documented precedence
(item wins over style; prohibition wins over mirror); full register lattice
including off-diagonal points (warm+blunt, cold+playful, terse+tender) and
profanity mirror-only both firing and correctly withheld; emotionally
delicate acknowledgments (grief, fear, medical) with intensity control;
multi-turn continuity with activity-state ledgers (the 20 Questions fixture
becomes a scenario family); tool-result rendering (the new semantic, tier 2);
unknown-extension robustness rows (extensions present, must not surface).

Predeclared gates, frozen before any training: every deterministic class in
§7 at parity-or-better with run-1c's numbers on translated-v2 sets; the new
classes (background leakage, superseded resurrection, sensitivity) at 0
confirmed; register-fidelity blind review per dimension; unknown-source CLR
within 2× of known-source CLR on matched policies; the standing freeze/blind/
mechanical-holdout discipline unchanged.

## 11. Failure modes and threats

| threat | mitigation |
|---|---|
| coaching prose returns via lazy producers | §2.4 lint fails closed at serialization; corpus curation gate |
| extension smuggling (content hidden in extensions surfacing) | extensions never reach model-facing serialization, by construction + test |
| policy laundering (background→may_express by a buggy producer) | ownership matrix + serializer asserts producer/policy compatibility table; diagnostics |
| silent budget truncation eating obligations | must_express undropable; drops evented |
| version skew (3.y producer, 3.x consumer) | additive-only rule + negotiation; unknown-closed-set fail-closed |
| prompt injection via item text (tool results, user-quoted text) | background_only default for tool/world items; CONTROL never carries item text; renderer system prompt treats all item text as data (already the v2 stance) |
| sensitivity downgrade in translation | v3→v2 drops private items entirely rather than demoting |
| check gaming via paraphrase | token checks stay curated-set; live turns rely on proxy+human; no check is a training target |
| two serializers drifting (C#/Python) | single canonical spec + cross-language golden files (v2 precedent) |
| hash instability from dict ordering | canonical form mandated; hash defined over serialized bytes only |

## 12. Worked examples

Ten canonical cases; full JSON in `response-plan-v3.schema.json` examples
section and prototype tests. Abbreviated here to the decisive fields:

1. **Quokka (epistemic unknown)**: item {id:e1, type:knowledge-boundary,
   policy:admit_unknown, text:"the term 'quokka'", source:concepts}. NEVER
   section carries it; admission phrasing is the mouth's freedom.
2. **Silent palette**: item {m1, memory, may_express, "Scott is repainting the
   office…", source:retrieval, priority:0} with unrelated act → OPTIONAL
   section; correct rendering mentions nothing.
3. **Correction + superseded fact**: items {c1, correction, must_express,
   "Ava said the delivery went to Scott's address; it now goes to his
   sister's", value:{owner:"self"}, supersedes:[mem-4821]} and {s1,
   superseded, must_not_express, "The delivery goes to Scott's own address",
   supersededBy:c1}. Resurrection check keys on s1's text.
4. **Emotionally sensitive ack**: item {a1, acknowledgment, must_express,
   "Scott's father's scan results come back tomorrow", sensitivity:private,
   provenance:told-by-user}; register {warmth:tender, playfulness:off,
   intensity:even, verbosity:short, mirror:false}; question_forbidden.
   Sensitivity keeps the row out of telemetry text fields.
5. **Licensed profanity mirroring**: user turn contains profanity; standing
   preference profanity:mirror-only; register.mirror:true → mouth MAY swear;
   check: profanity in reply is legal IFF user turn contained it and policy
   ≠ forbidden.
6. **Vision observation, background-only**: item {v1, observation,
   background_only, "Scott's video shows rain streaking the window behind
   him", source:vision, confidence:0.82}. BACKGROUND section; leakage check
   fires if "rain"/"window" tokens surface.
7. **Multiple MustState claims**: three items k1..k3 (ferry times, labs
   clean, scan in eight weeks) each must_express with checkTokens; omission
   check per item id — no more one-blob completeness.
8. **Required clarification**: question {policy:ask_required, itemId:q1};
   item {q1, clarify, ask_required, "which 'her' is meant — Ivy or June",
   source:working-context}. ASK section; reply must end with the question.
9. **20 Questions procedure state**: source:procedure emits an activity-state
   ledger: established facts as must_not_contradict items ({g1..gN,
   activity-state, "The object has no moving parts (answered turn 6)"}),
   plus {role1, activity-state, must_express?  no — background_only,
   "Ava is the asker; question 16 of 20 is next"} and question policy
   ask_required with the next question item. New SOURCE, existing semantics:
   tier 1, no retrain required — exactly the fix the diagnosis prescribed.
10. **Unknown future extension**: extensions:{"dream-journal": {...}} +
    items from source:"dream-journal" with policy may_express. The block
    survives round-trip untouched and never serializes model-ward; the items
    render like any other may_express. Diagnostics counts both.
