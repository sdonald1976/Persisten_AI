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

## Phase 3 — Memory extraction & validation ✅

**Status: complete.** Turns now run an extraction pipeline over the exchange.

**Delivered**
- `IMemoryExtractor` with a rule-based default (`RuleBasedMemoryExtractor`) and an
  LLM-backed implementation (`LlmMemoryExtractor`) behind the same interface.
- `IMemoryPipeline` / `MemoryPipeline` implementing generate → normalize
  (`MemoryNormalizer`) → in-batch dedupe → compare to existing → confidence
  (`ConfidenceCalculator`) → evidence requirement → decide → persist with a
  `MemoryRevision` audit trail. Candidates (`MemoryCandidate`) and decisions
  (`MemoryDecision` / `MemoryExtractionResult`) are surfaced in the turn trace and `/why`.
- Decisions: **Accept** (new), **Merge** (duplicate/confirmation — bumps confidence &
  recency, adds evidence), **Reject** (no evidence / below confidence), **NeedsReview**
  (same subject+predicate *and* same topic, different value — stored as a `Candidate`,
  excluded from retrieval, resolution deferred to Phase 5).
- Contradiction detection requires same slot **and** topic similarity, so unrelated facts
  sharing a predicate aren't false-flagged.

**Acceptance (met)**
- New candidates are proposed after a turn but only persisted when validated. ✅
- Duplicates are detected and merged rather than duplicated. ✅
- Every accepted memory has evidence and a confidence score. ✅

**Tests (added, 15)**
- Memory creation, duplicate detection/merge, evidence-required rejection, low-confidence
  rejection, same-slot change held for review, natural-language extraction, confidence
  calculation, normalizer behavior, LLM JSON parsing.

## Phase 4 — Projects, entities & open loops ✅

**Status: complete.** Projects, decisions, activity, and open loops are first-class; turns
are project-aware and resolve references honestly.

**Delivered**
- First-class `Project` / `ProjectAlias` / `ProjectEvent` / `Decision` / `OpenLoop` records
  with an EF Core `ProjectStore`.
- `IEntityResolver` / `EntityResolver`: ranks candidate projects on alias + name/keyword +
  embedding + recency, picks one only on a clear margin, and asks a clarifying question when
  ambiguous. Qualification is identity-based (recency alone never invents a candidate); no
  silent merges.
- `IProjectContextService` / `ProjectContextService`: resolve → reconstruct
  `ProjectSummary` → surface relevant open loops (boosted for the resolved project). The
  resolved project name feeds the retriever's project-association boost.
- Step 10 (`IProjectUpdater` / `ProjectUpdater`): a newly-accepted planned/in-progress
  episode opens an open loop; an episode reported done closes the best-matching loop and
  logs project activity.
- CLI `/projects`, `/project <name>`, `/loops`; `/why` shows resolution, open loops, and
  project-state updates. The context packet gains project-state, open-loop, and
  clarification sections.

**Acceptance (met)**
- Returning to a project reconstructs its current state (Scenario A). ✅
- Ambiguous references are ranked and a concise clarification is asked when confidence is low (Scenario C). ✅

**Tests (added, 10)**
- Confident resolution, alias-phrase resolution, ambiguous-reference clarification + ranking,
  unknown-reference → no guess, resolver user isolation, project-summary reconstruction,
  open-loop surfacing (by project and by content), project-boosted retrieval, and end-to-end
  open-loop open/close through a turn.

## Phase 5 — Temporal revision & correction ✅

**Status: complete.** Facts change without erasing history, and the user can curate memory.

**Delivered**
- `IMemoryCurator` / `MemoryCurator`: supersede (old kept as `Superseded` + `Validity.Superseded`,
  linked via `SupersededById`), correct (in-place value fix, re-embedded), re-associate project
  (Scenario D), forget (soft-delete + embedding purge), dispute, merge, and resolve-review
  (promote a parked Phase-3 candidate, superseding the conflicting slot fact).
- Pipeline auto-supersession: a same-slot/same-topic contradiction from a **direct user
  statement** supersedes the old value; an inferred one is still parked for review.
- `IProjectCurator` / `ProjectCurator`: merge two project references (reassign children +
  re-point memories + keep the old name as an alias + delete the source) and split a project
  (move specified open loops/aliases to a new one).
- Every operation writes a `MemoryRevision` (or `ProjectEvent`) audit entry; soft-deleted
  memories are filtered at the store boundary and purged from the vector index (embedding
  nulled), so they never resurface via retrieval or similarity.
- CLI: `/forget`, `/dispute`, `/correct`, `/reassign`, `/mergeprojects`; `/remember` shows ids.

**Acceptance (met)**
- Changed preferences keep history but aren't presented as current (Scenario B). ✅
- Corrections re-associate and leave an audit trail; the error doesn't recur (Scenario D). ✅
- Deleted memories never reappear anywhere (retrieval + vector index). ✅

**Tests (added, 12)**
- Direct-user supersession (Scenario B) + inferred contradiction held for review, forget
  (soft-delete + embedding purge + removed from index + audit), dispute, correct, project
  re-association (Scenario D), memory merge, resolve-review accept/reject, curation user
  isolation, project merge (+ merged reference still resolves) and split.

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
