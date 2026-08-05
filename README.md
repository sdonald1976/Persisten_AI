# Persisten_AI — Persistent AI Companion

A conversational AI companion with **durable memory and long-term context**. It uses an
existing language model for generation; its value is in the layers around the model —
memory, continuity, retrieval, temporal reasoning, and project awareness.

> **Guiding principle: build continuity, not consciousness.**

## Status

**Phase 4 (projects, entities & open loops) implemented**, on top of Phases 2–3. Projects,
decisions, activity, and open loops are now first-class records. Each turn resolves the
query's project reference (evidence-based, confidence-aware), reconstructs that project's
state, surfaces relevant open loops, and — when a reference is ambiguous — asks a clarifying
question instead of guessing. Accepted memories update project/open-loop state: a planned
matter opens a loop; reporting it done closes the matching one.

Earlier phases still hold: turns retrieve ranked+explained memories into a bounded,
provenance-labeled context packet (Phase 2), and extract candidate memories through a
validation pipeline before anything is stored — extraction proposes, the pipeline disposes
(Phase 3). Everything runs offline on deterministic mock/rule-based providers. **All 49
tests pass.** Design docs for the full phased plan are under `docs/`.

## Documents

| Document | What it covers |
|----------|----------------|
| [`docs/PERSISTENT_COMPANION_VISION.md`](docs/PERSISTENT_COMPANION_VISION.md) | Product goal, non-goals, UX, capabilities, success criteria. |
| [`docs/CURRENT_ARCHITECTURE_ASSESSMENT.md`](docs/CURRENT_ARCHITECTURE_ASSESSMENT.md) | Assessment of the existing repository (finding: empty → greenfield). |
| [`docs/PERSISTENT_COMPANION_ARCHITECTURE.md`](docs/PERSISTENT_COMPANION_ARCHITECTURE.md) | Components, data flow, storage, retrieval, memory lifecycle, model boundaries, Mermaid diagram. |
| [`docs/IMPLEMENTATION_PLAN.md`](docs/IMPLEMENTATION_PLAN.md) | Phased tasks, dependencies, acceptance criteria, test requirements. |
| [`docs/FIRST_VERTICAL_SLICE.md`](docs/FIRST_VERTICAL_SLICE.md) | Exact files proposed for the first vertical slice. |

## Stack

.NET 8 · EF Core + SQLite (authoritative store) · in-process vector index (swappable) ·
mockable model providers (local/hosted/mock) · xUnit.

## Project layout

```
src/Companion.Core            domain records, interfaces, retrieval + extraction + project logic (no I/O)
src/Companion.Infrastructure  EF Core store, SQLite BLOB vector index, mock + rule-based/LLM extractors, DI
src/Companion.Cli             chat REPL + /seed, /remember, /projects, /project, /loops, /why
tests/Companion.Tests         49 tests: ranking, packet, provenance, isolation, score math,
                              extraction (accept/merge/reject/review), confidence, normalizer, LLM parsing,
                              entity resolution + clarification, project summary, open-loop create/close, e2e
```

## Build & run

```bash
dotnet build                                   # build the solution
dotnet test                                    # run all tests

# seed a few months of demo history, then chat
dotnet run --project src/Companion.Cli -- seed
dotnet run --project src/Companion.Cli

#  you> I finally tested that board at home.
#  companion> ...
#  you> /why        # show retrieval scores, reasons, exclusions, and the context packet
#  you> /remember   # show what the companion remembers about you
```

`/project <name>` reconstructs a project's current state (status, decisions, open loops,
recent activity); `/projects` lists them; `/loops` lists open loops. `/why` shows the full
turn diagnostics — retrieval scores and reasons, project resolution (with candidate ranking
and any clarifying question), open loops surfaced, project-state updates, and extraction
verdicts. The model providers are deterministic (`MockChatModel`, `MockEmbeddingModel`,
`RuleBasedMemoryExtractor`), so everything runs offline. Real local/hosted providers —
including `LlmMemoryExtractor` — plug in behind the same interfaces.

## Next step

**Phase 5 — Temporal revision & correction** (supersession, contradiction resolution, user
corrections, soft deletion, and audit history), per `docs/IMPLEMENTATION_PLAN.md`.
