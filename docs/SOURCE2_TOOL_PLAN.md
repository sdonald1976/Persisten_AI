# Source 2 — declared scenarios and pass criteria (before execution)

Declared 2026-08-24, BEFORE any run. Fixed in advance; outcomes reported against
these whatever they are.

## Inspection finding

`ToolLoop.Outcome` carries `IReadOnlyList<ToolCallTrace>` plus `ResultsSection`
(the prose handed to the production prompt). `ToolCallTrace` keeps only a
CLIPPED JSON summary of what the model saw — it is a display artifact, not the
typed execution result. `ToolResult` (Ok / Code / Data) is the typed value and
exists only inside the loop before prose conversion.

Therefore: a new typed `ToolExecutionOutcome` is captured AT EXECUTION TIME and
carried on `Outcome` as an additive field. Nothing parses `ResultsSection`,
rendered JSON, or prompt prose. The change to `ToolLoop` is additive only — no
existing field, order, prose, or behavior changes.

## Planned scenarios: 13

| # | scenario | expected disposition | expected native effect |
|---|---|---|---|
| 1 | successful lookup the user asked for | `must_express` | one must_express claim item |
| 2 | successful but irrelevant result | `background_only` | one background item |
| 3 | authorization refusal | `withheld` | no item from `tool`; a `tool-authorization` withholding note |
| 4 | execution failure (provider_failure) | `may_express` | safe failure acknowledgment, no success claim, no stack trace |
| 5 | timeout | `may_express` | safe failure acknowledgment naming timeout only |
| 6 | cancellation | `background_only` | no claim of either success or failure |
| 7 | secret-bearing result | `withheld` | zero items; secret text absent from every persisted field |
| 8 | malicious instruction-shaped result | `background_only` | background item only; no policy/register/restriction change |
| 9 | multiple calls, mixed outcomes | mixed | each call independently attributable by stable call id |
| 10 | permitted for one recipient, not another | `must_express` | renders for the authorized recipient, refused for the other |
| 11 | volatile result | `background_only` + volatile retention | persisted metadata, content withheld |
| 12 | duplicate / retried call (same call id) | as first | exactly one contribution, one row version bump |
| 13 | contributor/store failure | n/a | content-safe diagnostic, native row for other sources unaffected |

## Pass criteria (all must hold)

1. **No prose parsing** — the contributor's only input is typed
   `ToolExecutionOutcome`; a test asserts it never reads `ResultsSection`.
2. **Authority separation** — no `tool` item reaches `must_express` without an
   authorized planner disposition; `PlanningPromotion` alone is insufficient
   when authorization, disclosure, recipient, or retention forbid it.
3. **No success from failure** — failed/timed-out/cancelled calls produce no
   claim of success, and no stack trace or exception text appears in any item
   or persisted field.
4. **Adversarial inertness** — the instruction-shaped result changes no policy,
   no register value, no restriction family, never becomes must_express, never
   escapes background, and never appears in a training-eligible field.
5. **Independent attribution** — with multiple calls, each contribution names
   its own stable call id and tool identity.
6. **Recipient enforcement** — a result authorized for one principal is refused
   for another through `ValidateForAudience`, as an obligation error not a
   silent drop.
7. **Idempotency** — a duplicated call id yields one contribution and one
   store version increment.
8. **Privacy** — secret-bearing results contribute nothing and appear in no
   persisted field; volatile results persist metadata with `ContentWithheld`.
9. **`/forget`** — sweeping an excerpt of tool content removes the row.
10. **Labeling** — simulated rows labeled `simulated`; no natural row mislabeled.
11. **Isolation** — Messages, Conversations, SemanticMemories, Procedures all
    unchanged; no displayed reply, packet, or Run-1c behavior altered.
12. **Suite green.**
