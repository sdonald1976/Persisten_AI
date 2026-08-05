# Persisten_AI — Persistent AI Companion

A conversational AI companion with **durable memory and long-term context**. It uses an
existing language model for generation; its value is in the layers around the model —
memory, continuity, retrieval, temporal reasoning, and project awareness.

> **Guiding principle: build continuity, not consciousness.**

## Status

**Phase 2 (minimal vertical slice) implemented.** The smallest complete loop runs
end-to-end on deterministic mock models — no network or GPU:
store conversation → detect project → retrieve ranked+explained memories →
assemble a bounded, provenance-labeled context packet → generate → store → diagnostics.
All 24 tests pass. Design docs for the full phased plan are under `docs/`.

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
src/Companion.Core            domain records, interfaces, retrieval/scoring/assembly logic (no I/O)
src/Companion.Infrastructure  EF Core store, SQLite BLOB vector index, mock models, DI
src/Companion.Cli             chat REPL + /seed, /remember, /why diagnostics
tests/Companion.Tests         24 tests: ranking, packet, provenance, isolation, score math, e2e turn
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

The Phase-2 model providers are deterministic mocks (`MockChatModel`, `MockEmbeddingModel`),
so everything runs offline. Real local/hosted providers plug in behind the same interfaces.

## Next step

**Phase 3 — Memory extraction & validation** (candidate extraction, dedup, confidence,
evidence rules, acceptance pipeline), per `docs/IMPLEMENTATION_PLAN.md`.
