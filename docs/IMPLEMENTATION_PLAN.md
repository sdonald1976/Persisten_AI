# Implementation Plan

Work proceeds in small, demonstrable phases. **Do not start a phase before the previous
one runs and its tests pass.** Each phase lists tasks, dependencies, acceptance criteria,
and required tests.

## Phase 0 — Scaffolding (prerequisite)

**Tasks**
- Create the solution and four projects (`Core`, `Infrastructure`, `Cli`, `Tests`).
- Add `.gitignore`, `Directory.Build.props` (nullable on, warnings-as-errors optional),
  and a top-level `README`.
- Wire `Microsoft.Extensions.*` DI, configuration, and logging in the CLI host.

**Dependencies:** none.

**Acceptance:** `dotnet build` and `dotnet test` succeed (with a single placeholder test).

## Phase 1 — Repository assessment ✅

**Status: complete.** See `CURRENT_ARCHITECTURE_ASSESSMENT.md`. Finding: the repository
is empty, so this is a greenfield build. No code was modified during assessment.

## Phase 2 — Minimal vertical slice

Implement the smallest complete loop. Detailed file list in `FIRST_VERTICAL_SLICE.md`.

**Tasks**
1. Domain records needed for the slice + `IMemoryStore`/`IConversationStore` interfaces.
2. EF Core `CompanionDbContext` (SQLite) for conversations, messages, memories, evidence.
3. `MockChatModel` + `MockEmbeddingModel` (deterministic).
4. `SqliteBlobVectorIndex` (cosine over stored embeddings).
5. `Retriever` with hybrid scoring + per-signal explanations.
6. `ContextAssembler` producing a bounded, labeled `ContextPacket`.
7. `Companion` orchestrator: store → detect → retrieve → assemble → generate → store.
8. CLI chat REPL + a `/why` diagnostics view rendering the `TurnTrace`.
9. A seed command that inserts a few memories, one project, and one open loop.

**Dependencies:** Phase 0.

**Acceptance**
- The CLI runs a full turn end-to-end using the mock model (no network).
- Retrieval returns a **ranked** set with visible scores and match reasons.
- The context packet is **bounded** (respects a token budget) and clearly labeled.
- `/why` shows retrieved memories, scores, reasons, and what was excluded.

**Tests**
- Retrieval ranking (relevant memory ranks above noise).
- Context-packet construction (budget respected; sections labeled).
- Provenance (each seeded memory links to its source message).
- User isolation (a second user's data never appears).

## Phase 3 — Memory extraction & validation

**Tasks**
- `IMemoryExtractor` + a rule-based mock extractor; LLM-backed extractor behind the same interface.
- The 8-step pipeline: generate → normalize → dedup → compare → confidence → evidence →
  decide (accept/reject/merge/review) → persist with revisions.

**Dependencies:** Phase 2.

**Acceptance**
- New candidates are proposed after a turn but only persisted when validated.
- Duplicates are detected and merged rather than duplicated.
- Every accepted memory has evidence and a confidence score.

**Tests**
- Memory creation, duplicate detection, semantic updates, confidence calculation,
  evidence-required rejection.

## Phase 4 — Projects, entities & open loops

**Tasks**
- Full `Project` / `ProjectEvent` / `Decision` / `OpenLoop` models + stores.
- `IEntityResolver` (alias match + embeddings + confidence; no silent merges).
- Project detection feeding retrieval; open-loop boost in scoring.
- Clarification flow for ambiguous references (Scenario C).

**Dependencies:** Phase 3.

**Acceptance**
- Returning to a project reconstructs its current state (Scenario A).
- Ambiguous references are ranked and a concise clarification is asked when confidence is low (Scenario C).

**Tests**
- Project association, entity aliases, open-loop retrieval, ambiguous-reference ranking.

## Phase 5 — Temporal revision & correction

**Tasks**
- Supersession, contradiction handling, validity transitions.
- User corrections: correct / delete (soft) / supersede / merge / split, each writing a
  `MemoryRevision` audit entry.
- Deletion filtering at the store boundary (also purges from the vector index).

**Dependencies:** Phase 4.

**Acceptance**
- Changed preferences keep history but aren't presented as current (Scenario B).
- Corrections re-associate and leave an audit trail; the error doesn't recur (Scenario D).
- Deleted memories never reappear anywhere.

**Tests**
- Temporal supersession, conflicting memories, deletion (incl. embedding purge),
  provenance/audit trail.

## Phase 6 — Consolidation & evaluation

**Tasks**
- Consolidation command: roll repeated low-level memories into higher-level knowledge,
  **preserving links to supporting evidence** and never destroying originals.
- Reproducible evaluation harness running Scenarios A–E against a seeded multi-month
  fictional history, with pass/fail assertions.

**Dependencies:** Phase 5.

**Acceptance**
- Consolidated memories cite their supporting episodes; originals are retained.
- The benchmark suite runs repeatably and all five scenarios pass.

**Tests**
- Consolidation preserves evidence; end-to-end Scenarios A–E; long-term continuity (Scenario E).

## Cross-cutting requirements (all phases)

- Nullable reference types on; async + `CancellationToken` on I/O; structured logging;
  configuration binding; explicit error handling (no silent catches); no hidden globals.
- Model calls are mockable so tests are deterministic.
- A small seeded dataset representing several months of fictional conversation history
  is introduced in Phase 2 and grown through Phase 6.

## Test matrix (aggregate)

| Area | Introduced |
|------|-----------|
| memory creation | Phase 3 |
| duplicate detection | Phase 3 |
| semantic updates | Phase 3 |
| temporal supersession | Phase 5 |
| project association | Phase 4 |
| entity aliases | Phase 4 |
| conflicting memories | Phase 5 |
| deletion (incl. embedding purge) | Phase 5 |
| retrieval ranking | Phase 2 |
| open-loop retrieval | Phase 4 |
| context-packet construction | Phase 2 |
| user isolation | Phase 2 |
| provenance | Phase 2 |
| end-to-end resume-old-discussion | Phase 6 |
