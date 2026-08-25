# Source 2 — results against the pre-declared plan

Run 2026-08-24 against `SOURCE2_TOOL_PLAN.md`, which fixed 13 scenarios and 12
pass criteria BEFORE anything ran. Nothing here was added to the plan after the
fact; where a result differs from what the plan predicted, the difference is
stated rather than the plan quietly amended.

Suite: **1284 passing** in `Companion.Tests` (24 of them Source 2) plus **31**
in the V3 prototype, including the 804-plan corpus golden tests. Zero failures.

## What was built

Tool results reach the native V3 plan as TYPED values captured at execution
time. Nothing parses `ResultsSection`, the rendered result JSON, or any prompt
prose — that was the whole point of the inspection finding recorded in the plan.

| piece | file |
|---|---|
| typed outcome record | `src/Companion.Core/Domain/ToolExecutionOutcome.cs` |
| capture at execution time | `src/Companion.Core/Services/ToolLoop.cs` (additive) |
| the two contributors | `src/Companion.Core/PlanV3/ToolOutcomeContributor.cs` |
| grants + reason family | `Contributors.cs`, `PlanV3Assembler.cs` |
| the real call site | `src/Companion.Core/Services/Companion.cs` |
| plan-only shadow path | `IRendererShadow.cs`, `RendererShadowService.cs` |
| acceptance evidence | `tests/Companion.Tests/ToolOutcomeSourceTests.cs` |

## Real vs constructed

The plan did not distinguish these, so it is stated now. Ten of the thirteen
scenarios run through the **real `ToolLoop`** against real `ICompanionTool`
implementations. Three construct typed outcomes directly, because the live tool
layer has no producer for those states yet:

- **6 — cancellation.** `ToolLoop` maps an expiring call to `TimedOut`; nothing
  produces `Cancelled`.
- **10 — recipient restriction.** No tool declares an authorized audience today.
- **11 — volatile retention.** No tool declares a retention class today.

These are gaps in the tool layer, not in the contribution boundary. The typed
contract has the fields; nothing upstream fills them. Sources 3–5 are untouched.

Scenario 5 (timeout) uses a tool returning `Code = "timeout"` rather than
blocking for the real 30-second limit — the mapping is exercised, the clock is
not.

## The one place expression comes from

The plan asked for a `must_express` scenario without saying what would produce
one. `ToolLoop` had hard-coded every disposition to `BackgroundOnly`, so nothing
could have reached expression at all.

The resolution: the **deterministic nudge tier** — the one place a lookup is
selected by a rule matching the USER's phrasing ("can you see images?") rather
than by the tool planner — now carries `MustExpress`. Planner-selected calls stay
`BackgroundOnly`. The tool planner is an untrusted model role; it may decide what
to look up, never that Ava must recite what came back.

Even then the tool does not grant itself expression: the item asks, and the
assembler's `(claim, must_express, promotable)` tuple grants it as a **recorded
promotion**.

## Measured tallies (`TheDeclaredVolume_ProducesTheDeclaredTallies`)

13 typed outcomes — 10 real calls plus the 3 constructed states — through one
assembly pass:

| measure | value |
|---|---|
| distinct call ids | 13 / 13 |
| items from source `tool` | 11 |
| withholding notes from `tool-authorization` | 2 |
| `must_express` | 2 (the nudge; the recipient-restricted result) |
| `may_express` | 2 (both failure acknowledgments) |
| recorded promotions | 4 |
| authority violations | 0 |
| lint rejections | 0 |
| contributor failures | 0 |
| items redacted before persistence | 13 / 13 |
| items with `full` retention | 0 |

The two calls that produced no item are the refusal and the secret-bearing
result. Both leave a note naming the tool; neither leaves content.

## Failures found and fixed

Four defects surfaced during the run. All are fixed and covered.

1. **`SecretDetector` could not see JSON-shaped credentials.** Scenario 7 fed it
   `{"apiKey":"sk-live-…"}` and it returned false: the prose pattern requires
   `name:` with no quote between, and `sk-live-…` breaks the OpenAI key pattern
   at the hyphen. The secret reached the item text. Fixed both patterns —
   the credential-name rule now tolerates a quote before the separator, and the
   `sk-` rule accepts the hyphenated project/live forms. This detector also
   guards durable memory, so the gap was wider than Source 2.
2. **The contributor's own check depended on value heuristics.** Added a
   structural scan: any property whose NAME is credential-shaped, with a
   non-empty value, withholds the result regardless of what the value looks
   like. `{"token": "hunter2"}` is a credential even though nothing about
   `hunter2` says so.
3. **The authorization subsystem never learned about a secret.** It filtered on
   `ContainsSecret`, which only the tool layer sets — so a credential caught by
   the contributor's own detection was withheld *silently*, with no note. Both
   contributors now share the detection. This was found by the volume run's
   note count, not by a scenario test.
4. **A promotable grant was usable without a planner promotion.** `cap.Find`
   returned the `(claim, must_express)` tuple whether or not `PlanningPromotion`
   was set, so the only thing stopping a tool from speaking on its own authority
   was the contributor's good behaviour. A grant marked promotable now EXISTS
   only for the planner: without a promotion it falls back and is diagnosed
   `promotion-grant-without-planner-promotion`. Tested with a forged
   contributor that asks for expression directly.

## Pass criteria

| # | criterion | result |
|---|---|---|
| 1 | no prose parsing | **pass** — erasing `ResultsSection` leaves the wire hash identical; the constructor takes typed outcomes only |
| 2 | authority separation | **pass** — four-way matrix, plus the forged-contributor and borrowed-reason-code cases |
| 3 | no success from failure | **pass** — provider text, exception name and stack frames absent from every item |
| 4 | adversarial inertness | **pass** — register unchanged, no restriction, no question, quoted, `no_training`, text absent from the row |
| 5 | independent attribution | **pass** — every item names its own call id and tool |
| 6 | recipient enforcement | **pass** — an obligation for an unauthorized principal is an error naming the item, and `CompactV3For` throws rather than trimming |
| 7 | idempotency | **pass** — the loop dedupes to one execution; same call id in, same wire hash out |
| 8 | privacy | **pass** — secrets contribute nothing; volatile persists metadata with the text withheld; a sensitive turn only tightens |
| 9 | `/forget` | **vacuous, and that is stronger** — see below |
| 10 | labeling | **pass** — the row is `renderer.plan3` with null Legacy/Model; no comparison row exists for a tool turn |
| 11 | isolation | **pass** — the user got a real reply, messages stored normally, no packet or Run-1c change |
| 12 | suite green | **pass** — 1284 + 31 |

### Criterion 9 is vacuous

The criterion assumed tool content would land in a row that a sweep could then
remove. It does not. `no_training` retention redacts the item text before
anything persists, and `V3ShadowItem` carries no attribution value — so neither
the tool's content nor its identity ever reaches a row. There is nothing for
`/forget` to find. The test asserts that absence directly, and separately
exercises the sweep on content the row DOES carry, so the mechanism is proven
rather than assumed.

## The tool turn's path through the shadow

A tool turn is still ineligible for a renderer COMPARISON: run-1c never trained
on tool results, so scoring it there measures the corpus's absence rather than
the renderer. Previously such a turn was skipped entirely and produced no
evidence at all.

It now takes a **plan-only** path — `IRendererShadow.ObservePlanOnly` — on the
same bounded queue with the same drop accounting. The V3 row is written; the
renderer is never invoked, no comparison is scored, and no renderer counter
moves. Verified end to end in
`RealCallSite_AToolTurn_RecordsANativeV3RowAndNeverRunsTheRenderer`: a live turn
through `Companion.RespondAsync` with the nudge phrase records
`plan.native-v3.tools=accepted=…` and `renderer.shadow=plan-only`, the counters
show `PlanOnly=1` with `Queued=0` and no canary outcome, and the row's native
section reports the tool's contribution while carrying none of its text.

Two supporting additions were needed:

- `V3NativeSection.SourceCounts` — items per contributing source. Without it a
  native row could say how many items existed but not WHICH organ contributed
  them, because the per-item detail in `Items` belongs to the translated_v2
  section and knows nothing about contributors. Source ids and counts only.
- `V3ShadowEnvelopeBuilder.WithAssembly` was written in P5a and never wired.
  It is now called, so contribution decisions, rejection reasons, authority
  violations and contributor failures land in the row.

## Behaviour changes outside the shadow

Three, all deliberate and all shadow-facing:

1. `ToolLoop.RunAsync` gained a `traceId` parameter and populates
   `Outcome.TypedOutcomes`. Production reads neither; every existing field,
   ordering, prose and decision is unchanged.
2. A tool turn's `renderer.shadow` decision verdict changed from `skipped` to
   `plan-only`. A trace-diagnostic change, stated here rather than buried.
3. `SecretDetector` matches more. It is used by durable memory as well as here,
   so this widens what gets withheld from persistence — the intended direction,
   but it is a shared component and worth naming.

## Remaining blockers

- **No live producer for audience, retention class, or cancellation.** The
  typed contract carries all three; no tool sets any of them. Until a tool does,
  scenarios 6, 10 and 11 remain constructed.
- **Expression is limited to the nudge tier.** Cognition has no general typed
  channel for "this result is required in the reply", so outside the nudge every
  live tool result is background. This is honest rather than complete.
- **The native row cannot name the tool.** `SourceCounts` says `tool: n`;
  nothing says which. Deliberate — it is the reason criterion 9 is vacuous — but
  it does limit what a shadow row can tell you about a tool turn.
- Sources 3–5 untouched.
