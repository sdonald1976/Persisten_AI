# Persisten_AI — Persistent AI Companion

A conversational AI companion with **durable memory and long-term context**. It uses an
existing language model for generation; its value is in the layers around the model —
memory, continuity, retrieval, temporal reasoning, and project awareness.

> **Guiding principle: build continuity, not consciousness.**

## Status

**Phase 5 (temporal revision & correction) implemented**, on top of Phases 2–4. Information
now changes without erasing history: a direct user statement that contradicts a stored fact
**supersedes** it (the old value is kept, marked not-current); weaker/inferred contradictions
are parked for review. Users can **correct, dispute, forget (soft-delete), merge, and
re-associate** memories, and **merge or split** projects — every operation writes an audit
entry, and a forgotten memory is purged from the embedding index so it can't resurface.

Earlier phases still hold: project-aware turns with honest reference resolution and open-loop
tracking (Phase 4), a validate-before-store extraction pipeline (Phase 3), and ranked,
provenance-labeled retrieval into a bounded context packet (Phase 2). Everything runs offline
on deterministic mock/rule-based providers. **All 61 tests pass.** Design docs are under `docs/`.

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
src/Companion.Core            domain records, interfaces, retrieval + extraction + project + curation logic
src/Companion.Infrastructure  EF Core store, SQLite BLOB vector index, mock + rule-based/LLM extractors, DI
src/Companion.Cli             chat REPL + /seed, /remember, /projects, /project, /loops,
                              /forget, /dispute, /correct, /reassign, /mergeprojects, /why
tests/Companion.Tests         61 tests: retrieval, packet, provenance, isolation, score math,
                              extraction (accept/merge/reject/review/supersede), confidence, normalizer,
                              LLM parsing, resolution + clarification, project summary, open-loop create/close,
                              supersession, correction/forget/dispute/merge, project merge/split, e2e
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

`/project <name>` reconstructs a project's current state; `/projects` and `/loops` list
projects and open loops. `/remember` shows stored memories with short ids you can pass to the
correction commands: `/forget <id>`, `/dispute <id>`, `/correct <id> <fact>`, `/reassign <id>
<project>`, and `/mergeprojects <a> into <b>`. `/why` shows the full turn diagnostics —
retrieval scores/reasons, project resolution (ranked candidates + any clarifying question),
open loops surfaced, project-state updates, and extraction verdicts (incl. supersessions). The
model providers are deterministic (`MockChatModel`, `MockEmbeddingModel`,
`RuleBasedMemoryExtractor`), so everything runs offline. Real local/hosted providers —
including `LlmMemoryExtractor` — plug in behind the same interfaces.

## Next step

**Phase 6 — Consolidation & evaluation** (roll repeated low-level memories into higher-level
knowledge while preserving evidence, and a repeatable Scenario A–E benchmark suite), per
`docs/IMPLEMENTATION_PLAN.md`.
