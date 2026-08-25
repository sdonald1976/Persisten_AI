# ResponsePlan v3 — specification (revision 2, 2026-08-24)

Status: **APPROVED (2026-08-24)** — revision 2 conditionally approved and the
three closing amendments (rev-2.1, reconciliation rows 17–19) resolved with
focused tests. P2 (the compatibility
bridge) implemented and accepted: V3 plans are produced by `FromV2` — their
semantic origin is **`translated_v2`, not `native_v3`** — so V3 is currently an
intermediary representation, not the authoritative planning output. The planner
is NOT described as natively producing V3 until it constructs V3 directly from
upstream cognitive state without first building ResponsePlan V2. Byte-identical
CompactV2 proven by golden tests over the complete frozen corpus (804/804) and
guarded at runtime. V3 remains non-authoritative and non-user-facing; run-1c
consumes byte-identical CompactV2 for its entire tenure; no existing hash,
fixture, adapter, or freeze manifest is affected. Audit: `RESPONSE_PLAN_V3_AUDIT.md`. Wire contract:
`response-plan-v3.schema.json`. Reference implementation + 22 invariant tests:
`tools/Companion.PlanV3.Prototype` (isolated; referenced by nothing).

**Wire conventions:** closed enums serialize snake_case; open-set strings are
kebab-case; CompactV3 model-facing labels are kebab-case. Display names label;
only stable ids and scheme-prefixed principal refs authorize.

## 1. Goals and non-goals

One stable, typed protocol between Ava's mind and mouth: future subsystems
integrate by producing generic typed items. No new cognition; no replacement
of run-1c; no serializer change for any existing model; **no general
moral-content authority** (§5.3); **no unnamed suppression, no display-name
authorization, no knowingly incomplete rendering** (rev-2).

Laws: the density-map law (new source ≠ new semantic, §4.2) and the echo law
(facts and instruction never share a string, §2.4).

## 2. The item model

```
PlanItem {
  id, type (open), category (closed, model-facing)?, policy (closed, SIX),
  text?, quoted, value?, source (open), provenance{origin, at, evidenceRef}?,
  confidence?, classification, disclosure, owner?, audience?, retention,
  reasonCode?, validity?, supersedes?, supersededBy?, priority?, checkTokens?
}
```

### 2.2 Expression policies (CLOSED — six values)

`must_express`, `may_express`, `background_only`, `must_not_express`,
`admit_unknown`, `ask_required`.

Removed in rev-2 (review §4): ~~`question_forbidden`~~ — question prohibition
is owned solely by `question.policy`; an item-level duplicate created
contradictory states (a question_forbidden item inside an ask_required plan)
with no use case. ~~`style_guidance`~~ — style is owned solely by the
canonical RegisterVector plus owned restrictions; free-text style items were
the echo-bait door reopening. Neither had a non-overlapping use; both are
gone rather than given conflict rules.

`must_not_contradict` does not exist (rev-1 §3): v2's version is a tombstone
(→ `must_not_express` + `epistemic-integrity.superseded-or-disputed`);
render-time consistency is cognition's obligation, upstream.

### 2.3 Classification / disclosure / owner / audience / retention / expression

Six independent facts about an item:
- `classification` — label only.
- `disclosure` — `unrestricted | participants | restricted`. **`restricted`
  requires an explicit `audience`** of stable principal references.
  ("owner_only" is gone: audience is never inferred from ownership.)
- `owner` — whose information it IS: an in-plan participant id or an external
  `principal:` reference. **Information about a third party is not owned by
  whoever supplied it**: Scott telling Ava about his father makes Scott the
  provenance origin, not the owner.
- `audience` — who may hear it: participant ids / principal refs. Display
  names never appear here (validated).
- `retention` — storage behavior (§2.6). Independent of expression:
  volatile content can still be `must_express` to its authorized audience.
- `policy` — this turn's rendering obligation.

### 2.4 The separation rule and lint — unchanged from rev-1
(producer-authored, non-quoted, authored-source text only; `quoted` gated by
quote-capable provenance.)

### 2.5 Participant identity (rev-2 §1)

`participants: [{id, role: user|companion|other, display}]` — at least a user
and a companion, unique stable ids. Ids survive display renames. Every
audience/owner reference resolves against these ids or carries an explicit
external scheme (`principal:`, and for supersession `memory:`/`concept:`).
Supported ownership: the user, Ava, another present participant, or an absent
third party (`principal:` ref).

### 2.6 `volatile_turn_only` — the precise surface matrix (rev-2 §3)

| surface | behavior |
|---|---|
| in-memory processing | permitted (plans, checks, rendering all operate on the text) |
| HTTP to the renderer | permitted over loopback/authenticated local transport — rendering requires the content; remote renderers require an explicitly configured trusted channel |
| application logs | content forbidden; content-free events (item id, policy, check results) permitted |
| shadow/telemetry rows | text and value fields NULLed; check results, metadata, and the keyed CorrelationTag permitted; plain content-derived hashes FORBIDDEN (§2.7) |
| traces/diagnostics | content-free only |
| crash dumps | residual risk documented: process memory may appear in dumps; deployments that must exclude this disable dumps for the renderer/host processes — the protocol cannot promise below the OS |
| training exports | forbidden, absolutely |
| long-term memory | forbidden; the extraction pipeline skips volatile items |

This matrix replaces every "never written down" phrasing.

### 2.7 Hashing and correlation (rev-2 §2–3)

Two hashes, distinct jobs:
- **`wirePlanHash`** — sha256 over the canonical JSON of the COMPLETE v3
  document (extensions included), with `text`/`value` of volatile items
  redacted to the fixed placeholder `"[volatile]"` first, so the hash derives
  nothing from low-entropy private content (tested: sibling plans differing
  only in volatile text hash identically). Canonical JSON: RFC 8785
  semantics for this document class — ordinal (UTF-16 code unit) key
  ordering, no insignificant whitespace, shortest-round-trip invariant
  number formatting; implemented and cross-format tested in the prototype;
  any other implementation (Python tooling) must match these bytes.
- **`renderPromptHash`** — sha256 over the exact CompactV3 bytes. Extensions
  change `wirePlanHash` but not `renderPromptHash` (tested). For plans
  containing volatile/private items this hash is content-derived and MUST
  NOT be persisted; persisted correlation uses **`CorrelationTag`**: a
  deployment-secret keyed HMAC-SHA256 with key-version prefix (`v1:…`),
  rotatable without content exposure — or no identifier at all where
  correlation is unnecessary.

## 3. The envelope

```
{ protocol, minorVersion, traceId, participants[], act,
  question{policy, itemId?}, items[], register, registerRestrictions[]?,
  budget{maxItems?, dropOrder: CLOSED}?, extensions{}? }
```

### 3.5 CompactV3 — unchanged structure, two rev-2 clarifications
Labels are the closed kebab-case RenderCategory set; `legacyStyle` is
migration metadata and NEVER serializes into CompactV3 (tested); STYLE always
prints the full canonical nine-dimension line. Invalid plans never serialize.

## 4. Extensibility — as rev-1, with §4.3 whole-plan invalidation, §4.4
semantic (not byte) extension preservation, §4.5 minor-versions-through-
extensions-only. ParticipantRole joins the closed sets.

## 5. Register

Dimensions, canonical defaults, and family-ordered conflict resolution as
rev-1 (§5.3–5.4: five permitted restriction families, no unnamed authority).
Rev-2 hardening (review §6): `registerRestrictions.dimension` is CLOSED (the
nine dimensions) and `value` must be legal for its dimension (validated);
`user-preference.*` and `hosting-config.*` restrictions **require
`provenance.evidenceRef`** (a preference record / configuration key) — a
subsystem cannot merely claim that authority (validated + tested).

### 5.5 What a `hosting-config` register vote is, and is not

The §5.4 precedence order is UNCHANGED: `user-preference.` outranks
`hosting-config.`. A `hosting-config` register vote is therefore a **hosting
DEFAULT** — the register the deployment prefers absent a user statement — and
NOT an enforceable deployment restriction. An explicit user preference on the
same dimension overrides it, and the assembler records both.

This is stated because the two read alike in the plan: a hosting vote may carry
`restrictive: true` and appear in `registerRestrictions`, which makes it look
enforceable when it is merely owned and overridable. `restrictive` marks a value
as forbidding rather than shaping; it does not mark it as unoverridable.

**There is currently no enforceable deployment restriction mechanism in this
contract.** An operator requirement that must hold regardless of what the user
asks (a legal, safety, or platform obligation) has no home in the register, and
must not be simulated by re-ranking the families — that would silently invert
who owns speech. Introducing one is a deliberate contract revision to §5.4 with
its own review, not a resolver change.


## 6. Ownership matrix — as rev-1, plus: question prohibition = question.policy
alone; style = RegisterVector + owned restrictions alone; item audience =
producing subsystem constrained by the privacy classifier (most restrictive
wins); correlation keys = deployment configuration (rotation owner: operator).

## 7. Fidelity mechanisms — unchanged from rev-1 (ids are attribution, not
proof; four honest mechanism columns; paraphrase-level checks are never
called deterministic). Rev-2 adds: retention enforcement in telemetry rows is
deterministic by construction and covered by pipeline tests, not renderer
checks.

## 8. Compatibility and migration (rev-2 §5 — protected fallback)

- v2→v3 and the byte-identical round-trip: as rev-1 (tested).
- **v3→v2 is capability-checked, all-or-nothing.** `CheckV2Compatibility`
  refuses translation when ANY obligation or protection would drop or
  weaken: non-`full` retention, `restricted` disclosure, any
  registerRestrictions (v2 has no enforceable carrier). Background-only
  additions are droppable-by-design and translate safely (tested). An
  INVALID v3 plan is never deemed v2-compatible — invalidity does not
  launder into fallback (tested). An incompatible plan routes ONLY to a
  compatible v3 renderer or an explicitly authorized legacy path proven to
  preserve the same obligations; otherwise the turn fails diagnosed
  (`renderer-unavailable`) — **a knowingly incomplete response is never
  generated**.
- Hashes and freeze interplay: dataset/freeze manifests record
  `wirePlanHash` (stable across producers) and `renderPromptHash`
  per protocol; existing plan/2 manifests untouched.

## 9. Structural invariants — rev-1 battery plus rev-2 additions:
participant uniqueness + role coverage; audience/owner resolution (ids or
schemes; display names rejected); restriction dimension/value closure;
evidence requirements. All in `Validate()`, schema-expressible parts in
schema `allOf`. Invalid plans never reach any serializer.

## 10. Curriculum and gates — as rev-1, plus strata: restricted-audience
items rendered without naming the restriction; volatile-content turns
(mouth behavior is identical — the corpus teaches that retention is
invisible to rendering); evidence-backed vs claim-only restriction drills
(harness-level). Gates frozen before any run-2 training.

## 11. Threats — rev-1 table plus:
| threat | mitigation |
|---|---|
| display-name authorization confusion | authorization only via ids/schemes; validation rejects display strings in audience/owner |
| dictionary attack on stored hashes of private text | wire-hash volatile redaction + keyed versioned CorrelationTag; plain content hashes of volatile plans never persisted |
| authority claims without evidence | user-preference.*/hosting-config.* require evidenceRef; validated |
| lossy fallback as privacy/completeness hole | capability check; all-or-nothing translation; diagnosed failure beats plausible incompleteness |
| parallel authorities drifting (item vs envelope) | the duplicated policies were removed; single owner per concern |

## 12. Worked examples — deltas from rev-1

4. **Grief (corrected ownership)**: item g1 {acknowledgment, must_express,
   "Scott's father's scan results come back tomorrow", classification:
   private, disclosure: restricted, **owner: "principal:scott-father"**
   (a third party not present — Scott supplying it makes him the provenance
   origin, not the owner), **audience: ["usr-local"]**, retention:
   volatile_turn_only, provenance: {origin: told-by-user}}. It IS said,
   tenderly; the telemetry row carries check results and a keyed
   CorrelationTag, no text, no plain hash. This plan is v2-INCOMPATIBLE and
   must render on a v3 renderer or fail diagnosed.
9. **20 Questions**: as rev-1 (procedure selects the question upstream;
   plan carries the selected ask + minimal background) — unchanged by rev-2.
Others: as rev-1, with participants arrays and owner/audience where
disclosure is restricted.

### 2.8 Optional questions — the complete may_ask canon (rev-2.1)

`may_ask`: `question.itemId` optional. When present it references a SUGGESTION —
an ordinary `may_express` item with a question-capable category (clarify or
curiosity); it renders in OPTIONAL with a CONTROL pointer (`question = may_ask
-> sq1`) and creates no second authority. When absent, the renderer may
formulate at most one contextually relevant question (behavioral,
fidelity-gated). `question_forbidden` permits no optional question and no
suggestion pointer. `ask_required` requires exactly ONE ask_required item,
referenced, question final. All valid/invalid combinations validated + tested.

### 2.9 Recipient authorization before serialization (rev-2.1)

`ValidateForAudience(plan, currentRecipientPrincipals, rendererTrustContext)`
runs before any CompactV3 (the audience-scoped entry point is `CompactV3For`).
Reference resolution alone is insufficient: restricted items reach the renderer
and user only when the CURRENT recipients are each in the item audience AND the
transport is permitted (local loopback or configured trusted remote for
restricted/volatile content). Unauthorized obligations (must_express /
ask_required) are ERRORS — replan upstream or fail diagnosed, never silently
removed or downgraded. Unauthorized non-obligations (background, optional,
tombstones) are EXCLUDED from the serialization so protected content is never
leaked to an untrusted renderer merely to prohibit it. Five recipient/trust
cases tested.

### 2.10 Protected-content identity (rev-2.1, supersedes parts of §2.7)

`ContainsProtectedContent` derives from DISCLOSURE and RETENTION — restricted
disclosure or any non-full retention — never from the classification label.
`PersistableIdentity` enforces the whole rule in one place: plain plans keep
deterministic plain hashes (wire + render); protected plans persist the
redacted STRUCTURAL wire hash (explicitly NOT a unique content identity — two
protected plans differing only in protected text share it), no
renderPromptHash, and a keyed versioned CorrelationTag computed over canonical
UNREDACTED content — distinct texts stay distinguishable, key rotation changes
version and tag, offline dictionaries stay impossible without the deployment
key. All five review-required behaviors tested.

## 13. Reconciliation table

Review 1 (rows 1–10): as recorded in revision 1 — unnamed authority removed;
sensitivity split; procedure ownership + tombstone narrowing; whole-plan
invalidation; minor-versions-through-extensions; semantic extension
preservation; provenance-aware lint; honest determinism; structural
invariants; closed model-facing categories.

Review 2:

| # | issue | resolution | artifacts |
|---|---|---|---|
| 11 | display-name identity | stable participant ids + roles + display labels; owner/audience as principal refs (in-plan id or scheme); third-party ownership modeled; grief example corrected; validation rejects display-name authorization | spec §2.3/2.5/§12.4; schema participants/owner/audience; types (Participant, Owner, Audience); codec Validate; 2 tests |
| 12 | one hash doing two jobs | wirePlanHash (canonical RFC-8785-semantics JSON, volatile-redacted, extensions included) vs renderPromptHash (CompactV3 bytes); extensions move one, not the other; canonical JSON documented + cross-format tested | spec §2.7; codec WirePlanHash/CanonicalJson/RenderPromptHash; 2 tests |
| 13 | dictionary attacks on volatile hashes | wire-hash redaction placeholder; keyed versioned HMAC CorrelationTag for persisted correlation; §2.6 precise surface matrix replaces "never written down" | spec §2.6/2.7; codec CorrelationTag/ContainsVolatile; 2 tests |
| 14 | parallel item authorities | question_forbidden and style_guidance REMOVED from ExpressionPolicy (six remain); question.policy and RegisterVector are sole owners; no conflict rules needed because no conflict can exist | spec §2.2; schema; types; codec; 1 test |
| 15 | lossy fallback | CheckV2Compatibility before translation; all-or-nothing; invalid ≠ compatible; route to v3 renderer or diagnosed failure; three required tests added | spec §8; codec CheckV2Compatibility; translation TranslateToV2 guard; 3 tests |
| 16 | traceability + wire consistency | evidenceRef required for user-preference.*/hosting-config.*; restriction dimensions/values closed + validated; wire=snake_case, model-facing=kebab-case documented and tested; legacyStyle = migration-only, never in CompactV3 (tested) | spec header/§5; schema; codec; 4 tests |

Review 3 (closing amendments, rev-2.1):

| # | issue | resolution | artifacts |
|---|---|---|---|
| 17 | may_ask underdefined | §2.8 canon: optional suggestion = may_express item with question-capable category, CONTROL pointer, no second authority; exactly-one rule for ask_required; all combinations validated | spec §2.8; codec Validate; FromV2 category fix; 1 test (9 assertions) |
| 18 | recipient authorization | §2.9 ValidateForAudience + CompactV3For: current-recipient + transport checks; obligations error, non-obligations excluded without leakage | spec §2.9; types (RendererTrustContext, AudienceDecision); codec; 5 tests |
| 19 | protected-hash generalization | §2.10 ContainsProtectedContent (disclosure+retention); PersistableIdentity; structural wire hash disclaimed as content identity; keyed tag over unredacted canonical content | spec §2.10; types (PlanIdentity); codec; 2 tests |

## 14. P2 record (2026-08-24)

Implemented on branch `responseplan-v3`, worktree-isolated from the live canary:
protocol implementation moved to `src/Companion.Core/PlanV3/`; the renderer
shadow/canary serialization takes the producer hop (FromV2 → guarded
TranslateToV2 → frozen CompactV2) with a runtime byte-equality guard that falls
back to direct serialization on any divergence, logged — run-1c behavior
cannot change even in the presence of an undiscovered translator bug. Golden
tests cover the complete frozen corpus: 804/804 plans byte-identical through
the hop AND matching their frozen plan2 strings. The golden immediately caught
and fixed a real defect (tone round-trip ambiguity when tone prose contains
"; "). Full suite: 1167 + 31 green. V3 remains non-authoritative and
non-user-facing.

## 15. P3 record and the corrected roadmap (2026-08-24)

**Corrected phase terminology:** everything produced today is `planOrigin =
translated_v2`. Native V3 begins only when the planner constructs V3 directly
from upstream cognitive state with no V2 ancestor.

**P3 (implemented): translated-V2 V3 shadow infrastructure.** Every eligible
turn's V3 envelope is recorded beside the plan2 row (subject `renderer.plan3`)
without CompactV3 ever reaching a model. Rows carry: planOrigin=translated_v2,
the V2 source hash, the structural wire hash, renderPromptHash only when
persistable, a keyed versioned correlation tag where protected, the
V2-compatibility result, and full validation + audience-validation results.
The complete disclosure/retention rules apply BEFORE recording: protected text
never enters rows, logs, traces, diagnostics, or exports; unknown-extension
and invalid-plan events carry names/reason codes only. Rows ride the existing
bounded queue (TryWrite, drop-counted, drain-on-dispose) — canary turns enqueue
a v3-only entry after the reply is chosen, so displayed-response latency is
untouched; production, run-1c routing, memory, reflection, and tools are
unreachable from the recording path by construction. Diagnostics expose
produced/valid/invalid/v2-compatible/protected/redacted/failed/dropped; the
existing renderer.* forget clause sweeps plan3 rows (tested). **translated_v2
rows are never native-V3 corpus examples** — they test translation,
serialization, privacy, and infrastructure only. Committed fixture examples
(synthetic data only): `docs/examples/v3-shadow-fixture-plain.json` and
`docs/examples/v3-shadow-fixture-redacted.json`.

**P4 (implemented): native fact/instruction separation.** `PlanV3Builder`
constructs the first genuinely `native_v3` plans directly from upstream
cognitive state (intent, working context, retrieval results, concept lookup,
curiosity) — never from the V2 plan, its serialization, or its templates
(type-level: the builder cannot receive a ResponsePlan; test-level: no V2
template phrase may appear in native item text; provenance audit names every
consumed input). Facts separate from instructions at the source: acknowledgment
items carry the USER'S OWN QUOTED WORDS plus typed owner participant-ids
instead of templated English; the coaching lint runs at item CREATION and
rejects producer-authored imperatives with content-safe diagnostics while
preserving quoted/user/memory/tool imperatives. Register derives from typed
state only — the v2 tone prose is deliberately not consumed, so most
dimensions sit at canonical defaults: a RECORDED P4 FINDING that upstream
typed register signals do not yet exist (persona/mood work feeds P5+).
Native plans ride the P3 shadow rows beside their translated siblings with
per-class semantic parity (act, required, optional, prohibitions, epistemic,
question policy, correction ownership, register intent) — differences are
attributed evidence, never behavior. Notable parity finding: acknowledgment
content CONVERGES at plan level (the V2 template lives only in the
serializer), so the honest divergences are exactly the lint rejections.
A failed native build records a content-safe diagnostic and production
continues unchanged (tested). V3 remains parallel shadow evidence — NOT
authoritative — until a separately approved migration.

**P5a (complete): the contribution framework — synthetic proof only.** `IPlanV3Contributor` lets any
subsystem PROPOSE typed facts/state; `PlanV3Assembler` alone grants authority —
validating provenance, approving/downgrading/rejecting each proposed policy
against a registered `SourceCapability`, resolving register conflicts by family
precedence, applying disclosure/retention, running the source-side lint,
applying budgets, and emitting the final native plan with a content-safe
report. Registration IS the authority model: an unregistered source can never
reach must_express/must_not_express/ask_required or register restrictions —
its informational items become background_only (and any reason-code claim is
refused outright), diagnosed, never silently promoted. Privileged reason
families are owned by exactly the subsystems holding the state
(`tool-authorization.*` by the tool-authorization subsystem;
`epistemic-integrity.*` by knowledge owners; `user-preference.*` /
`hosting-config.*` only with a stored preference/configuration reference).
Contributors shipped: procedure (ledger upstream, next question selected
upstream, plan receives the selected question plus minimal frame — the four
Twenty Questions failures are prevented structurally), tool (six separated
states; unauthorized/secret results contribute nothing; results are DATA, never
protocol instruction; expression requires planner authorization), perception
(world/vision/embodiment background_only, confidence+validity carried, expired
or thin observations never become claims, promotion requires a deliberate
planner decision and is recorded), and typed register sources (persona,
user-preference, relationship, mood, working-context, mirror) that resolve
off-diagonal personalities — warm+blunt, tender+terse, cold+playful,
skeptical+calm, and all four profanity modes — without collapsing to
"friendly". `PlanV3Builder` did not grow: new organs arrive as contributors.

**Status honesty:** P5a is the FRAMEWORK plus synthetic proof. No live organ
is integrated: the procedure/tool/world/persona subsystems do not yet emit
contributions, no native plan has been assembled from a real turn, and the
register votes exercised in tests are synthetic. Live native-organ
integration is P5b.

**P5b (in progress): real subsystem integration.** Authority hardened first
(grants are exact source+category+policy+reason+provenance+promotion tuples,
never a Cartesian product; the procedure audit is explicit and tested).

- **Source 1a — COMPLETE**: activity domain model (`ActivityInstance`,
  lifecycle, stable question keys, bound answers, diagnosed selection
  failure), the audited procedure authority grant, the contributor, and unit
  proof. Synthetic only.
- **Source 1b — COMPLETE**: the runtime/strategy separation, explicit
  activation, transactional idempotent transitions, the hybrid selector
  (deterministic bank + untrusted structured proposals + deterministic
  validation with bounded retries), open-domain hypothesis state, and the
  final-guess lifecycle are IMPLEMENTED and unit-proven, including a complete
  simulated session that reaches a correct open-domain guess appearing in no
  hard-coded list — together with the shadow-isolated persistent store, the
  real turn-path call site, and the declared LifeRunner volume (12 sessions,
  11 scenarios, 9 pre-declared pass criteria — `SOURCE1B_VOLUME_PLAN.md`).

  The call site takes an immutable snapshot only AFTER the displayed response
  is finalized, records which renderer actually spoke, and identifies the
  displayed question CONSERVATIVELY: exactly one interrogative that matches
  the pending move resolves it; zero, several, or an unmatchable one records
  `displayed-move-unresolved` and invents no identity — with binding permitted
  only in the resolved case. Observed and counterfactual branches are separate
  rows; the counterfactual names its parent and branch point and carries
  `bindable:false`. Every failure is content-safe (exception type only) and
  user-invisible.

  LifeRunner drives complete sessions through the SAME activation → runtime →
  observer → store path, on isolated users and conversations with a controlled
  clock: correct guess (ending "a dildo"), incorrect guess, exhausted limit,
  correction, malformed answer, abandonment, retry/idempotency, restart/resume,
  volatile no-resume, two simultaneous users, and deterministic fallback. All
  12 rows labeled simulated, zero counterfactual moves in any of them, and
  Messages/Conversations/SemanticMemories/Procedures all empty afterwards.

  The live proposer remains an OPERATIONAL DEPENDENCY, not a completion
  blocker: captured-proposal sessions prove the integration.

  **Counterfactual resolution (the key correctness result):** natural shadow
  has two realities. The user answers the question PRODUCTION displayed; the
  native strategy meanwhile selects a question nobody saw. Binding the real
  answer to the undisplayed native question would fabricate evidence, so
  branches are typed — `ProductionObserved` (diagnostic only, never parsed
  into native training targets), `CounterfactualNative` (records the branch
  point, the proposed move, and its validation; may NEVER consume a
  subsequent user answer and is never reportable as a completed natural
  session), and `Simulated` (its moves ARE displayed to a simulated user, so
  its answers bind legally, labeled simulated and never natural). Every move
  carries a disposition — observed_displayed / counterfactual_not_displayed /
  simulated_displayed — the displayed renderer, the displayed question id,
  and a bindability flag derived from all of it.

  Also landed: the shadow-isolated persistent store (`ActivityBranches`,
  purely additive migration — its only DropTable is the Down() of the new
  table) with per-user/conversation isolation, optimistic concurrency,
  per-input idempotency keys that return the existing transition on duplicate
  delivery, terminal/age cleanup, and `/forget`; retention enforced AT the
  persistence boundary so a volatile branch keeps metadata, loses content,
  and sets ContentWithheld — restart-resume diagnosed unavailable rather
  than retention silently weakened; and activation resolved through the REAL
  Procedure store, requiring an explicit request plus exactly one match, with
  ambiguity yielding clarification and no match a diagnosed non-activation.
  Subject matter never affects any of it: an activity whose hypothesis is
  "a dildo" persists exactly like any other, because classification and
  disclosure decide, not topic. Design: `SOURCE1B_SELECTOR_DESIGN.md`;
  inspection: `SOURCE1B_PROCEDURE_INSPECTION.md`.
- **Source 2 — COMPLETE**: typed tool outcomes reach the native plan.
  `ToolExecutionOutcome` is captured AT EXECUTION TIME inside `ToolLoop` and
  carried additively on its `Outcome`; nothing parses `ResultsSection`, the
  rendered result JSON, or any prompt prose. Two contributors split the
  authority — `tool` proposes results, `tool-authorization` alone records that
  something was withheld. Six states are separate facts (requested, authorized,
  executed, succeeded/failed, disclosure permitted, planner disposition) and
  only the last two can make a result expressible.

  Expression comes from exactly one place: the DETERMINISTIC NUDGE tier, where
  a rule matching the user's own phrasing selected the lookup, carries
  `MustExpress`. Planner-selected calls stay `BackgroundOnly` — the tool
  planner is an untrusted model role that may decide what to look up, never
  that Ava must recite what came back. Even then the assembler grants it as a
  recorded promotion. A failure can only be ACKNOWLEDGED, through a
  `(claim, may_express)` tuple scoped by the `tool-failure.` reason prefix so a
  success (which carries no reason code) can never travel it.

  A tool turn is still ineligible for a renderer COMPARISON — run-1c never
  trained on tool results — so it takes the new PLAN-ONLY shadow path: the V3
  row is written, the renderer is never invoked, and no renderer counter moves.
  Proven end to end on a live turn through `RespondAsync`.

  Four defects were found and fixed by the run, including a promotable grant
  that was usable WITHOUT a planner promotion and a `SecretDetector` blind to
  JSON-shaped credentials. Results, measured tallies, the real-vs-constructed
  split, and the remaining blockers: `SOURCE2_RESULTS.md`.

  STILL MISSING: no tool declares an authorized audience, a retention class, or
  produces a cancellation, so those three scenarios are constructed rather than
  live; and cognition has no general typed channel for "this result is required
  in the reply" beyond the nudge tier.
- **Source 3 — COMPLETE**: explicit user preferences reach the native plan
  through a real structured layer. The inspection (`SOURCE3_PERSONA_INSPECTION.md`)
  found the stop condition on all three counts — no identity, no precedence, no
  revocation — and the approved amendments shaped the fix:
  `UserPreferenceRecord` (stable id, closed dimension/value, global scope,
  evidence, insert-and-link lifecycle), a closed six-pattern command
  interpreter at the REAL AdjustStyle path (routing untouched; ambiguity
  creates nothing), a pure order-independent resolver whose report carries no
  preference text, and two SEPARATE authorities — user records vote under
  `user-preference.*` with record-id evidence, hosting configuration votes
  under `hosting-config.*` with config-path evidence, and the assembler's
  contract precedence (§5.4: user above hosting) decides between them with
  both recorded. Register preferences are votes; expression restrictions are
  must_not_express notes under `user-preference.expression-restriction.`
  (the one new grant). "You can swear again" REVOKES "don't swear" —
  deactivation with its own evidence, never a competing record.

  `/forget` invalidates by STABLE IDENTITY: every captured instruction mints a
  durable `EvidenceEventId` at capture (the intent path stores no Message row,
  so without it only text would remain), and the text route is normalized exact
  equality with ambiguity refusing to revoke anything. The one-active invariant
  is a DATABASE constraint — a nullable `ActiveSlot` under a unique index —
  not transaction intent. See §5.5 for what a `hosting-config` vote is (an
  overridable hosting default, not an enforceable deployment restriction). Ava's tastes
  (`CompanionPreference`) and the legacy persona blob are untouched —
  descriptive only, never parsed, never restriction-bearing. Nothing is ever
  inferred from sentiment, subject matter, or repetition. Results, live vs
  constructed, and blockers: `SOURCE3_RESULTS.md`.

  STILL MISSING: live capture for expression restrictions and for
  bare-phrasing commands (both cognition-layer, deliberately not simulated
  with routing regexes); a live `stored-message` evidence producer.
- Sources 4–5 (mood/relationship/working-context, world/vision/embodiment)
  — NOT STARTED.

**Remaining roadmap:**
- Corpus freeze only after native V3 plans exist and pass shadow validation.
- P6 — Run-2 training on CompactV3, full run-1 discipline, gates from §10.
- P7 — cross-protocol shadow/canary (run-1c/plan2 vs run-2/plan3) and the
  promotion decision.
