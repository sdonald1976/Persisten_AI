# Source 3 — inspection, and a STOP

Inspection of the real persona and preference storage/resolution paths, done
before any contributor was written. **The stop condition fires.** All three
things the instruction named — stable preference identity, precedence rule,
revocation/update semantics — are absent. No contributor was built; the missing
layer is proposed below and awaits approval.

## 1. Two different things are called "preference", and neither is the one Source 3 needs

`CompanionPreference` / `IPreferenceStore` / the `preference.list` tool are
**Ava's own tastes** — "Ava has a moderately positive opinion of Alien". The
domain comment is explicit that the user liking something never writes there;
tastes form only through her own reflection, gradually, so a casual remark
cannot overwrite an established one. That separation is what lets her honestly
disagree, and Source 3 must not disturb it.

It is a **persona-descriptive** source. It must never produce a restriction.

**There is no user-owned preference store at all.** The full table list confirms
it: `CompanionPreferences` (hers) and `Users` (the profile). Nothing else.

## 2. What actually holds a user's standing preferences: one free-text blob

`UserProfile.Persona`, a nullable string. The only conversational writer is
`Agent.AdjustStyleAsync`, which does this:

```csharp
var line = "- " + char.ToUpper(directive[0], …) + directive[1..].TrimEnd('.') + ".";
var persona = string.IsNullOrWhiteSpace(profile.Persona) ? line : profile.Persona!.TrimEnd() + "\n" + line;
await _profiles.SetPersonaAsync(userId, persona, ct);
```

The user's raw sentence, bulleted, appended to a blob. `PersonalityService.Compose`
then pastes the whole blob into the system prompt under "Extra style the user
asked for:".

That is the entire user-preference subsystem.

### 2a. Stable preference identity — ABSENT

A preference is a line in a string. No id, no key, no dimension, no timestamp,
no source. Two preferences cannot be told apart, addressed, or counted. Nothing
can cite one.

This matters immediately: the V3 registry already requires evidence for the
`user-preference.` family (`PlanV3Assembler.EvidenceRequired`), and a proposal
carrying that reason code without a `provenance.evidenceRef` is rejected
`authority-claimed-without-evidence`. **There is nothing in the system to put in
that field.** The authority model is already correct and already stricter than
the data layer can satisfy.

### 2b. Precedence rule — ABSENT

Contradictory lines accumulate in arrival order and are handed to the model as
prose. "- Be more concise." and a later "- Give me more detail." both sit in the
prompt; nothing resolves them, nothing records which won, and the model is not
even told that later should beat earlier. Arrival order is a side effect of
string concatenation, not a rule.

### 2c. Revocation / update semantics — ABSENT

There is no remove operation. `SetPersonaAsync(null)` clears the **entire** blob,
so the only available revocation is total amnesia about every preference at once.
"Actually, you can swear again" appends a third line while the prohibition stays
in the prompt forever.

This is the most dangerous of the three. A restriction that cannot be lifted is
worse than one that was never recorded.

## 3. Capture is phrase-triggered and stores raw prose

`RuleBasedIntentParser.TryStyle` fires on five regexes over sentence *shape*:
`be more/less …`, `talk like …`, `from now on, …`, `keep it …`, and `be <adjective>`
against a fixed adjective list. Three consequences:

- **Shape, not meaning.** "From now on, call me Scott" is captured as a *style*
  line, because it matches `^from now on`.
- **Raw prose reaches the prompt.** The stored text is the user's own sentence,
  pasted verbatim into the system prompt. The echo law: prose given to a model
  gets spoken by a model.
- **Everything else is invisible.** A preference stated in any other shape is
  never captured at all.

Widening these regexes is not the fix, and per the Source 2 ruling it is exactly
the move to avoid — more phrase-specific rules simulating cognition.

## 4. No restriction concept exists in production

`profanity` appears nowhere in the codebase outside the V3 register vector.
Hosting configuration is `PersonalityOptions`, which has exactly one field: the
default preset name. So there is no lawful producer of a `hosting-config.*`
restriction either, and nothing in production currently restricts anything on
grounds of subject matter — which is the correct starting state.

## 5. The V3 register still has no production producer

`PlanV3Builder` sets only act-derived verbosity and leaves every other dimension
at canonical default, with a comment recording that parsing v2 tone prose to
recover structure is banned. Source 3 would therefore be the **first real
register producer** in the system. That raises the stakes on getting identity,
precedence and evidence right before anything votes.

## 6. Why I did not build the contributor anyway

Each gap independently blocks it, and the workaround for each is a banned move:

- Without identity, the `user-preference.` grant cannot be satisfied — and
  faking an `EvidenceRef` to get past the assembler would defeat the one check
  that makes the family safe.
- Without precedence, `ResolveRegister` has nothing principled to resolve.
- Without revocation, any restriction the contributor emits is permanent.
- Recovering any of the three inside the contributor means **parsing the persona
  blob's prose into structure**, which is the move the whole V3 design exists to
  prevent.

## 7. Proposed layer — `UserPreference` (NOT IMPLEMENTED, awaiting approval)

A real store, a sibling of the profile rather than a field inside it.

| field | purpose |
|---|---|
| `Id` (Guid) | stable identity; this is what `provenance.evidenceRef` cites |
| `UserId` | isolation root, like every store |
| `Dimension` | closed set, aligned to the register where one exists: `warmth, bluntness, playfulness, teasing, skepticism, intensity, verbosity, profanity, mirror`, plus non-register `address-name`, `question-frequency` |
| `Value` | from that dimension's closed value set — never free text |
| `Restrictive` | whether it forbids rather than shapes |
| `Source` | `stated-by-user` \| `hosting-config`. No third option. Nothing is ever `inferred` |
| `EvidenceMessageId` + `EvidenceExcerpt` | the actual message where it was stated, bounded |
| `StatedAt`, `UpdatedAt` | |
| `Status` | `active` \| `superseded` \| `revoked` |
| `SupersededById` | update inserts a new row and marks the old one superseded — history survives |
| `RevokedAt`, `RevocationEvidenceMessageId` | revocation carries its own evidence |

**Precedence, declared rather than emergent:**

1. `hosting-config` restrictive beats everything.
2. A user restrictive preference beats a user non-restrictive one.
3. Among equals, the most recent `StatedAt` wins.
4. Losers are RECORDED as losers — the assembler's `ResolveRegister` already
   does exactly this for register votes, so this plugs into existing machinery.

**Revocation:** update = insert + supersede; revoke = mark revoked with its own
evidence, effective immediately and gone from the prompt. Nothing is ever hard
deleted; `/forget` sweeps by excerpt like every other store.

**Capture:** only an explicit statement creates a row, and the originating
message id is recorded at creation. **Never inferred from silence, sentiment, or
subject matter.** No preference is created because a topic is sexual, because a
reply seemed to land badly, or because the user went quiet. Sexual content and
profanity stay ordinary content; only an active, evidence-bearing `profanity`
preference or an explicit hosting configuration restricts them.

**Migration:** the existing `UserProfile.Persona` blob **stays as descriptive
persona** — a style tweak layered on a preset is a legitimate thing for it to
hold — and is **not** parsed into preferences. Old lines keep working as prompt
text; new explicit preferences go to the new store. The blob shrinks by attrition
rather than by a prose-parsing migration.

**What is already right:** both capability entries exist and neither needs
changing. `persona` is `Cap("persona", "derived", [], register: true)` — votes
only, zero item grants, so it cannot put a single word into a plan.
`user-preference` is `Cap("user-preference", "told-by-user", [], register: true,
restrictions: true)` with the evidence requirement already enforced. The
authority model is correct; only the data layer is missing.

## 8. Pre-declared scenarios and coverage (frozen now, to run after approval)

Fixed here so they cannot be adjusted to match whatever happens.

| # | scenario | expected |
|---|---|---|
| 1 | descriptive persona trait (preset + blob) | register votes only; zero items; no restriction |
| 2 | Ava's own taste (`CompanionPreference`) | never produces a restriction; never becomes a user preference |
| 3 | explicit user preference, non-restrictive | register vote carrying its preference id as evidence |
| 4 | explicit user preference, restrictive | restriction recorded with owner and evidence |
| 5 | restriction proposed with NO evidence | rejected `authority-claimed-without-evidence` |
| 6 | two contradictory active preferences | declared precedence resolves; loser recorded, not dropped |
| 7 | superseded preference | no longer votes; history retained |
| 8 | revoked preference | stops applying immediately; absent from prompt and plan |
| 9 | hosting-config restriction vs user preference | hosting restrictive wins; both recorded |
| 10 | sexual subject matter, no stored preference | **no restriction** — ordinary content |
| 11 | profanity, no stored preference | **no restriction** — ordinary content |
| 12 | user annoyance / silence / negative feedback | **no preference created**, no restriction inferred |
| 13 | persona source attempts a restriction | rejected — `persona` holds no restriction authority |
| 14 | preference store failure | content-safe diagnostic; other sources unaffected |

Coverage: real vs constructed labeled per scenario as in Source 2; integration
through the real shadow call site; V2, Run-1c and displayed routing untouched.

## 9. Recommendation

Approve or amend the `UserPreference` layer in §7 before Source 3 proceeds.
Building the contributor first would mean inventing identity, precedence and
revocation inside it — which is how those semantics end up implicit, untestable,
and impossible to revoke.

Nothing in this inspection changed any code. Source 2's blockers stand as
recorded: three absent upstream tool producers, and no general typed expression
decision beyond the deterministic nudge tier.
