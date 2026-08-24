# ResponsePlan v3 — specification (revision 2, 2026-08-24)

Status: FINAL CONTRACT CANDIDATE, incorporating Scott's second review (six
issues; reconciliation table §13 covers all sixteen resolutions to date).
Production implementation remains unauthorized; run-1c consumes byte-identical
CompactV2 for its entire tenure; no existing hash, fixture, adapter, or freeze
manifest is affected. Audit: `RESPONSE_PLAN_V3_AUDIT.md`. Wire contract:
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
