# Persisten_AI — Persistent AI Companion

A conversational AI companion with **durable memory and long-term context**. It uses an
existing language model for generation; its value is in the layers around the model —
memory, continuity, retrieval, temporal reasoning, and project awareness.

> **Guiding principle: build continuity, not consciousness.**

## Status

**Phase 3 (memory extraction & validation) implemented** on top of the Phase 2 slice.
Each turn now also extracts candidate memories from the exchange and runs them through a
validation pipeline before anything is stored:

> generate candidates → normalize → de-duplicate → compare to existing → score confidence
> → require evidence → **decide (accept / merge / reject / hold-for-review)** → persist with
> an audit trail.

Extraction only *proposes*; the pipeline *disposes* — the model never writes memory directly.
Runs end-to-end on deterministic mock/rule-based providers (no network or GPU). **All 39
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
src/Companion.Core            domain records, interfaces, retrieval + extraction pipeline logic (no I/O)
src/Companion.Infrastructure  EF Core store, SQLite BLOB vector index, mock + rule-based/LLM extractors, DI
src/Companion.Cli             chat REPL + /seed, /remember, /why diagnostics
tests/Companion.Tests         39 tests: ranking, packet, provenance, isolation, score math,
                              extraction (accept/merge/reject/review), confidence, normalizer, LLM parsing, e2e
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

After a turn, `/why` also shows the extraction verdicts — which candidates were proposed and
whether each was accepted, merged into an existing memory, rejected, or held for review, with
reasons. The model providers are deterministic (`MockChatModel`, `MockEmbeddingModel`,
`RuleBasedMemoryExtractor`), so everything runs offline. Real local/hosted providers —
including `LlmMemoryExtractor` — plug in behind the same interfaces.

## Next step

**Phase 4 — Projects, entities & open loops** (first-class project/open-loop records,
evidence-based entity resolution, ambiguous-reference clarification), per
`docs/IMPLEMENTATION_PLAN.md`.
