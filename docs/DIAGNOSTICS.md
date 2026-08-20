# Diagnostics & Observability

Everything the companion does operationally leaves a record you can query — when she used her
tools, which model served which job and how fast, what a recent turn actually had in context.

## Where to look

| Surface | What it shows |
|---|---|
| **Chat page** | A quiet `🔧 looked up: memory.search` line under any reply she used tools for. |
| **Dashboard → 🔬 Diagnostics** | Recent turns (context sections, model, rounds, tool calls), per-role model telemetry for the last 24h, and the durable tool-call history. |
| `GET /diagnostics/turns` | The in-memory ring: the last 5 turns' full operational story (what was retrieved, which sections were present, generation metadata, tools advertised/used). Each turn carries a `traceId`, structured `retrieved` entries (content/score/source), and a `decisions` list — every system-level verdict the pipeline made (privacy, roleplay, project, curiosity, register, budget, tools, gate, extraction) with its decider and reason. See `LANGUAGE_ORGAN.md` Phase 0. Cleared on restart. |
| `GET /diagnostics/tools?count=50` | Durable tool-call history, newest first. Survives restarts. |
| `GET /diagnostics/models?hours=24` | Per role+model aggregates: calls, failures, average latency, token totals, last used. |
| `diagnostics.last_turn` (her tool) | She can answer "why did you say that?" from the same ring. |
| Server console | `Companion` log level is `Information` by default now — turn summaries, tool calls, outreach and reflection outcomes all print live. |

## Finding bugs without inventing conversations

Every fault this project has had was found by talking to her and then reading the stored rows.
None came from the unit suite, and that isn't an accident — they all lived in the seams. A model
role pointed at a model too small to do its job. A prompt that overflowed a window nothing knew the
size of. A filter that ran everywhere except the path a person actually talks through. The
components on both sides of each seam were correct and individually tested.

Two things automate the reading.

### The soak harness

```bash
dotnet run --project tools/Companion.Soak
```

Drives real conversations against a running companion over HTTP and asserts *properties* of the
replies — never exact text, because a model is stochastic and an exact-output assertion gets
rewritten every run or quietly weakened until it passes. Exits non-zero, so it can gate a change.

| Scenario | The failure it reproduces |
|---|---|
| `memory` | Facts stated in one conversation must survive into the next. This is the whole product in one check, and it was false for the entire life of the project. |
| `register` | A short message gets a short reply, not an interview. |
| `compound` | Several things in one message stay several things. |
| `long` | A conversation long enough to put the prompt under pressure — is she still herself afterwards? |

Per-turn checks apply to every reply in every scenario, and each guards a fault that reached a real
conversation: naming herself at the start, narrating gestures she has no body for, annotating her
speech with the packet's own provenance words, reproducing the shape of her context packet,
repeating an entire earlier turn verbatim, and overflowing the prompt budget.

Run one at a time with `--only memory`; a turn can take a couple of minutes when models are
swapping. `--long-turns 30` makes the long scenario harder.

### The extraction self-test

The model preflight checks each configured model *exists*. That is a different question from
whether it *works*, and the gap between them is the worst bug this project has had: the extraction
role was pointed at a model that answered `{}` to everything — present, responsive, fast, and
completely useless. Presence passed. Every test passed. She simply never remembered anything.

So at startup the extractor is now asked to do its actual job once, on a sentence whose answer is
not in doubt. If it finds nothing you get an error on boot naming the likely cause, instead of
discovering it weeks later.

## What gets recorded

**Every model call** (all chat roles — conversation, extraction, summarizer, reranker, safety,
task-auditor — plus embeddings) via a logging decorator at the provider seam: role, operation,
reported model name, duration, prompt/completion sizes, token usage when the server reports it,
and outcome (failures record the exception type and rethrow untouched). **No prompt or reply
text is stored** — sizes and outcomes only, so telemetry never becomes a second, unguarded
copy of your conversations.

**Every tool call** the model makes mid-turn: tool name, validated arguments (bounded), outcome
code, duration. Results are not stored — they exist only in the turn that used them.

## Judging models with it

`/diagnostics/models` is the comparison table: swap a role's model in `appsettings.json`
(e.g. a different `Summarizer`), talk normally for a day, and compare the role's average
latency and failure rate before/after. Failures per role also tell you which fallback chains
are actually being exercised.

## Retention & safety

- Telemetry writes can never break the call they describe — the store swallows its own
  failures (logged at debug), and the decorators rethrow the original result/exception.
- The sleep cycle prunes records older than 30 days (`SleepCycle.DiagnosticsRetention`).
- The turn ring (`/diagnostics/turns`) is process-lifetime by design: operational
  self-knowledge, not memory.
