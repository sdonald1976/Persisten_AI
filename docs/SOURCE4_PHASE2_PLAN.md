# Source 4b — mood, amended design (declared before implementation)

Declared 2026-08-25. Amends the inspection's §4b.7 proposal, which was rejected
on one point: a hash of mutable current state is not provenance.

## What this source is, and is not

**Is:** `CompanionStateSnapshot` — Ava's own spirits and energy.
**Is not:** `MoodReading`, `MoodDetector`, `EmotionalSignal`, or any inference
about how the USER feels. Two unrelated things wear the name "mood" here; this
contributor's constructor takes `CompanionStateSnapshot` and therefore cannot
reach the other one.

## StateRef: a transition event, not a hash

The inspection proposed a deterministic id derived from `(userId, nudgedAt)`.
Rejected, and correctly: it identifies nothing, resolves to nothing, and changes
the moment the value does.

Replaced by **`CompanionMoodTransitions`** — an append-only, versioned log. Each
nudge writes a row: `Id` (the StateRef), `UserId`, `Version` (monotonic, unique
per user), `PreviousSpirits`, `NewSpirits`, `AppliedValence`, `OccurredAt`.
`CompanionStateSnapshot.StateRef` is the newest row's id and resolves in that
table forever. **No transition, no vote** — an unciteable mood has no standing.

`AppliedValence` is a number, never the cue or the user's words.

## Bounded typed mappings

| signal | dimension | mapping |
|---|---|---|
| spirits | **intensity** | `<= -0.3` → `flat`; `>= 0.3` → `raised`; otherwise **no vote** |
| energy | **none, for now** | declared empty — see below |

Deliberately **not warmth**. Warmth toward the user is a persona and
relationship concern; sourcing it from her spirits would make her low mood the
user's problem, which her own state contract explicitly forbids.

**Energy votes nothing in this phase.** It is derived fresh from the clock and
has no transition event, so it has no StateRef to cite. Rather than invent
provenance for it or let one contributor mix two evidence kinds, its dimension
set is declared empty until a resolvable provenance decision is made. Recorded
as a blocker, not quietly skipped.

## The floor

`MoodContributor.Floor = 0.3`. Below it, **silence** — not a "neutral" vote,
which would still displace a lower-ranked source. Decay carries every mood back
under the floor on its own (4-day half-life), so a moment that once moved her
stops modulating her without anything having to expire it.

## Modulation only

Source `mood` has zero item grants and no restriction authority. It cannot make
a claim, restrict expression, imply consent, create a preference, or produce a
recitable explanation. §5.4 ranks it below persona, user-preference and
relationship, so it can never overrule any of them.

## Declared cases: 12

| # | case | expected |
|---|---|---|
| 1 | spirits `<= -0.3` with a StateRef | one `intensity=flat` vote citing the transition id |
| 2 | spirits `>= 0.3` with a StateRef | one `intensity=raised` vote |
| 3 | spirits inside the floor | **no vote**; outcome `below-floor` |
| 4 | no StateRef (mood never moved) | **no vote**; outcome `no-state-ref` |
| 5 | mood vs a user preference on the same dimension | preference wins (§5.4); mood recorded as loser |
| 6 | mood attempts an item | impossible — zero item grants; a forged attempt is rejected |
| 7 | mood attempts a restrictive vote | recorded violation |
| 8 | StateRef resolves | the cited id is findable in `CompanionMoodTransitions` |
| 9 | **concurrent nudges** | every nudge lands; versions unique and contiguous; results compose rather than clobber |
| 10 | **deterministic replay** | replaying the logged valences from the logged start reproduces the final spirits exactly |
| 11 | decay carries mood under the floor | a once-voting mood stops voting with only time passing |
| 12 | live turn through the real shadow call site | native row carries the mood decision, no prose |

## Pass criteria

1. The contributor cannot reach user-emotion types (constructor asserted).
2. No StateRef → no vote; the StateRef resolves durably.
3. Only `intensity` is voted; only from spirits; energy votes nothing.
4. The floor produces silence, not a neutral vote.
5. Concurrent nudges compose, with unique contiguous versions.
6. Replay from the log is deterministic.
7. Mood loses to persona/user-preference by contract, loser recorded.
8. Diagnostics carry source/dimension/winner/loser/reason and no prose.
9. Live evidence through the real native-shadow call site.
10. V2/Run-1c/routing/displayed output unchanged; suite green.
