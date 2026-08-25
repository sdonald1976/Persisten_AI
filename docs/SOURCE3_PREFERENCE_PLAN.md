# Source 3 — declared design, scenarios, and pass criteria (before execution)

Declared 2026-08-24, BEFORE implementation runs. Fixed in advance; outcomes are
reported against these whatever they are. Amendments 1–10 from the acceptance of
`SOURCE3_PERSONA_INSPECTION.md` are binding and cited by number below.

## Design (per the amendments)

**Two record kinds, one store, separate mechanisms (am. 3).**
`UserPreferenceRecord.Kind` is `Register` or `ExpressionRestriction`. Register
preferences carry a closed dimension and a closed value from the plan/3 schema
(`warmth: cold|cool|plain|warm|tender`, …, `profanity: unrestricted|mirror-only|
encouraged|neutral|avoid|forbidden`). Expression restrictions carry a `Subject`
and no register dimension. They do not share a value vocabulary, a resolver
branch, or a contribution shape: register preferences become register VOTES;
expression restrictions become `must_not_express` NOTE items.

**Record shape (am. 4).** Stable `Id` (Guid — this is what
`provenance.evidenceRef` cites), `UserId`, `Kind`, `Dimension`/`Subject`,
structured `Value`, `Restrictive`, `Scope` (only `global` exists; no other scope
is invented), `StatedAt` (effective time), `Status`
(`Active|Superseded|Revoked|EvidenceForgotten`), `SupersededById`, evidence
fields, revocation fields.

**Precedence within user-owned preferences (am. 1).** Explicit supersession and
revocation win outright: capture marks the prior active record for the same
scope+dimension `Superseded` when a new statement arrives, and `Revoked` when
the statement is a revocation. "You can swear again" therefore DEACTIVATES
"don't swear" — it does not create a competing preference. Among records that
are still simultaneously active for the same scope+dimension (possible only if
capture-time supersession was bypassed), the pure resolver picks the newest
`StatedAt` deterministically and reports the situation. There is no
"restrictive beats non-restrictive" rule among user preferences — the
inspection's proposed rule is dropped as amended.

**Hosting configuration is a separate authority (am. 2).** It lives in
configuration (`HostingPolicyOptions`), never in the user store, and votes
through its own contributor under `hosting-config.*` with a config-path
evidence reference. It cannot masquerade as a user preference; the assembler's
`RegisterDecisions` name the winning source and its reason code, which is the
"which authority won and why" diagnostic.

**Cross-authority precedence follows the frozen contract.** Spec §5.4 ranks
`user-preference.` ABOVE `hosting-config.` — the inspection's proposal that
hosting-restrictive beats user was wrong against the approved contract and is
dropped. The conflict scenario records the user preference winning WITH both
recorded. If deployment-must-win semantics are ever wanted, that is a contract
revision, not a resolver choice, and it is not made here.

**Resolution is a pure function (am. 9).** `UserPreferenceResolution.Resolve`
takes the active records and returns, per scope+dimension: winner id, value,
restrictive flag, loser ids, and a reason token (`single-active` /
`newest-statement`). Deterministic (ties broken by ordinal id), no I/O, no
clock, and NO preference text — ids, dimensions, closed-set tokens and status
only. The same rule appears nowhere else.

**Capture (am. 5).** Only `PreferenceCommands.Interpret`, a deterministic
closed-pattern interpreter, creates records — and only from directives that
ALREADY reach `Agent.AdjustStyleAsync` through the existing intent parser.
Routing is untouched: a bare "don't swear" is a chat turn today and stays one
(recorded as a capture blocker, not worked around with new routing rules).
"From now on, don't swear", "be concise", "from now on, give me more detail"
already route there and are captured live. Anything the interpreter does not
recognize creates NO record (the legacy blob append still happens, unchanged).
Nothing is inferred from annoyance, sentiment, sexual subject matter,
profanity use, repetition, or Ava's tastes — there is no code path from any of
those to the store.

**Evidence (am. 6).** Live capture happens on intent-path turns, which store no
`Message` row — so `EvidenceKind = direct-instruction` and the record itself is
the system of record for the verbatim command (`EvidenceStatement`).
`EvidenceKind = stored-message` with `EvidenceMessageId` exists for evidence
that lives in the message table. Diagnostics, telemetry, register decisions,
and shadow rows carry ONLY the record id — never the statement.

**`/forget` (am. 6), behavior declared here:** when `MemoryCurator.ForgetAsync`
forgets a memory, the same evidence handles it already collects (excerpts +
evidence message ids) also invalidate dependent preferences: any active record
whose `EvidenceMessageId` matches a forgotten evidence message, or whose
`EvidenceStatement` mutually contains a forgotten excerpt (case-insensitive,
either direction), becomes `EvidenceForgotten` — deactivated immediately — and
its `EvidenceStatement` is PURGED so the forgotten text does not linger in the
preference table. The skeleton row (id, dimension, status, timestamps) remains
for audit.

**Ava's tastes (am. 7).** `CompanionPreference` is untouched. No code path
reads it into the user-preference store, the resolver, or any restriction; a
test asserts the store stays empty across taste formation.

**Legacy blob (am. 8).** `UserProfile.Persona` is not parsed, not migrated, and
its append behavior is unchanged. It remains descriptive legacy prompt input.

**V3 authority.** `user-preference` gains ONE grant it lacked:
`(note, must_not_express, reasonPrefix: "user-preference.expression-restriction.",
evidence required)` — the expression-restriction shape. Its register authority
(`register: true, restrictions: true`) and the evidence requirement on the
`user-preference.` family already exist and are unchanged. `persona` keeps zero
item grants and no restriction authority.

**Shadow integration.** Contributors run in the existing native-V3 assembly in
`Companion.RespondAsync` (shadow/canary-gated, same as Source 2), reading the
store once per observed turn. Production packet, V2 plan, Run-1c, and routing
are untouched; failure to load or contribute is a content-safe diagnostic.

## Declared scenarios: 14

| # | scenario | am. | expected |
|---|---|---|---|
| 1 | "from now on, don't swear" → later "from now on, you can swear again" | 1, 10a | first: active restrictive `profanity=forbidden`; second: first becomes `Revoked`, no active profanity preference remains, register vote gone |
| 2 | "be concise" → later "from now on, give me more detail" | 1, 10b | first superseded by second; exactly one active verbosity preference (`expansive`); resolver reports the supersession |
| 3 | hosting `profanity=avoid` vs user `profanity=mirror-only` | 2, 10c | both vote; user-preference wins per frozen contract; decision names winner source, reason code, and loser |
| 4 | hosting restriction alone, no user preference | 2 | hosting wins its dimension; restriction recorded with owner `hosting-config` and config-path evidence |
| 5 | sexual conversation, no stored preference | 5, 10d | store untouched; no restriction; register unchanged |
| 6 | user annoyance / negative feedback | 5, 10e | store untouched; no restriction |
| 7 | Ava forms a dislike (`CompanionPreference`) | 7, 10f | user store untouched; no vote claims the user requested anything |
| 8 | `/forget` a memory whose evidence backs a preference | 6, 10g | preference becomes `EvidenceForgotten`, statement purged, vote gone next turn |
| 9 | register vote without evidence ref | — | rejected `authority-claimed-without-evidence`-class violation, recorded |
| 10 | `persona` source attempts a restrictive vote | 3, 7 | rejected — no restriction authority; violation recorded |
| 11 | expression restriction (constructed) | 3 | `must_not_express` note under `user-preference.expression-restriction.` with evidence; naming the subject, not quoting content |
| 12 | expression restriction without evidence (constructed) | 3, 6 | rejected |
| 13 | preference store failure at the shadow call site | — | content-safe diagnostic; turn and other sources unaffected |
| 14 | end-to-end: live turn through `RespondAsync` with an active preference | — | native row's register decision shows `user-preference` winning its dimension; no preference text anywhere in the row; reply/messages normal |

Live vs constructed, declared now: 1, 2, 5, 6, 7, 8, 14 run the REAL capture
path (`Agent.AdjustStyleAsync` / `MemoryCurator.ForgetAsync`) and/or the real
shadow call site. 3, 4, 9, 10, 11, 12, 13 construct records or contributors
directly (hosting config is configuration, not conversation; expression
restrictions have no live capture path yet — recorded as a blocker).

## Pass criteria (all must hold)

1. Explicit revocation deactivates; it never creates a competing preference.
2. Explicit supersession leaves exactly one active record per scope+dimension.
3. Resolution is pure, deterministic, and its report contains no preference
   text — asserted by serializing the report.
4. Authority separation: hosting never appears as `user-preference.*`; every
   register decision names its winning source and reason code.
5. Register preferences and expression restrictions travel different
   mechanisms (votes vs note items) — no shared dimension/value path.
6. No inference: scenarios 5–7 leave the store byte-empty.
7. Evidence: every record carries a resolvable reference; no shadow row,
   register decision, or diagnostic contains `EvidenceStatement` text.
8. `/forget` invalidates dependent preferences and purges their statements.
9. Ava's tastes never produce a user-owned record or restriction.
10. Legacy blob behavior unchanged (existing PersonaAndFeedback tests stay
    green, blob still appends verbatim).
11. Isolation: V2 plan, packet, Run-1c, routing, Messages/Conversations
    unchanged on every scenario; full suite green.
12. The end-to-end scenario produces a native row through the real call site
    with the preference decision in it.
