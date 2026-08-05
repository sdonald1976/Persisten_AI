# Persisten_AI — Persistent AI Companion

A conversational AI companion with **durable memory and long-term context**. It uses an
existing language model for generation; its value is in the layers around the model —
memory, continuity, retrieval, temporal reasoning, and project awareness.

> **Guiding principle: build continuity, not consciousness.**

## Status

**All six planned phases are implemented.** The final phase adds **memory consolidation** —
repeated, related low-level memories are rolled up into a higher-level fact (marked as
system-generated, not a direct quote) that keeps links to all its supporting evidence, never
destroys the originals, and won't generalize from one or two remarks — plus a reproducible
**Scenario A–E benchmark suite** that exercises the whole system end-to-end.

The full loop: project-aware turns resolve references honestly (asking to clarify when
ambiguous), retrieve ranked+explained memories and open loops into a bounded,
provenance-labeled context packet, generate a reply, then extract candidate memories through a
validate-before-store pipeline, update project/open-loop state, and revise over time
(supersession, correction, forgetting) — all with an audit trail. Everything runs offline on
deterministic mock/rule-based providers. **All 70 tests pass**, including the five reference
scenarios. Design docs are under `docs/`.

### Reference scenarios (the acceptance benchmark)

| Scenario | Behavior verified |
|----------|-------------------|
| A — Returning to a project | Resolves the right project (not a similar one), resumes continuity, closes the open loop. |
| B — Changed preference | A new direct statement supersedes the old; history is kept but not presented as current. |
| C — Ambiguous reference | Ranks candidates and asks a concise clarifying question instead of guessing. |
| D — Corrected memory | Re-associates to the right project, leaves an audit trail, doesn't recur. |
| E — Long-term continuity | Surfaces the relevant long-term interest from an oblique mention; doesn't inject unrelated facts. |

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
                              /forget, /dispute, /correct, /reassign, /mergeprojects, /consolidate, /why
tests/Companion.Tests         70 tests: retrieval, packet, provenance, isolation, score math,
                              extraction (accept/merge/reject/review/supersede), confidence, normalizer,
                              LLM parsing, resolution + clarification, project summary, open-loop create/close,
                              supersession, correction/forget/dispute/merge, project merge/split,
                              consolidation, and the Scenario A–E benchmark
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

The schema is managed with **EF Core migrations** and applied automatically on startup, so it
upgrades in place as new versions add tables. If you have a local database created before
migrations were introduced, delete it (e.g. `companion.db`) and re-run — it will be recreated,
then reload demo data with `seed`.

`/project <name>` reconstructs a project's current state; `/projects` and `/loops` list
projects and open loops. `/remember` shows stored memories with short ids you can pass to the
correction commands: `/forget <id>`, `/dispute <id>`, `/correct <id> <fact>`, `/reassign <id>
<project>`, and `/mergeprojects <a> into <b>`. `/consolidate` rolls repeated memories into
higher-level knowledge. `/why` shows the full turn diagnostics — retrieval scores/reasons,
project resolution (ranked candidates + any clarifying question), open loops surfaced,
project-state updates, and extraction verdicts (incl. supersessions). The model providers are
deterministic (`MockChatModel`, `MockEmbeddingModel`, `RuleBasedMemoryExtractor`,
`MockSummarizer`), so everything runs offline. Real local/hosted providers — including
`LlmMemoryExtractor` and an LLM summarizer — plug in behind the same interfaces.

## Where next

The six-phase plan in `docs/IMPLEMENTATION_PLAN.md` is complete. Natural follow-ups: wire a
real local/hosted model behind the `IChatModel` / `IEmbeddingModel` / `IMemoryExtractor` /
`ISummarizer` interfaces, and (per the design docs) harden the privacy/export controls and swap
the in-process vector index for a dedicated ANN store.
