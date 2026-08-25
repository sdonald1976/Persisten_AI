# Source 1b — declared session plan and pass criteria (before execution)

Declared 2026-08-24, BEFORE any volume run. These counts and criteria are fixed;
outcomes are reported against them whatever they turn out to be.

## Planned LifeRunner sessions: 12 sessions / 11 scenarios

| # | scenario | sessions | turns (approx) | must end as |
|---|---|---|---|---|
| 1 | correct guess (captured proposals, ends "a dildo") | 1 | 6 Q + guess + verdict | Completed, FinalGuessCorrect true |
| 2 | incorrect guess then continue | 1 | 3 Q + guess + verdict | Active, guess cleared |
| 3 | exhausted question limit | 1 | limit 4, 5 selections | Completed, `question-limit-exhausted` |
| 4 | answer correction | 1 | 3 Q + 1 correction | Active, corrected binding only |
| 5 | malformed answer | 1 | 2 Q + 1 malformed | Active, number unchanged |
| 6 | abandonment | 1 | 2 Q + abandon | Abandoned |
| 7 | retry / idempotency | 1 | 2 Q, one delivered twice | Active, single binding |
| 8 | restart / resume (durable retention) | 1 | 3 Q, reload from store | resumed with content |
| 9 | volatile no-resume | 1 | 3 Q, reload from store | resumed WITHOUT content, `ContentWithheld` |
| 10 | two simultaneous users | 2 | 3 Q each | both Active, no cross-contamination |
| 11 | deterministic fallback (no proposer) | 1 | 3 Q | Active, all moves `Deterministic` |

## Pass criteria (all must hold)

1. **Path identity** — every simulated row is produced by the same activation →
   runtime → assembler → recorder → store path natural turns use. No test-only
   shortcut writes a row.
2. **Labeling** — every simulated row carries `label = simulated`,
   `branchKind = Simulated`, and every move disposition is
   `simulated_displayed`. Zero simulated rows labeled natural.
3. **Counterfactual separation** — zero simulated or natural rows in which a
   `counterfactual_not_displayed` move has a bound answer.
4. **Lifecycle correctness** — each scenario reaches exactly the terminal state
   in the table above.
5. **Idempotency** — scenario 7's duplicated delivery produces exactly one
   answer binding and one store row version increment.
6. **Retention** — scenario 9 persists metadata with `ContentWithheld = true`
   and no moves/hypotheses/guess/activation text; scenario 8 persists content.
7. **Isolation** — after every scenario: zero rows added to Messages,
   Conversations, SemanticMemories, EpisodicMemories, or Procedures.
8. **Determinism** — scenario 11 and all captured-proposal scenarios reproduce
   identical move sequences on a second run.
9. **Suite** — the complete test suite stays green.

## Natural-turn call-site criteria (separate from volume)

- Snapshot is enqueued only AFTER the displayed response is finalized; the
  displayed response is never delayed (enqueue is `TryWrite`, drop-counted).
- The renderer that actually spoke is recorded on the observed move.
- Displayed-question identification is CONSERVATIVE: exactly one question in
  the displayed reply → resolved; zero, multiple, or ambiguous → recorded as
  `displayed-move-unresolved`, and no identity is invented.
- Natural input binds only when exactly one displayed move was unambiguously
  identified.
- ProductionObserved and CounterfactualNative branches are separate rows with
  distinct branch ids; the counterfactual names its parent and branch point.
- Every runtime/store/assembler failure is content-safe and user-invisible.
