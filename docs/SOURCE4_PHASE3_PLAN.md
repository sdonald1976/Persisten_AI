# Source 4c — familiarity register contribution (declared before execution)

Declared 2026-08-25. Its own commit.

## What is integrated, and what is explicitly excluded

**Integrated:** `FamiliaritySnapshot.Stage` only — derived from days known and
user-message count, taking the LOWER of the two reads, so neither tenure without
conversation nor volume without time can fake a history.

**Excluded, and stated as blocked rather than skipped:** `EmotionalSignal`
sentiment, `RelationshipSnapshot.AverageValence` / `RecentMood` / `Trend` /
`RecentEmotion` / `RecentTopic`, and every derived claim about how the user
feels. These stay out of register voting until expiry, confidence, correction
and privacy semantics exist for them. The contributor takes `FamiliaritySnapshot`
and therefore cannot reach them.

## Familiarity is not intimacy

A long interaction history is evidence that conversations happened. It is not
evidence of affection, trust, consent, flirtation permission, or ownership. The
mapping is built so it cannot be read that way: it **only restrains, never
grants**.

| stage | vote |
|---|---|
| `New` | `verbosity = short`, `teasing = off` |
| `Acquainted` | **nothing** |
| `Familiar` | **nothing** |
| `Close` | **nothing** |

Tenure earns no register concession. `Close` does not turn warmth up, unlock
teasing, or grant anything — if closeness exists, it reaches the register
through persona and explicit user preferences, which carry the user's actual
instructions. This also answers the inspection's concern that familiarity only
ever moves forward: a stage that grants nothing cannot accumulate permissions.

`verbosity = short` rather than `warmth = cool`, deliberately: brevity with
someone barely met is caution, whereas a coldness vote would be a claim about
the relationship. `teasing = off` matches the canonical default; voting it
explicitly makes the restraint auditable rather than incidental.

## Declared cases: 9

| # | case | expected |
|---|---|---|
| 1 | `New` | two votes: `verbosity=short`, `teasing=off`, reason `relationship.familiarity-stage` |
| 2 | `Acquainted` / `Familiar` / `Close` | **no votes at all** |
| 3 | `Close` grants nothing | warmth, teasing, playfulness all remain canonical defaults |
| 4 | familiarity vs an explicit user preference on verbosity | preference wins (§5.4), familiarity recorded as loser |
| 5 | familiarity vs working-context on verbosity | familiarity wins (rank 6 vs 8), working-context recorded as loser |
| 6 | familiarity cannot produce an item | zero item grants; forged attempt rejected |
| 7 | familiarity cannot restrict | recorded violation |
| 8 | no EmotionalSignal input is reachable | constructor takes `FamiliaritySnapshot` only (reflection) |
| 9 | live turn through the real shadow call site | a new relationship's native row carries the familiarity decision; no relationship prose in the row |

## Pass criteria

1. Only `New` votes; later stages contribute nothing.
2. No warmth, playfulness, or affection-adjacent dimension is ever voted.
3. Sentiment and user-emotion types are unreachable by construction.
4. Precedence: loses to user-preference, beats working-context, losers recorded.
5. No items, no restrictions.
6. Diagnostics content-safe; evidence ref is the two counts, not prose.
7. Live evidence through the real native-shadow call site.
8. V2/Run-1c/routing/displayed output unchanged; suite green.
