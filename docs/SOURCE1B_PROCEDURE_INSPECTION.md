# Source 1b — procedure inspection and selector proposal (2026-08-24)

Inspection first, per instruction. Conclusion up front: **the existing procedure
system is a knowledge store, not an activity engine, and no question/action
selector exists anywhere in production.** Item 5's stop condition therefore
applies — this document ends with the smallest bounded selector proposal and
implements nothing.

## 1. What `procedure.search` actually is

Traced through `CompanionTools.ProcedureSearchTool` → `IProcedureStore` →
`Procedure`/`ProcedureStep`/`ProcedureRevision`.

| question | finding |
|---|---|
| how is a procedure identified? | `Procedure.Id` (Guid) + `Name`, scoped by `UserId`. Semantic identity is the NAME ("how I like the fire laid"); lookup is `SearchAsync(userId, query, limit)` — a text search, not a resolution to a stable key. |
| stable ID / version? | Stable Guid: yes. Version: **no version field**. `ProcedureRevision` records *edits over time*, which is history, not a callable version. |
| what starts or resumes one? | **Nothing.** There is no start, no resume, no run. A procedure is *read*, never *executed*. |
| what state is persisted? | The recipe: name, description, ordered steps, owner/access/status/confidence, source message, revisions. **No execution state of any kind** — no instance, no position, no bindings. |
| can it select actions/questions? | **No.** `IProcedureStore` exposes teaching, revision, search, and revision-history. There is no selection surface. |
| does an LLM perform selection? | No. The LLM *invokes the search tool* and receives steps as text; any "next step" behaviour in a reply is the chat model improvising over that text, unowned and unrecorded. |
| how do results enter the packet? | As a tool result: JSON → `packet.ToolResults` → rendered prose in the generation prompt. |

Corroborating evidence for "no activity concept exists": a repo-wide search for
`SelectNext|NextQuestion|NextAction|ChooseAction|Activity*` outside the V3
branch code returns nothing procedural — only unrelated `LastActivityAt`
project timestamps.

## 2. Reuse assessment — what is structurally correct, and what cannot stretch

**Correct and reusable as-is:**
- `Procedure` as the *definition* of a user-taught how-to. Twenty Questions can
  legitimately be a procedure DEFINITION (name, description, steps = rules).
- `IProcedureStore.SearchAsync` for resolving "the user means this procedure".
- The teaching/revision pipeline: a user can already teach a procedure, and
  activation should resolve against exactly those records.

**Structurally incapable, with reasons:**
1. **No instance/runtime layer.** `Procedure` is a singleton definition per user;
   two simultaneous conversations playing the same game would collide on one
   row. Activity state needs its own entity keyed by instance, not by procedure.
2. **No lifecycle.** `ProcedureStatus` is Active/… for the *definition* ("is
   this recipe still true"), not for a run ("is this game in progress"). Reusing
   it would conflate "the recipe exists" with "a game is happening".
3. **No selection surface, and no owner for one.** Nothing in the store, the
   tool layer, or the packet path chooses a next action. The chat model does it
   implicitly, which is precisely the failure the December game recorded.
4. **Steps are prose instructions**, not typed moves. A step reads "ask a
   yes/no question that halves the candidate space"; it is not a callable.

**Therefore:** the activity layer is genuinely new work, but it must *sit on
top of* the existing definition store rather than replace it — activation
resolves a real `Procedure` row, and the instance references its Guid. No
competing procedure framework: one definition store (existing), one activity
runtime (new, shadow-isolated).

## 3. The stop condition (item 5)

> "If no suitable selector exists, stop after inspection and propose the
> smallest bounded selector design before implementing it."

No selector exists. Implementation stops here. The proposal follows.

## 4. Proposal — the smallest bounded selector

**Shape: deterministic strategy over a typed question bank, with NO model call.**
Rejected alternatives and why:
- *Executive/planner model selects*: reintroduces the exact failure mode (a
  model rediscovering state each turn), costs a model call per turn, and makes
  repeat-prevention probabilistic. Rejected for v1.
- *Hybrid (model proposes, ledger validates)*: defensible later, but it is
  strictly more machinery than v1 needs and cannot be evaluated until a
  deterministic baseline exists to compare against. Deferred.
- *Prose steps interpreted at runtime*: requires a step interpreter — the
  largest option, and unnecessary for a fixed-rule game.

### 4.1 Interface (bounded, one method)

```csharp
public interface IActivityQuestionSelector
{
    string ProcedureType { get; }                  // "twenty-questions"
    int Version { get; }
    SelectionOutcome SelectNext(ActivityInstance instance);   // pure, no I/O, no model
}

public sealed record SelectionOutcome(
    AskedQuestion? Question,                       // null ⇒ failure
    string? FailureReason,                         // diagnosed, never silent
    IReadOnlyList<string> RejectedForRepeat);      // observability
```

### 4.2 The v1 strategy (twenty-questions)

A static, ordered **question bank** of typed candidates, each with a stable
`Key`, its text, and the facts it would establish:

```
{ key: "physical",     text: "does it exist physically",        splits: [...] }
{ key: "man-made",     text: "is it man-made",                  ... }
{ key: "moving-parts", text: "does it have moving parts",       ... }
…
```

Selection = first candidate that passes all five validations, in bank order
(coarse → fine, so the game narrows). No scoring, no search, no randomness:
deterministic and replayable, which is what makes shadow evidence meaningful.

### 4.3 The five validations (all pure, all on the authoritative ledger)

1. **Repeat detection** — candidate `Key` not in `instance.AskedQuestions`
   (stable key, so rephrasing cannot evade it).
2. **Question-limit validation** — `CurrentQuestionNumber <= QuestionLimit`.
3. **Role validation** — `instance.AskerParticipantId` is the companion; a
   game where the user asks yields no companion-side selection.
4. **Relevance / state consistency** — the candidate's key is not already
   settled by `EstablishedFacts` or `Exclusions`, and its precondition (if
   any) holds against established facts.
5. **Evidence stamping** — the returned question is stamped
   `activity:{InstanceId}` so the contributor's grant requirement is satisfiable.

Failure produces `SelectionOutcome(null, reason, …)` with reason ∈
{`no-valid-question-available`, `question-limit-reached`, `not-asker-role`,
`activity-not-active`} — which the contributor already turns into a diagnosed
contributor failure rather than an ordinary plan (implemented in Source 1a).

### 4.4 Scope boundaries of the proposal

- One procedure type (`twenty-questions`, version 1). No general framework.
- No model call, no I/O, no persistence inside the selector — it is a pure
  function of the ledger, so it is trivially testable and idempotent under
  retries.
- The question bank is data (a static typed list), reviewable in one screen.
- Candidate/guess narrowing is NOT in v1: `Candidates` stays a recorded field;
  final-guess selection is a separate, later decision.

### 4.5 What Source 1b still needs after the selector is approved

Activation (resolving a real `Procedure` row + recording the activation
evidence), the shadow-isolated activity store (new entity + migration, in the
shadow boundary — never touching production procedure state), transactional
turn binding with idempotent retries, the real turn-path assembly call, the
LifeRunner-driven simulated sessions, and the labeled shadow rows.

**Awaiting approval of §4 before implementing any of it.**
