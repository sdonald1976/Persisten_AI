# Source 4a — working-context register contribution (declared before execution)

Declared 2026-08-25, before the contributor ran. Its own commit.

## Authority, in full

One dimension: **verbosity**. Two moves: `ConfirmsClaim` and `Correction`, both
→ `short`. Every other `ConversationMove` votes nothing. Source id is the
registered `working-context-register` (zero item grants, no restriction
authority); reason code `working-context.move`, which ranks LAST but one in
§5.4 precedence — below persona, user-preference, relationship and mood.

## Forbidden inputs, enforced by construction

The constructor takes `traceId`, `Move`, `ResolutionConfidence?`, and
`ReferentSourceMessageId?`. It never receives `InterpretationNote`, `Topic`,
`SalientEntities`, `ReferenceMarkers`, `RawQuery`, `RetrievalQuery`,
`BoundQuestion`, or `ResolvedReference`. There is no prose in scope to parse.

## Two suppression rules

- **Guess suppresses everything.** A `ResolutionConfidence.Guess` means the turn
  does not know what the user referred to; its reading of what kind of turn this
  is does not get authority either. Whole contribution withheld.
- **Turn-local validity expires automatically.** The contributor is built for one
  trace id and returns empty for any other, so a stale reading cannot leak into a
  later turn.

## Declared cases: 10

| # | case | expected |
|---|---|---|
| 1 | `ConfirmsClaim` | one verbosity=short vote, reason `working-context.move` |
| 2 | `Correction` | one verbosity=short vote |
| 3 | `NewTopic` / `ContinuesThread` / `AnswersOpenQuestion` / `ResolvesReference` | no vote |
| 4 | `Guess` resolution + a voting move | **no vote at all**; outcome `suppressed-guess` |
| 5 | `Exact` / `Unambiguous` / null resolution + a voting move | vote proceeds |
| 6 | wrong trace id | no vote; outcome `expired-different-turn` |
| 7 | mixed-dimension conflict: working-context verbosity vs user-preference verbosity | user-preference wins (§5.4), working-context recorded as loser |
| 8 | mixed dimensions survive independently: warmth + bluntness + verbosity from three sources | all three resolve, no cross-talk |
| 9 | contributor cannot restrict | a restrictive vote from this source is a recorded violation |
| 10 | live turn through the real shadow call site | native row carries the verbosity decision naming `working-context-register`; no prose in the row |

## Pass criteria

1. Only verbosity is ever voted; no item is ever produced.
2. Prose fields are not constructor-reachable (asserted by reflection over the
   constructor's parameter types).
3. `Guess` contributes nothing.
4. Turn-local validity expires on a foreign trace id.
5. Mixed dimensions resolve independently; conflicts follow frozen precedence
   with the loser recorded.
6. Diagnostics are content-safe: source, dimension, winner, loser, reason —
   no text.
7. Live evidence through the real native-shadow call site.
8. V2/Run-1c/routing/displayed output unchanged; suite green.
