# Source 3 — results against the pre-declared plan

Run 2026-08-24 against `SOURCE3_PREFERENCE_PLAN.md` (frozen before
implementation) and the ten binding amendments. Suite: **1309 passing** in
`Companion.Tests` (25 of them Source 3) plus **31** prototype goldens including
the 804-plan corpus. Zero failures.

## What was built

| piece | file |
|---|---|
| the record (id, dimension, value, scope, evidence, lifecycle) | `src/Companion.Core/Domain/UserPreferenceRecord.cs` |
| the store (supersede/revoke as insert-and-link) | `src/Companion.Infrastructure/Persistence/UserPreferenceStore.cs` + additive migration `AddUserPreferences` |
| the closed command interpreter | `src/Companion.Core/Services/PreferenceCommands.cs` |
| the pure resolver | `src/Companion.Core/Services/UserPreferenceResolution.cs` |
| the two contributors (user / hosting — separate authorities) | `src/Companion.Core/PlanV3/PreferenceContributors.cs` |
| capture at the real path | `Agent.AdjustStyleAsync` |
| `/forget` invalidation at the real path | `MemoryCurator.ForgetAsync` |
| shadow integration | the generalized native-V3 assembly in `Companion.RespondAsync` |
| acceptance evidence | `tests/Companion.Tests/UserPreferenceSourceTests.cs` |

## The amendments, one by one

1. **Revocation deactivates; newest explicit statement wins.** "From now on,
   you can swear again" marks the standing "don't swear" `Revoked` with the
   revocation's own evidence and creates nothing (scenario 1, live). A new
   statement for an occupied slot supersedes the old in the same transaction, so
   exactly one record per (kind, scope, dimension) is ever active (scenario 2,
   live). No restrictive-beats-non-restrictive rule exists anywhere.
2. **Separate authorities.** Hosting restrictions live in configuration
   (`CompanionOptions.HostingRegisterRestrictions`), never in the user store,
   and vote through their own contributor under `hosting-config.*` with a
   config-path evidence reference. The assembler's register decision names the
   winning source, its reason code, and the losers — which authority won and
   why (scenarios 3, 4).
3. **Two mechanisms.** Register preferences are VOTES; expression restrictions
   are `must_not_express` NOTE items under
   `user-preference.expression-restriction.` (one new grant — the only registry
   change). Criterion-5 test proves a mixed set produces one vote and one item,
   with no crossover.
4. **Record shape.** All required fields present; `Scope` is `"global"` only —
   no other scope exists to be invented.
5. **Explicit only.** Capture happens solely inside `AdjustStyleAsync` via
   `PreferenceCommands.Interpret`, a closed pattern list. Routing untouched: a
   directive the interpreter declines gets the legacy blob line exactly as
   before and NO record. Sexual content and annoyance (scenarios 5, 6 — live
   chat turns) leave the store byte-empty; there is no code path from
   sentiment, subject matter, repetition, or Ava's tastes to the table.
6. **Evidence resolvable, diagnostics text-free, `/forget` wired.** Live
   capture happens on intent-path turns which store no Message row, so the
   record itself is the system of record (`EvidenceKind=direct-instruction`,
   verbatim statement on the record). Decisions, resolver reports, and shadow
   rows carry only the record id — asserted by serialization in criteria 3 and
   7, and end-to-end in scenario 14 ("don't swear" appears nowhere in the
   persisted row). The real `MemoryCurator.ForgetAsync` now also invalidates
   any active preference whose evidence message or statement matches the
   forgotten memory's evidence (mutual-contains, ≥12 chars): status becomes
   `EvidenceForgotten` and the statement is PURGED (scenario 8, live).
7. **Ava's tastes untouched.** `CompanionPreference` unchanged; scenario 7
   forms a real dislike through `IPreferenceStore` and the user store stays
   empty.
8. **Legacy blob untouched.** No parsing, no migration, append behavior
   byte-identical (all PersonaAndFeedback tests green; captured commands still
   append their blob line exactly as before, so production prompts for those
   directives are unchanged from what they already were).
9. **Pure resolution.** `UserPreferenceResolution.Resolve` — no I/O, no clock;
   order-independent (asserted); reports winner id, losers, reason token,
   scope, dimension, value; carries NO Subject and no statement, so it is safe
   to serialize into telemetry. Cross-authority precedence stays in the
   assembler, the one place the contract orders it.
10. **All seven named cases** are scenarios 1, 2, 3, 5, 6, 7, 8 — five live,
    two constructed, as pre-declared.

## One contract point, stated rather than smuggled

The inspection proposed "hosting-restrictive beats user". That was WRONG
against the frozen contract: spec §5.4 ranks `user-preference.` above
`hosting-config.`. The implementation follows the contract — scenario 3 records
the user preference winning with the hosting vote recorded as the loser. If
deployment-must-win semantics are ever wanted, that is a contract revision to
§5.4, and it is not made here.

## Live vs constructed (as pre-declared, confirmed)

- **Live** (real capture path, real `/forget`, real shadow call site):
  scenarios 1, 2, 5, 6, 7, 8, 14. Scenario 14 runs a full
  `RespondAsync` turn after a real "from now on, don't swear" capture: the
  native row's register line reads `profanity=forbidden`, the register decision
  names `user-preference` / `user-preference.profanity`, zero authority
  violations, and the statement text appears nowhere in the row.
- **Constructed**: 3, 4 (hosting is configuration, not conversation), 9, 10,
  12, 13 (adversarial contributors), 11 (expression restrictions have no live
  capture path — see blockers).

## Behavior changes outside the shadow

Three, all deliberate, none touching V2 plans, Run-1c, or routing:

1. `AdjustStyleAsync` writes a structured record when (and only when) the
   closed interpreter recognizes the directive, and answers a recognized
   revocation with a lifted-rule confirmation instead of the generic echo. The
   blob append is unchanged in both cases.
2. `MemoryCurator.ForgetAsync` now reads a memory's evidence unconditionally
   (it previously read it only when the shadow recorder was on) and invalidates
   dependent preferences. Same transaction discipline, additive.
3. The native-V3 assembly block accepts a contributor list (tools +
   preferences + hosting) instead of tools only. Shadow-gated as before; the
   `plan.native-v3.tools` decision record now appears only when an assembly
   actually ran.

## The interpreter's closed list (complete)

profanity→forbidden (restrictive), profanity revocation, profanity→mirror-only,
verbosity→short, verbosity→expansive, warmth→warm. Six patterns, each one
reading. Everything else — including "talk like a pirate", "be nicer about my
cooking", and "that swearing earlier was funny" — produces nothing durable
(tested).

## Remaining blockers

- **No live capture for expression restrictions.** "Don't bring up X" has no
  routed path; scenario 11 is constructed. Capturing it from open conversation
  is a cognition-layer job, not a regex.
- **Bare-phrasing commands stay chat turns.** "Don't swear" without "from now
  on" routes to chat and is not captured — routing was deliberately not
  touched. Same cognition-layer note.
- **The interpreter's register coverage is six patterns.** Honest floor, not a
  ceiling; widening it is cheap but each pattern is a claim and was kept to
  what the scenarios required plus the obvious.
- **`stored-message` evidence has no live producer** (intent turns store no
  Message row). The kind exists and `/forget` honors it; nothing writes it yet.
- Sources 4–5 untouched. Source 2's blockers stand as recorded.

---

# Amendment — evidence/forget linkage repair (2026-08-25)

Conditional acceptance required replacing approximate text containment with
stable identity linkage. Done; **1325 + 31 passing**, 41 of them Source 3.

## What was wrong

`InvalidateByForgottenEvidenceAsync` matched a forgotten excerpt against a
preference's statement by MUTUAL CONTAINMENT with a 12-character floor. Any
forgotten sentence that contained a standing instruction — or was contained by
it — silently destroyed that preference's authority. Verified against the old
rule, these three all revoked a `profanity=forbidden` rule that had nothing to
do with the memory being forgotten:

| forgotten excerpt | why it hit |
|---|---|
| `don't swear when we talk` | fragment of the instruction |
| `from now on, don't swear` | opening clause of the instruction |
| `from now on, don't swear when we talk about work` | superstring of it |

The first two are the realistic shape: an ordinary memory whose evidence excerpt
is part of the same sentence the instruction was stated in.

## The repair

**Durable evidence event at capture (req. 2).** `EvidenceEventId` is minted in
`Agent.AdjustStyleAsync` for every captured instruction, whether or not a
Message row exists — the intent path creates none, which is precisely why text
was doing the work before. `InvalidateByEvidenceEventAsync` invalidates by that
id with no text involved at all.

**Exact identity only (req. 1, 3).** Text matching is now normalized EXACT
EQUALITY (trim + ordinal-ignore-case), never containment. Candidates are
resolved separately, and a statement matching more than one active record is
AMBIGUOUS: it revokes nothing, the count is returned in
`PreferenceInvalidationResult.Ambiguous`, and `MemoryCurator` logs a warning
telling the user to revoke explicitly. Choosing one of two identical
instructions would be a coin flip, and a coin flip that silently drops a
standing rule is the worst available outcome.

**Adversarial coverage (req. 4).** Eight overlapping-phrase cases (the three
verified old-rule failures plus short phrases, single words, and a common
opener) assert zero invalidations and zero ambiguity; two exact-match cases
assert invalidation still works, including whitespace/casing normalization;
identical statements from different events assert 0 invalidated / 1 ambiguous
with both records surviving, then assert that forgetting one of them by its own
event id takes exactly that one; and id-linkage is asserted exact even when a
text collision is present alongside it.

**One-active invariant in the database (req. 5).** New nullable `ActiveSlot`
column — `kind|scope|dimension[|subject]` while Active, NULL the moment the
record is superseded, revoked, or evidence-forgotten — under a UNIQUE index on
`(UserId, ActiveSlot)`. SQL treats NULLs as distinct, so history is
unconstrained while a second active row for one slot is impossible. Two
independent DbContexts racing the same slot are tested: the loser gets a
`DbUpdateException`, and exactly one active record survives. The invariant no
longer rests on transaction intent.

**Spec §5.5 (req. 6).** New section stating that a `hosting-config` register
vote is a hosting DEFAULT, not an enforceable deployment restriction — the §5.4
precedence is unchanged and NOT silently altered. It records that `restrictive:
true` marks a value as forbidding rather than shaping, and does **not** mark it
unoverridable, which is the confusion worth naming; and that no enforceable
deployment-restriction mechanism exists in the contract today, so an operator
obligation that must hold regardless of the user has no home in the register and
must not be simulated by re-ranking families. `CompanionOptions` carries the
same warning at the configuration site.

## Scope

Legacy persona blob untouched (`UserProfile`, `IProfileStore`,
`PersonalityService` have zero diff). V2, Run-1c, routing, and displayed output
unchanged. The migration was regenerated before ever being applied anywhere, so
it remains a single additive `CreateTable` plus its indexes.

## Blocker closed, blocker remaining

`stored-message` evidence still has no live producer, but it is no longer the
only exact handle: `EvidenceEventId` gives every captured instruction a durable
identity today. Remaining Source 3 blockers are unchanged — no live capture for
expression restrictions or bare-phrasing commands, both cognition-layer.
