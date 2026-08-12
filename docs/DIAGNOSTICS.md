# Diagnostics & Observability

Everything the companion does operationally leaves a record you can query — when she used her
tools, which model served which job and how fast, what a recent turn actually had in context.

## Where to look

| Surface | What it shows |
|---|---|
| **Chat page** | A quiet `🔧 looked up: memory.search` line under any reply she used tools for. |
| **Dashboard → 🔬 Diagnostics** | Recent turns (context sections, model, rounds, tool calls), per-role model telemetry for the last 24h, and the durable tool-call history. |
| `GET /diagnostics/turns` | The in-memory ring: the last 5 turns' full operational story (what was retrieved, which sections were present, generation metadata, tools advertised/used). Cleared on restart. |
| `GET /diagnostics/tools?count=50` | Durable tool-call history, newest first. Survives restarts. |
| `GET /diagnostics/models?hours=24` | Per role+model aggregates: calls, failures, average latency, token totals, last used. |
| `diagnostics.last_turn` (her tool) | She can answer "why did you say that?" from the same ring. |
| Server console | `Companion` log level is `Information` by default now — turn summaries, tool calls, outreach and reflection outcomes all print live. |

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
