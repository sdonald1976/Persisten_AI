# First Vertical Slice — Proposed Files

> **Historical planning document.** This was the pre-implementation proposal for the Phase 2
> vertical slice, written when the repository was empty. The slice (and much more) has since been
> built — see `README.md` for the current state. Kept for provenance.

This is the exact set of files the **Phase 2** vertical slice would add. It is scoped to
the smallest complete loop that produces real value and is fully runnable/testable with
**mock models** (no network, no GPU).

Since the repository started empty, every file below was **added** (none changed).

## Solution & build

| File | Purpose |
|------|---------|
| `Persisten_AI.sln` | Solution referencing the four projects. |
| `Directory.Build.props` | `net9.0`, `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`, `<LangVersion>latest</LangVersion>`. |
| `.gitignore` | Standard .NET ignore (bin/obj, `*.db`, user secrets). |
| `README.md` | How to build, seed, and run the CLI. |

## `src/Companion.Core` (pure domain + interfaces + logic)

| File | Purpose |
|------|---------|
| `Companion.Core.csproj` | Class library, no I/O dependencies. |
| `Domain/UserProfile.cs` | Identity + isolation root. |
| `Domain/Conversation.cs` | Conversation record. |
| `Domain/Message.cs` | Message record (role, content, `ReplyToId`, tokens, timestamp). |
| `Domain/SemanticMemory.cs` | Durable fact/preference with confidence + validity. |
| `Domain/EpisodicMemory.cs` | Event with `EventTime` / `MentionedAt` / `CreatedAt`. |
| `Domain/MemoryEvidence.cs` | Provenance link memory → message. |
| `Domain/Enums.cs` | `MemoryKind`, `MemoryStatus` (lifecycle), `Validity`, `TimePrecision`, `MessageRole`. |
| `Domain/RetrievalResult.cs` | One ranked hit + per-signal score breakdown + reason. |
| `Domain/ContextPacket.cs` | Labeled, bounded context sent to the model. |
| `Domain/TurnTrace.cs` | Per-turn diagnostics record. |
| `Abstractions/IChatModel.cs` | Chat completion role. |
| `Abstractions/IEmbeddingModel.cs` | Embeddings role. |
| `Abstractions/IVectorIndex.cs` | Similarity search over embeddings. |
| `Abstractions/IConversationStore.cs` | Persist/read conversations + messages. |
| `Abstractions/IMemoryStore.cs` | Persist/read memories + evidence (user-scoped). |
| `Abstractions/IRetriever.cs` | Ranked, explained retrieval. |
| `Abstractions/IContextAssembler.cs` | Build the `ContextPacket`. |
| `Abstractions/ICompanion.cs` | Orchestrates one turn. |
| `Services/Retriever.cs` | Hybrid scoring (similarity + keyword + recency + importance + confidence) with explanations. |
| `Services/ContextAssembler.cs` | Bounded, labeled packet under a token budget. |
| `Services/Companion.cs` | The turn pipeline (store→detect→retrieve→assemble→generate→store→trace). |
| `Services/ScoreMath.cs` | Pure cosine + recency-decay + weighted-sum helpers (unit-tested). |
| `CompanionOptions.cs` | Config-bound weights, token budget, top-K. |

## `src/Companion.Infrastructure` (I/O implementations)

| File | Purpose |
|------|---------|
| `Companion.Infrastructure.csproj` | References `Core`, EF Core SQLite. |
| `Persistence/CompanionDbContext.cs` | EF Core context (users, conversations, messages, memories, evidence). |
| `Persistence/Configurations/*.cs` | Entity type configs + indexes (incl. `UserId`). |
| `Persistence/ConversationStore.cs` | `IConversationStore` impl. |
| `Persistence/MemoryStore.cs` | `IMemoryStore` impl (filters soft-deleted). |
| `Vector/SqliteBlobVectorIndex.cs` | `IVectorIndex` — cosine over stored embedding BLOBs. |
| `Models/MockChatModel.cs` | Deterministic canned/templated responses. |
| `Models/MockEmbeddingModel.cs` | Deterministic hash-based embeddings. |
| `DependencyInjection.cs` | `AddCompanionInfrastructure(...)` extension. |

## `src/Companion.Cli` (front-end)

| File | Purpose |
|------|---------|
| `Companion.Cli.csproj` | References `Core` + `Infrastructure`. |
| `Program.cs` | Host builder, DI, config, logging. |
| `ChatLoop.cs` | REPL: read → run turn → print reply. |
| `Commands/SeedCommand.cs` | Insert seed memories, one project, one open loop. |
| `Commands/WhyCommand.cs` | Render the last `TurnTrace` (scores, reasons, exclusions, packet). |
| `appsettings.json` | Weights, token budget, DB path, model selection (`Mock`). |

## `tests/Companion.Tests`

| File | Purpose |
|------|---------|
| `Companion.Tests.csproj` | xUnit; references `Core` + `Infrastructure`. |
| `Fixtures/SeedData.cs` | A few months of fictional history (Jetson project, low-carb, buoy project). |
| `Fixtures/SqliteFixture.cs` | Fresh SQLite file/in-memory DB per test. |
| `RetrievalRankingTests.cs` | Relevant memory outranks noise; scores explained. |
| `ContextPacketTests.cs` | Budget respected; sections labeled. |
| `ProvenanceTests.cs` | Every memory links to its source message(s). |
| `UserIsolationTests.cs` | User B's data never surfaces for user A. |
| `ScoreMathTests.cs` | Cosine + recency-decay + weighted-sum correctness. |

## What this slice deliberately excludes

Deferred to later phases (to avoid over-building):

- LLM-backed extraction/summarization/reranking (Phase 3+; mocks used here).
- Full project/entity/open-loop management and ambiguity clarification (Phase 4).
- Supersession, correction, merge/split, deletion audit (Phase 5).
- Consolidation and the Scenario A–E benchmark suite (Phase 6).
- A real vector database (the BLOB cosine index is intentionally simple).

## Definition of done for the slice

- `dotnet build` and `dotnet test` pass.
- `dotnet run --project src/Companion.Cli -- seed` populates the store.
- A chat turn retrieves ranked, explained memories and produces a mock response.
- `/why` shows exactly what was retrieved, the scores, why, what was excluded, and the
  context packet that was sent to the model.
