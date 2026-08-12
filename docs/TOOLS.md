# The Tool Layer

Ava's chat model can now *intentionally look things up* before replying — search her own
memory, check what this installation can actually do, inspect why she said something — through
a small set of controlled, **read-only** tools. This document explains what exists, how the
loop works, why it's safe, and how to extend it.

## Why

Everything the companion knows used to reach the model one way: the deterministic pipeline
decided what context a turn deserved and injected it. That's still the backbone — but it means
the model could never *ask* for something the pipeline didn't guess it needed. The tool layer
adds deliberate recall on top of automatic recall: when the user references something old, or
asks "what can you actually do?", or "why did you bring that up?", the model can fetch a real
answer instead of confabulating one.

## The tools (v1 — all read-only)

| Tool | What it answers | Arguments |
|---|---|---|
| `capability.list` | "What can you actually do here?" — live capability registry entries (availability, provider, last verified) + the invocable tool set | none |
| `memory.search` | Deliberate recall over everything remembered (same hybrid ranked retrieval as the turn pipeline, same privacy exclusions) | `query` (≤200 chars), `limit` (1–8, default 5) |
| `project.get` | One project's summary, decisions, open loops, recent activity — or the project list without a name | `name` (optional) |
| `openloop.list` | Everything unfinished: open loops, dated anticipations, her own open curiosities | none |
| `procedure.search` | Things the user taught her how to do, with active steps | `query` |
| `preference.list` | Her own formed tastes (subject, feeling, observation count) | none |
| `diagnostics.last_turn` | The operational record of recent turns: memories used, context sections present, generation metadata, tools run | `turns` (1–3, default 1) |

Every tool result is structured JSON with provenance labels (kind, owner, confidence, why)
rather than bare text, and every failure is a typed code — `invalid_arguments`, `not_found`,
`unavailable`, `provider_failure`, `timeout` — never an exception escaping into a turn.

## How a turn uses them — the Tool Planner

Tool selection is NOT the conversational model's job. An 8B roleplay fine-tune reliably
follows the JSON format but unreliably exercises the *judgment* ("would a lookup help here?"),
so orchestration belongs to a dedicated executive — the **ToolPlanner** model role — plus a
deterministic tier above it:

```
user message
  → pipeline as before (retrieval, project context, mood, …) → context packet
  → Tier 1 — deterministic nudges: extremely clear phrasings ("can you see images?",
      "why did you say that?") run their tool outright, no model judgment involved
  → Tier 2 — the PLANNER (its own small model; no personality, no prose):
      input:  COMPACT planning context — recent exchange, what automatic retrieval already
              found, detected project, tool list, any nudge hint. Never the persona packet.
      output: {"needsTools": true|false, "reason": "…", "calls": [{"tool": …, "arguments": …}]}
      → validated calls execute; ONE follow-up planning round may chase what results revealed
  → results injected into the packet as a labeled section ("Things you just looked up")
  → ONE final generation by the conversational model with everything in place → reply
```

The split is deliberate: **Stheno specializes in being her; the planner specializes in what
she needs before she speaks; deterministic code decides what actually happens.**

The planner role is configured like every other specialist (`Models:ToolPlanner` —
BaseUrl/Model/Temperature/TimeoutSeconds), falls back to `Extraction` then `Chat` when not
configured, and works best on a small instruction-following model (e.g. a 3B instruct at
temperature 0.1). The protocol is prompt-level JSON, not native function calling — local RP
models have no reliable native tool support. Planner output is untrusted text: valid plans
execute, and prose, malformed JSON, timeouts, or a dead endpoint all degrade to "answer
without lookups" (the failure is recorded in the decision trail). Bounds: max
`MaxToolCallsPerTurn` executions and max 2 planning rounds per turn; the legacy single-call
dialect ({"tool": "name"} / {"tool": null}) is tolerated for robustness with small models.

## The safety properties

- **Read-only, all of them.** No tool mutates memory, projects, preferences, or anything else.
  The worst a confused (or prompt-injected) model can do is a few wasted lookups.
- **The model never picks the user.** `userId` comes from the trusted execution context;
  arguments the model supplies are validated, clamped, and clipped inside each tool.
- **Hard bounds everywhere.** Max `MaxToolCallsPerTurn` calls (default 3), identical-call
  dedupe (a repeated call ends the loop), 30s per-call timeout, results clipped to 2KB each /
  6KB per section.
- **Unknown tools are refused honestly.** Asking for a tool this installation doesn't have is
  recorded in the trace (`unavailable`) and ends the loop — it never pretends.
- **The conversation stays truthful.** Tool calls and results never become conversation
  messages and never enter durable memory. They exist in exactly two places: the current
  turn's prompt, and the diagnostics trace.
- **Privacy is inherited, not re-implemented.** `memory.search` goes through the same
  retriever as the turn pipeline, so DoNotRemember exclusions and user isolation apply
  automatically.

## Diagnostics

Every turn records a `TurnDiagnostics` entry in an in-memory per-user ring (last 5 turns):
what was retrieved, which packet sections were present, generation metadata, which tools were
advertised and which ran (with timing and result codes). That ring is deliberately *not*
persistence — it's operational self-knowledge, cleared on restart, never a place for secrets.
It powers both the `diagnostics.last_turn` tool ("why did you say that?" answered from the
record, not confabulation) and future debugging.

## Configuration

| Option | Default | Meaning |
|---|---|---|
| `Companion:EnableToolUse` | `true` | Master switch. Off → the loop never runs, turns behave exactly as before. |
| `Companion:MaxToolCallsPerTurn` | `3` | Upper bound on lookups before the final reply. |

The decision prompt lives in the prompt catalog (`tools.system`, plus
`renderer.tools.header` / `renderer.tools.rules` for how results render into context) and can
be overridden like any other prompt via the `prompts/` directory or the prompt editor UI.

## Adding a tool

1. Implement `ICompanionTool` (see `src/Companion.Core/Services/Tools/CompanionTools.cs`):
   validate arguments with `ToolArgs`, return `ToolResult.Success/Fail`, never throw for bad
   input, clip everything you return.
2. Register it in `DependencyInjection.cs` (`AddScoped<ICompanionTool, YourTool>()`). The
   loop, `capability.list`, and diagnostics pick it up automatically.
3. Set `Available` honestly — a tool whose backing provider isn't configured should say so,
   and it will simply not be advertised.
4. Keep v1 discipline: read-only. A future write-capable tool class needs its own permission
   model (explicit user confirmation) before it exists at all.
