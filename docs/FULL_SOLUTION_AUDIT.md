# Full-Solution Engineering & AI Architecture Audit

*Reviewed at commit `ef93cab` (+ local); ~28.5k lines across Core / Infrastructure / Api / Tests;
589 tests green.*

## 1. Executive summary

This is a healthy codebase. The house rules it was built on — deterministic control flow decides
*what*, models decide *how*; everything "alive" is backed by timestamped DB truth; privacy fails
closed — are visible in nearly every subsystem and have held up as the feature surface grew. The
recent ToolPlanner work moved the architecture in exactly the right direction: specialist roles
doing judgment work, the RP model doing personality.

The audit found **no P0 issues** (no data-loss, correctness, or security defects). It found
**three P1 issues**, all fixed in this pass, and they share a theme: *the turn had no seams for
infrastructure failure*. Retrieval, project resolution, and post-reply derived work all ran
unguarded, so a dead embedding endpoint or a hiccuping extraction model turned a perfectly
answerable conversation into an HTTP 500 — including cases where the reply had **already been
generated and stored**. Everything model-facing degrades gracefully (reranker, privacy, planner,
greeter, rephraser); everything embedding-facing and post-reply did not.

The other headline finding is efficiency: the same user message was embedded **up to three times
per turn** (project resolution → open-loop scoring → memory retrieval), each a network round trip
on the critical path. Now cached.

The biggest *remaining* concern is not a defect but a trajectory: `GetRetrievableMemoriesAsync`
loads every non-deleted memory — embeddings included — into memory on every single turn. At today's
scale it's invisible. At three years of daily use it is the wall this system hits first. It is
documented as the top backlog item with a measured rationale rather than fixed here, because the
fix touches the store contract and deserves its own change.

## 2. Current architecture overview

```
Companion.Core          domain + services (no I/O deps): the companion's brain
Companion.Infrastructure EF/SQLite stores, model providers, vector index, seeding
Companion.Api           minimal-API host, WebSocket, static UI (chat/dashboard/prompts)
Companion.Tests         589 tests, mostly behavioral over the real composition root
```

Layering is genuinely respected: Core has no EF or HTTP dependency, and every provider sits behind
an abstraction (`IChatModel`, `IEmbeddingModel`, `ITranscriber`, …). Composition is one file
(`DependencyInjection.cs`), which makes the whole system legible in one read — a real asset.

## 3. Runtime turn flow (as implemented)

```
RespondAsync
 ├─ conversation ownership check ......................... 1 query
 ├─ last-seen read (temporal anchor) ..................... 1 query
 ├─ store user message ................................... 1 write
 ├─ pending-clarification check .......................... 1 query
 ├─ project context: resolve + summary + open loops ...... 3–6 queries + [EMBED ×2]
 │   └─ ambiguous? → store question, STOP (no model call)
 └─ CompleteTurnAsync
     ├─ privacy classify (rules; LLM if configured) ...... [MODEL: safety, optional]
     ├─ persona/identity/roleplay gate ................... 1 query
     ├─ retrieval: all memories → score → rerank ......... 1 query + [EMBED ×1] + [MODEL: reranker]
     ├─ associative recall ............................... 1–2 queries
     ├─ recent messages .................................. 1 query
     ├─ mood capture + anticipation detect (rules) ....... 2 writes
     ├─ relationship / inner state / familiarity ......... 3–4 queries
     ├─ musing + curiosity + preferences + attention
     │   + procedures + capabilities + perspectives ...... 6–8 queries
     ├─ context packet assembly (deterministic)
     ├─ TOOL PLANNER (compact context) ................... [MODEL: tool-planner ×1–2]
     │   └─ nudges + validated tool calls ................ 0–3 tool executions
     ├─ generation (+ auto-continue) ..................... [MODEL: conversation ×1–N]
     │   └─ completion judge ............................. [MODEL: task-auditor, optional]
     ├─ store assistant message .......................... 1 write
     └─ derived state (GUARDED as of this audit)
         ├─ extraction ................................... [MODEL: extraction] + [EMBED ×N]
         ├─ project updates / decisions .................. writes
         ├─ attention / procedures / commitments ......... writes
         └─ diagnostics ring + durable telemetry
```

**Model calls on the critical path (real model configured):** 1 privacy (optional) + 1 reranker +
1–2 planner + 1–N conversation + 1 auditor (optional) ≈ **4–6**, of which only the conversation
call is user-visible latency in the streaming path.

**Redundancy found:** the user message was embedded 3× (now 1×). Memory candidates are materialized
once per turn (fine today, see §9). No duplicate context construction was found — the packet is
assembled once and reused for both planning (compacted) and generation.

## 4. Model-role analysis

| Role | Job | Verdict |
|---|---|---|
| **Chat (conversation)** | Ava's voice, personality, integrating context | Correctly scoped **now**. Tool orchestration was removed from it (ToolPlanner); privacy, extraction, auditing were already elsewhere. No further responsibilities should be moved *onto* it. |
| **ToolPlanner** | What to look up before answering | Correct as a separate role. Small instruct model, temp ≈0.1. Falls back Extraction → Chat. |
| **Extraction** | Turn → memory candidates (structured) | Genuinely needs a model; JSON validated; failure now benign. |
| **Summarizer** | Consolidation summaries | Background only. Cheap model right. |
| **Reranker** | Second-pass memory relevance | Model earns its place (semantic judgment), and rule-based fallback exists. |
| **Safety/privacy** | Should this turn skip durable memory? | Hybrid rules-first + LLM. Correct: rules catch secrets deterministically; the model catches phrasing. |
| **TaskAuditor** | Is a self-stopped reply complete? | Optional, off critical path when disabled. Fine. |
| **Embeddings** | Vectors | Now cached; write paths still fail loudly (correct — never store a degenerate vector). |
| **Vision / Transcription / Speech** | Media transport | Correctly *not* model-invokable tools; reported through the capability registry. |

**LLM → deterministic opportunities:** none compelling remain. The deterministic detectors
(commitment, decision, anticipation, mood, in-character, secrets) already cover the mechanical
cases, and each is a pure function with unit tests.

**Rules → small specialist opportunities:** `ToolNudge` is the one place where brittle regex does
work a small model could do better — but it exists precisely because the model *declined* the job
(observed live). Correct call: keep rules as the floor, planner as the ceiling.

## 5. Memory & retrieval analysis

The lifecycle (extract → validate → evidence → dedupe → persist → retrieve → rerank → consolidate →
supersede → correct → forget) is complete and provenance is carried end to end. Ownership
(User/Companion/Shared) is enforced at write time and visible at read time. Privacy exclusions live
*inside* store queries — the right place, because it cannot be forgotten by a caller.

Distinctions between memory types are semantically real, not accidental: facts vs. episodes vs.
shared perspectives vs. projects vs. procedures vs. curiosities vs. attention vs. open loops each
have different lifetimes and different triggers. **No consolidation recommended** — merging any two
would lose a real distinction.

Retrieval order is: load all candidates → hybrid score (similarity + keyword + recency + importance
+ confidence + project + open-loop boost) → relevance floor → rerank → budget. This is a sound
order; the floor before reranking is what stops a merely-recent fact bleeding into an unrelated
turn. The one structural weakness is the "load all" step (§9).

## 6. Tool & planner analysis

Seven read-only tools, all correctly model-visible (each answers a question the model cannot
answer from context alone), all bounded, all ownership-enforced through the trusted `userId`.
Descriptions are written for a small planner and are appropriately terse.

Planner review: context is compact by construction (recent exchange + retrieval summary + project +
tool list + hint — explicitly *not* the persona packet, and there is a test asserting that);
rounds are capped at 2; calls capped by the existing per-turn budget; results clipped at 2KB each /
6KB total; failure is benign in every mode. Deterministic hints are useful *and* brittle by nature —
mitigated by the fact that a missed hint costs nothing and a wrong hint costs one read-only lookup.

## 7. Context & prompt analysis

62 catalog entries, all file-overridable. The renderer is catalog-driven, so there is one place
where prompt text lives. Inspection of the assembled Chat prompt found **no contradictory or
duplicated instruction blocks** — sections are single-purpose and conditionally emitted (a section
with no content is omitted entirely, so an empty companion state costs zero tokens).

One dead entry was found and removed: `tools.system`, orphaned when the ToolPlanner took over tool
selection — a textbook historical accident (a prompt compensating for a job that moved).

Context budgeting is currently global (counts per section: memories, loops, preferences, attention)
rather than token-budgeted. That is adequate now and is listed in the backlog for when transcripts
grow.

## 8. Persistence analysis

27 tables, 36 indexes, all user-scoped queries filtered by `UserId`, migrations applied on startup
with a pre-migration backup and restore-on-failure path (a genuinely good design). Index coverage
matches the actual query shapes — the hot reads (`UserId`, `UserId+Status`, `ConversationId+Timestamp`,
`UserId+Timestamp`) are all indexed. No N+1 patterns found in the turn path: each subsystem does one
scoped query. `DateTimeOffset` is stored via a sortable binary converter, so temporal ordering is
correct in SQLite.

## 9. Performance findings

1. **Triple embedding of the user message** (fixed — see §15). Saves 2 embedding round trips per
   turn, on the critical path.
2. **Whole-memory-table materialization per turn** (backlog P2, top item). `GetRetrievableMemoriesAsync`
   loads every memory *including its embedding blob* on every turn. The retriever never reads those
   blobs — similarity comes from the in-memory vector index — so at 10k memories this deserializes
   roughly 30MB of float arrays per turn for nothing. Fix: a projection that omits embeddings for
   the retrieval path. Deferred because it touches the store contract and the `IMemory` shape.
3. Planner adds one model call to most turns — deliberate, and now measurable per-role in
   `/diagnostics/models`.

## 10. Reliability findings

All three fixed this pass (§15). The general principle now applied consistently: **infrastructure
failure costs quality, never the conversation** — matching how the reranker, privacy classifier,
planner, greeter, and rephraser already behaved. Write paths deliberately still throw (storing a
degenerate embedding would silently corrupt the vector index forever).

Remaining gap: SQLite `database is locked` under concurrent writes is unhandled (backlog).

## 11. Security & privacy findings

No regressions found. Loopback binding, explicit CORS allow-list, optional bearer token on every
REST/SSE/WebSocket path, secrets detected before memory write, `DoNotRemember` enforced inside store
queries, user isolation on every store method, and the tool layer inherits all of it (a tool cannot
supply its own `userId` — there is a test proving a plan that smuggles one changes nothing).

Telemetry stores no prompt or reply text — sizes and outcomes only — which was the right call for a
table that retains 30 days of history.

## 12. Behavioral findings

Companion-state systems each earn their place: musings resurface only on relevance, curiosities are
voiced-once with cooldown, anticipations expire, attention items TTL at 7 days, preferences erode
before flipping. None can accumulate indefinitely; each has a decay or expiry path. No behavioral
change was made in this pass.

## 13. Test-quality findings

The suite is behavior-first (it drives the real composition root over in-memory SQLite), which is
why a protocol change as large as the ToolPlanner landed with zero test rewrites. Gaps found and
partially closed: failure-mode coverage was thin (now +4 tests). Still missing: restart/persistence
tests (does the vector index rebuild correctly from disk?) and concurrency tests (two turns at once).
Both are in the backlog. One flaky-by-construction pattern was fixed earlier this session (three API
test classes racing on process-wide env vars).

## 14. Prioritized improvement table

| Pri | Area | Finding | Why it matters | Recommended change | Effort | Risk | Benefit |
|---|---|---|---|---|---|---|---|
| **P1** | Reliability | Post-reply derived work unguarded; a failure after the reply was stored returned 500 | User loses an answer the companion actually gave; exchange left orphaned | Guard the derived-state block; log and continue | S | Low | ✅ **Done** |
| **P1** | Reliability | Embedding outage threw out of retrieval → whole turn failed | Ava becomes unusable when one optional server is down | Degrade to keyword retrieval (Cosine already treats empty as 0) | S | Low | ✅ **Done** |
| **P1** | Performance | Same message embedded 3×/turn | 2 needless round trips on the critical path | Bounded caching decorator, outermost | S | Low | ✅ **Done** |
| **P2** | Performance | Retrieval materializes all embeddings it never reads | The first real scaling wall (~30MB/turn at 10k memories) | Projection without embeddings for the retrieval path | M | Med | Backlog #1 |
| **P2** | Prompts | `tools.system` dead after ToolPlanner | Stale prompt confuses future editors | Remove | S | Low | ✅ **Done** |
| **P2** | Reliability | SQLite lock contention unhandled | Concurrent turns could surface a raw DB error | Busy-timeout + retry policy | S | Low | Backlog #2 |
| **P2** | Context | Sections have count budgets, not token budgets | Long transcripts could crowd the window | Per-section token budgets | M | Med | Backlog #3 |
| **P2** | Tests | No restart or concurrency coverage | Vector-index rebuild and racing turns untested | Add both | M | Low | Backlog #4 |
| **P3** | Code | `Program.cs` 875 lines, `Companion.cs` 764 | Navigability, not correctness | Split endpoints by area | M | Low | Backlog #5 |

## 15. Changes implemented during this pass

1. **`CachingEmbeddingModel`** (new) — bounded (256-entry), copy-in/copy-out memoization, wrapped
   outermost so cache hits are not recorded as provider calls. Turn embeddings of the user message:
   **3 → 1**.
2. **Retrieval degrades on embedding failure** — `Retriever`, `ProjectContextService`, and
   `EntityResolver` now fall back to keyword/alias/recency signals instead of throwing. Vector search
   is skipped entirely when there is no query vector.
3. **Derived-state work can no longer destroy a delivered reply** — extraction, project updates,
   attention, procedures, and commitments are wrapped; failures are logged as errors and the turn
   stands. Cancellation still propagates (that's the caller leaving, not a failure).
4. **Dead `tools.system` prompt removed.**
5. **+4 tests** (`ResilienceTests`): embedding server dying mid-session, derived-state explosion,
   cache collapsing repeats, and a full turn embedding the user message exactly once.

**Behavioral difference intentionally introduced:** during an embedding outage, retrieval is
keyword-only, so recall is shallower for those turns (previously: no reply at all). When derived
work fails, that turn's memories/updates are lost, and the failure is logged at Error (previously:
the whole turn failed and nothing downstream ran anyway). No change to personality, identity,
privacy semantics, memory ownership, or conversational behavior.

## 16. Larger recommendations deferred

See `docs/IMPROVEMENT_BACKLOG.md` — the retrieval projection, SQLite busy-retry, token budgeting,
restart/concurrency tests, endpoint-file split, and a shared turn-analysis object (evaluated and
**not** recommended in its naive form).
