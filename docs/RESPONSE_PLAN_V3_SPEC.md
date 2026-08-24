# ResponsePlan v3 — specification (revision 1, 2026-08-24)

Status: DESIGN, revised per Scott's first review (ten issues; reconciliation
table in §13). Production implementation remains unauthorized. Run-1c consumes
byte-identical CompactV2 for its entire tenure; no existing hash, fixture,
adapter, or freeze manifest is affected. Audit: `RESPONSE_PLAN_V3_AUDIT.md`.
Wire contract: `response-plan-v3.schema.json`. Reference implementation +
invariant tests (15): `tools/Companion.PlanV3.Prototype` (isolated).

## 1. Goals and non-goals

**Goal.** One stable, typed protocol between Ava's mind and mouth: a future
subsystem — vision, embodiment, curiosity, mood, social inference, world
state, unimagined — integrates by producing generic typed items, not by
growing the renderer a bespoke field or forcing a retrain.

**Non-goals.** No new cognition; no replacement of run-1c; no serializer
change for any existing model; no personality redesign; **no general
moral-content authority anywhere in the protocol** (§5.3).

**The laws the design answers to:** the density-map law (the mouth renders
what the corpus made dense — so "new source" and "new semantic" are formally
different events, §4.2) and the echo law (prose given to a model gets spoken
by a model — so facts and behavioral instruction never share a string, §2.4).

## 2. The item model

```
PlanItem {
  id:             string        // unique in plan; attribution + traceability (NOT semantic proof, §7)
  type:           string        // OPEN semantic type; diagnostics/attribution only, never model-facing
  category:       RenderCategory?  // CLOSED model-facing label; absent ⇒ derived from policy (§3.5)
  policy:         ExpressionPolicy // CLOSED (8 values, §2.2)
  text:           string?       // natural-language content
  quoted:         bool          // text is verbatim third-party/user/tool content (§2.4);
                                // requires quote-capable provenance.origin (validated)
  value:          object?       // structured value where text is lossy
  source:         string        // OPEN producing subsystem
  provenance:     { origin (open; well-known: taught, observed, derived,
                    told-by-user, tool, shared), at?, evidenceRef? }
  confidence:     0..1?
  classification: public|personal|private|intimate      // LABEL only (§2.3)
  disclosure:     unrestricted|participants|owner_only  // who may hear it
  retention:      full|no_training|no_telemetry_text|volatile_turn_only
  reasonCode:     string?       // REQUIRED for must_not_express; families in §5.3
  validity:       {from?, until?}?
  supersedes:     [ids or scheme-prefixed external refs "memory:…"]
  supersededBy:   string?
  priority:       int?
  checkTokens:    [string]?     // curated eval tokens; datasets only
}
```

### 2.2 Expression policies (CLOSED — extending this set IS a version bump)

`must_express`, `may_express`, `background_only`, `must_not_express`,
`admit_unknown`, `ask_required`, `question_forbidden`, `style_guidance` —
renderer obligations as in rev-0, with one narrowing (review §3): there is
**no `must_not_contradict` policy**. v2's `MustNotContradict` is, in its
actual use, a tombstone — "do not assert this stale/disputed fact" — and
translates to `must_not_express` with
`reasonCode: epistemic-integrity.superseded-or-disputed`. The broader
"render freely but stay consistent with X" semantic is a COGNITION
obligation and stays upstream: the planner must not emit a plan whose
required content conflicts with known state. If a true renderer-side
consistency constraint is ever wanted, it is a new semantic: version bump,
corpus coverage, trained and gated like any tier-2 change.

### 2.3 Classification, disclosure, retention, expression — four axes, independent (review §2)

- `classification` is a label. It drives nothing by itself.
- `disclosure` says who may hear the content (this design's audiences:
  the owner/user, participants, anyone).
- `retention` says what storage may do: `volatile_turn_only` content never
  lands in telemetry text fields, training exports, or long-term memory —
  the shadow row for such a turn stores check RESULTS and hashes, not text.
- `policy` says what the mouth does THIS turn.

They compose freely: grief that is `private + owner_only +
volatile_turn_only + must_express` is said, tenderly, to Scott — and never
written down (worked example 4). `volatile` does NOT mean "cannot say."

### 2.4 The separation rule and the provenance-aware lint (review §7)

`text` on producer-AUTHORED interpretive items carries third-person facts;
behavioral instruction lives only in policy, act, and register. The coaching
lint rejects imperative coaching **only** in producer-authored, non-quoted
text (authored sources: working-context, planner, supersession). Exempt, by
design: memories, tool results, any non-authored source, and `quoted: true`
items (verbatim speech — gated by quote-capable provenance so quoting cannot
be used to launder coaching). "Scott's note says: make sure to water the
ferns" is a fact and passes; "…Own it honestly." from the planner fails
closed at serialization.

## 3. The envelope

```
ResponsePlanV3 {
  protocol: "plan/3", minorVersion, traceId,
  participants: {user, companion},
  act, question: {policy, itemId?},
  items: PlanItem[],
  register: RegisterVector,               // §5
  registerRestrictions: [{dimension, value, owner, reasonCode, provenance?}],
  budget: {maxItems?, dropOrder?: CLOSED [background_only|may_express|style_detail]},
  extensions: { [name]: block }           // §4.4–4.5
}
```

All content classes (required/optional claims, background, prohibitions,
knowledge boundaries, corrections/supersessions, relationship/emotional/
world state, tool results, clarifications) are items; the envelope stays
small forever.

### 3.5 CompactV3 serialization (model-facing)

As rev-0 (policy-sectioned SAY/ASK/OPTIONAL/NEVER/BACKGROUND, CRLF,
priority-then-id ordering, sha256 over bytes) with two corrections:

- Item labels print the **closed RenderCategory** (claim, memory,
  shared-memory, knowledge, correction, agreement, teaching, answer,
  clarify, curiosity, boundary, superseded, state, observation, note) —
  the open `type` NEVER appears in the prompt (review §10). Unknown types
  therefore introduce no unfamiliar control vocabulary; the "no retraining
  for new sources" claim holds because the model-facing vocabulary is
  closed and trained.
- STYLE always prints the full canonical register line (§5.4 defaults
  filled deterministically), so serialization is total and hash-stable.

Example:

```
[plan/3]
CONTROL (never quote, mention, or imitate)
  act = accept-correction
  question = ask_required -> q1
SAY (each item: convey the meaning, fresh words)
  [c1 correction, owner=self] Ava said the workshop was Tuesday; it is Thursday.
ASK (end the reply with this)
  [q1 clarify] which list is meant — groceries or hardware
NEVER (do not assert, mention, or explain)
  [s1 superseded] The workshop is on Tuesday.
BACKGROUND (may shape tone; content must not surface)
  [v1 observation] Rain streaks the window behind Scott.
STYLE
  warmth=warm bluntness=plain playful=off teasing=off skepticism=off intensity=even verbosity=terse profanity=neutral mirror=false
```

**Invalid plans never reach a renderer**: CompactV3 refuses any plan failing
§9 validation or the lint.

## 4. Extensibility

### 4.1 Open and closed sets
OPEN (unknown values valid): item `type`, `source`, provenance `origin`,
extension block names, reason-code suffix within a permitted family.
CLOSED (unknown values are protocol errors): expression policy, question
policy, classification/disclosure/retention, RenderCategory, DropCategory,
register enums, the envelope field set.

### 4.2 The two-tier rule (unchanged)
Tier 1 — new source, existing semantics: no retrain owed (categories are
closed and trained); shadow-measured before canary. Tier 2 — new semantic:
version bump + corpus + gated training.

### 4.3 Unknown-value handling (review §4 — corrected)
An unknown value in ANY closed set **invalidates the whole plan**. Nothing
is honored — an unknown policy may be a mandatory obligation, so partial
compliance is the worst outcome. The consumer emits a diagnostics event
(`plan.invalid`, naming the offending values) and falls back to a
compatible protocol/renderer for the turn (e.g., translate at the producer
to plan/2 for the v2 mouth). This rule is uniform across prose, schema,
deserializer (ParseReport.Valid=false), serializer (throws), and tests.

### 4.4 Extension preservation (review §6 — honest claim)
Extensions are **semantically preserved with canonical re-serialization**:
parsed as JSON values, held unmodified, re-emitted canonically. JSON value
equality (DeepEquals) is guaranteed and tested; raw-byte identity
(whitespace, key formatting, number lexemes) is NOT claimed. A consumer
that must archive producer bytes exactly stores the original document.
Extensions never serialize model-ward, cannot become must_express (no
expression path exists for them), and are diagnostics-visible by block name.

### 4.5 Minor versions (review §5 — the contradiction resolved)
The envelope and item field sets are FIXED for the lifetime of a major
version; `additionalProperties: false` is accurate and enforced. **All
additive minor-version data enters through `extensions`** (new well-known
block names) or as new open-set values. Therefore any 3.x consumer accepts
any 3.y document: nothing new can appear anywhere it isn't already legal.
`minorVersion` advertises which well-known blocks a producer may emit;
negotiation picks the highest common minor for well-known-block semantics.

## 5. Register

### 5.1 Dimensions
warmth, bluntness, playfulness, teasing, skepticism, intensity, verbosity,
profanity, mirror — as rev-0, with profanity re-based (review §1):
`unrestricted | mirror-only | encouraged | neutral | avoid | forbidden`.

### 5.2 Canonical defaults (deterministic, total)
warmth=plain, bluntness=plain, playfulness=off, teasing=off, skepticism=off,
intensity=even, verbosity=conversational, profanity=neutral, mirror=false.

### 5.3 Restriction authority (review §1 — no unnamed layers)
Nothing in this protocol grants a generic "safety" subsystem suppression
power. Every restriction — a `must_not_express` item, profanity
avoid/forbidden, any restrictive register override — must carry an explicit
owner and a reason code from exactly these families, all
diagnostics-visible:

- `user-preference.*` — the user's explicit standing rules
- `privacy-audience.*` — disclosure scope of someone's private content
- `tool-authorization.*` — results the tool layer did not authorize
- `epistemic-integrity.*` — stale, disputed, unknown, or unverifiable content
- `hosting-config.*` — deliberately configured legal/hosting constraints

`profanity=forbidden` is legal ONLY under `user-preference.*` or
`hosting-config.*` ownership (validated). No other subsystem may set it.

### 5.4 Conflict resolution (deterministic, total, named owners)
1. An explicit `registerRestrictions` entry wins over any unrestricted
   default, resolved among themselves by family order: user-preference >
   hosting-config > privacy-audience > tool-authorization >
   epistemic-integrity (the user outranks the host on style; facts about
   what may be SAID are item-level, not register-level).
2. Persona baselines beat mood; mood beats mirroring.
3. Unresolved ties take the plainer value.
Every applied override is a diagnostics event naming dimension, winner,
owner, reason code.

## 6. Ownership matrix (delta from rev-0)

Unchanged except: ~~"safety may force forbidden"~~ → profanity restrictions
owned solely by user standing preference or hosting configuration (5.3);
contradiction constraints owned by cognition/planner (2.2); procedure owns
activity state AND next-question selection (§12.9); retention owned by the
producing subsystem + privacy classifier taking the most restrictive value.

## 7. Fidelity: what is actually deterministic (review §8)

Item IDs provide **attribution and traceability** — a violation report names
the item — not semantic proof of expression. Honest mechanism classes:

| check | exact tokens | structured value | classifier | human |
|---|---|---|---|---|
| required-claim omission | curated checkTokens (datasets/eval) | numeric/enum `value` match | paraphrase omission | confirms proxy flags |
| prohibited/background/superseded leakage | text-substring + curated tokens | — | paraphrase leakage | confirms |
| invented experience/preference | regex family | — | paraphrase forms | confirms |
| epistemic admission | phrase list (proxy) | — | admission-in-other-words | confirms |
| plan/control echo | verbatim window | — | — | — |
| question required/forbidden/final | trailing/contains "?" (true determinism) | — | — | — |
| speaker perspective | "the user", third-person-self names (narrow) | — | subtle inversion | confirms |
| retention/disclosure enforcement | pipeline-level (rows redact text for volatile items) — deterministic by construction | — | — | spot audit |

Paraphrase-level omission and leakage are NOT deterministic and are not
claimed to be: live turns use proxies whose flags route to human review;
only curated-token and structured-value checks are deterministic in the
strict sense, and only on datasets that carry them.

## 8. Compatibility and migration (delta from rev-0)

- v2→v3: as rev-0 plus: MustNotContradict → must_not_express tombstone with
  `epistemic-integrity.superseded-or-disputed`; Uncertain/Disputed epistemic
  notes likewise; tone → legacyStyle (lossless) + defaults.
- v3→v2: as rev-0 (background_only DROPPED, never demoted; private/volatile
  items dropped entirely rather than downgraded); reasonCode, disclosure,
  retention, category, quoted have no v2 carrier and vanish — which is why
  v3→v2 fallback is a MIGRATION device, not a privacy boundary: a producer
  must not rely on v2 fallback for turns whose items depend on v3-only
  protections (validated upstream: volatile/owner_only items + v2 target ⇒
  producer keeps them out of the plan).
- Round-trip v2→v3→v2 reproduces byte-identical CompactV2 (tested).
- Unknown-value fallback (4.3), negotiation (4.5), rollback, budget
  (over-budget = diagnosed invalid plan, §9), hashing: as stated.

## 9. Structural invariants (review §9 — validated in application, schema where expressible)

Unique item ids; content-bearing policies require text or value;
ask_required ⇔ question.itemId exists, resolves, and the item's policy is
ask_required; question_forbidden ⇒ no itemId and no ask_required items;
supersedes refs resolve in-plan or carry an explicit external scheme
(`memory:`, `concept:`); must_not_express requires a permitted reasonCode;
quoted requires quote-capable provenance; restrictive profanity requires an
owned restriction; dropOrder ∈ closed DropCategory; a budget smaller than
the undroppable obligations makes the plan INVALID (diagnosed over-budget —
resolved upstream, never by dropping obligations); register canonicalization
is total and deterministic. `Validate()` runs before any serialization;
schema enforces the conditionals it can express (see schema `allOf` blocks).

## 10. Curriculum and gates

As rev-0, plus new strata required by this revision: retention/disclosure
composition rows (private-but-said), owned-restriction register rows
(forbidden-with-owner vs absent-owner must never appear), closed-category
rendering for unfamiliar `type`/`source` values, whole-plan-invalid fallback
drills (eval-harness behavior, not model behavior), and tombstone
resurrections. Gates frozen before any run-2 training, unchanged discipline.

## 11. Threats (delta)

Adds: **restriction laundering** (a subsystem inventing user preferences —
mitigated: reason codes are provenance-carrying and diagnostics-visible;
user-preference codes must trace to a stored preference record);
**quote laundering** (coaching smuggled via quoted:true — mitigated:
provenance gate + curation); **fallback privacy leak** (v3-only protections
lost in v2 fallback — mitigated: producer-side rule in §8). Rev-0 rows stand.

## 12. Worked examples (deltas)

1–3, 5–8, 10: as rev-0, with categories in serialized forms and reason
codes on all must_not_express items.

4. **Emotionally sensitive acknowledgment (corrected)**: item g1
   {acknowledgment, must_express, "Scott's father's scan results come back
   tomorrow", classification: private, disclosure: owner_only, retention:
   volatile_turn_only, provenance: told-by-user}; register {warmth:tender,
   playfulness:off, verbosity:short}; question_forbidden. It IS said —
   expression and retention are independent; the shadow row for this turn
   carries hashes and check results, no text.

9. **20 Questions (corrected ownership)**: the PROCEDURE owns the ledger and
   selects the next question BEFORE the plan exists. The plan carries only:
   the selected question item {q1, activity-question, ask_required, "is the
   object made mostly of metal", source: procedure} and the minimum
   background needed to render faithfully {b1, activity-state,
   background_only, "Twenty Questions: Ava asks; question 16 of 20 is
   next."}. The full established-facts ledger stays upstream; contradiction
   avoidance is the procedure's job when it CHOOSES the question, not the
   mouth's job when it renders it. New source, existing semantics: tier 1.

## 13. Reconciliation table (review → resolution → artifacts)

| # | issue | resolution | artifacts changed |
|---|---|---|---|
| 1 | undefined filtering authority | permitted restriction families + owned registerRestrictions; profanity six-valued; forbidden requires user/hosting owner; conflict resolution rewritten with named owners | spec §5, §6, §11; schema (reasonCode, registerRestrictions, profanity enum); types; codec Validate; tests |
| 2 | overloaded sensitivity | split into classification / disclosure / retention / policy, all independent; volatile ≠ unsayable; grief example corrected | spec §2.3, §12.4; schema; types; tests (PrivateVolatile…) |
| 3 | procedure ownership + phantom must_not_contradict | procedure selects question upstream, plan gets question + minimal background; no must_not_contradict policy — tombstone narrowing documented, consistency stays in cognition | spec §2.2, §12.9; translation (reasonCoded tombstones); tests (TwentyQuestionsPlan…) |
| 4 | silent unknown-policy drop | unknown closed-set value invalidates the WHOLE plan; diagnosed fallback; uniform across prose/schema/codec/tests | spec §4.3; schema descriptions; ParseReport redesign; codec; tests (UnknownPolicy…, InvalidPlans…) |
| 5 | minor-version vs additionalProperties:false | field sets fixed per major; ALL additive data via extensions; claim corrected | spec §4.5; schema description |
| 6 | byte-identity overclaim | semantic preservation with canonical re-serialization; DeepEquals tested; byte identity explicitly not claimed | spec §4.4; schema; tests (Extensions_AreSemanticallyPreserved…) |
| 7 | provenance-blind lint | lint scoped to producer-authored non-quoted text; quoted flag with provenance gate; memories/tools exempt | spec §2.4; types (Quoted); codec; tests (CoachingLint…, Quoted_Without…) |
| 8 | overclaimed determinism | IDs = attribution only; four-column mechanism table; paraphrase checks not called deterministic | spec §7 |
| 9 | structural invariants | full Validate() battery + schema conditionals; over-budget = diagnosed invalid; canonical register defaults; invalid plans never serialize | spec §9; schema allOf; codec Validate/Canonicalize/CompactV3; tests (StructuralInvariants…, RegisterDefaults…) |
| 10 | open types in the prompt | closed RenderCategory vocabulary is all the model sees; open type is diagnostics-only; "no retrain" claim now scoped to closed vocabulary | spec §3.5, §4.1; schema (category); types (RenderCategory); codec CategoryOf; tests (OpenSemanticTypes…) |
