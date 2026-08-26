# DATABASE_UTILIZATION_AUDIT.md

Read-only audit. No source, schema, migration, configuration, database content, test, or
running process was altered. Ava ran throughout (`companion-api` PID 21228, started
08:16:57) and was never stopped or restarted. Every live query used
`file:...?mode=ro` and read only metadata, counts, null/non-null counts, timestamp ranges,
and content-presence booleans. **No message text, memory text, evidence, prompt, reply, tool
result, frame content, preference statement, or content-derived hash appears in this
document.**

**Inspected commit:** `2d7ec93a711332dd2a21f43849cd9ac30595baee` (branch
`audit/database-utilization`, cut from tag `refactor-baseline`; working tree clean).
**Database:** the live application SQLite file under the API project directory — single-file,
single-user deployment, WAL. Path details omitted as unnecessary.
**Migrations applied:** 36. **Date:** 2026-08-26.

**Fact** marks something read directly from the tree or the live database at that SHA.
**Recommendation** marks my judgement. Where evidence was insufficient I say *unresolved*
rather than guessing.

---

## A. Executive summary

**43 physical tables = 41 application tables + 2 EF infrastructure tables**
(`__EFMigrationsHistory`, `__EFMigrationsLock`). No views. 56 named indexes.
`PRAGMA foreign_key_check` reports **0 violations**.

| Classification | Count | Tables |
|---|---:|---|
| Active | 17 | Messages, Conversations, SemanticMemories, EpisodicMemories, Evidence, Experiences, Reflections, Curiosities, AttentionItems, CompanionPreferences, Capabilities, Projects, ProjectEvents, KnowledgeGaps, ToolCalls, ModelCalls, TurnRecords |
| Newly integrated / awaiting natural traffic | 3 | FrameSessions, UserPreferences, CompanionMoodTransitions |
| Dormant-by-design | 8 | Anticipations, OpenLoops, OutboundMessages, PendingClarifications, Decisions, ProjectAliases, Feedback, MemoryAssociations |
| Unwired | 5 | ActivityBranches, FrameBoundaries, Concepts, ConceptAliases, ConceptAssertions |
| Write-only (no production reader) | 3 | Revisions, ShadowComparisons *(partly)*, SharedExperiencePerspectives *(read on turn, see F)* |
| Read-only (no production writer) | 0 | — |
| Test-only but production-persisted | 0 | — |
| Duplicated state | 0 confirmed | see §H — four candidate pairs examined, none judged accidental duplication |
| Orphaned | 0 | — |
| Uncertain | 5 | Procedures, ProcedureSteps, ProcedureRevisions, Users, EmotionalSignals |

**Tables with privacy or lifecycle concerns: 9.** Detail in §G. The five that matter most:
`ShadowComparisons` (holds user text, no `UserId`, forgotten by **substring match**),
`Experiences` (84 rows of user-derived text, no `/forget` path), `AttentionItems`,
`Reflections`, `Curiosities` (user-derived, no `/forget` path), and `FrameSessions` (no
cleanup path at all).

**The single most important finding:** `/forget` reaches **6** of the **14** tables holding
user-derived content. The R-01 work closed the frame gap; it did not close the others, and
the audit that produced R-01 did not look for them.

---

## B. Inventory reconciliation

Every number reconciles exactly. There are no unexplained differences.

| Count | Value | Source |
|---|---:|---|
| `DbSet<>` properties on `CompanionDbContext` | **41** | Fact — declaration scan |
| EF model entity types | **41** | Fact — `tests/Companion.Tests/Goldens/ef-model.txt` |
| Physical application tables | **41** | Fact — `sqlite_master` |
| EF infrastructure tables | **2** | `__EFMigrationsHistory` (36 rows), `__EFMigrationsLock` (0 rows) |
| **Total physical tables** | **43** | |
| Views | **0** | Fact |
| Named indexes | **56** | Fact |

**41 = 41 = 41.** Every `DbSet` maps to exactly one EF entity, and every entity maps to
exactly one physical table. Specifically (Fact):

- **No join tables**, implicit or explicit. No many-to-many relationships are configured.
- **No table sharing.** No two entities map to the same table.
- **No owned entities / no complex types.** Collections that could have been owned entities
  are instead serialized JSON columns on their parent (`FrameSessions.TransitionLogJson`,
  `FrameSessions.CharactersJson`, `FrameSessions.AppliedKeysJson`,
  `TurnRecords.Retrieved`, `TurnRecords.Decisions`, `TurnRecords.Plan`).
- **No tables exist physically without an EF entity** (beyond the two EF infrastructure
  tables, which are owned by EF Core itself and are correctly not modelled).
- **No EF entities lack a physical table.**
- **No migration remnants.** Every table created by a migration is still in the model; the
  only column ever dropped is `FrameBoundaries.EvidenceStatement` (migration
  `20260826110929`), and it is absent from both the model and the physical schema.

Three entity names differ from their table names, which is deliberate and configured:
`MemoryEvidence → Evidence`, `MemoryRevision → Revisions`, `UserProfile → Users`.

**Foreign keys are unusually sparse: only 3 are declared** (Fact) —
`Messages.ConversationId → Conversations.Id`, `ProcedureRevisions.ProcedureId → Procedures.Id`,
`ProcedureSteps.ProcedureId → Procedures.Id`, all `NOT NULL`, all `CASCADE`. Every other
inter-table relationship (memory → evidence, curiosity → reflection, gap → curiosity,
perspective → experience, event → project, signal → message) is an **unenforced Guid
reference**. See anomaly 13 in §F.

---

## C. Complete table matrix

Column key: **W** = production writer path, **R** = production reader path, **Iso** =
user-isolation key, **F** = `/forget` behaviour, **Cls** = classification, **Rec** =
recommendation, **Conf** = confidence.

Introducing migrations are abbreviated (`Initial` = `20260805183952_InitialCreate`).

### Conversation core

| Table | Entity / DbSet | Migration | Rows | Range | W | R | Iso | F | Retention | Cls | Rec | Conf |
|---|---|---|---:|---|---|---|---|---|---|---|---|---|
| **Messages** | `Message` | Initial | 88 | full history | `ConversationStore.AddMessageAsync` ← `Companion.StoreMessageAsync` (`Companion.cs:1566`), every turn | `GetRecentMessagesAsync` ← `Companion.cs:350`, every turn | `UserId` + FK `ConversationId` | **Not deleted.** `/forget` deletes the *memory*, not the transcript | none | Active | keep unchanged | High |
| **Conversations** | `Conversation` | Initial | 13 | full history | `StartConversationAsync` ← `Program.cs` `/conversations` | `GetConversationAsync` ← `Companion.cs:294`, every turn | `UserId` | not reached | none | Active | keep unchanged | High |
| **PendingClarifications** | `PendingClarification` | `20260806145112` | 0 | — | `AddAsync` ← `Companion.RequestClarificationAsync` (`:199`) | `GetActiveAsync` ← `Companion.cs`, every turn | `UserId` | not reached | none | Dormant-by-design | keep and document | High |

`PendingClarifications` is empty because ambiguity is a *control-flow state* that only
materialises on a materially ambiguous project reference; 88 messages produced none. Writer
and reader are both on the normal turn path.

### Memory

| Table | Entity | Migration | Rows | W | R | Iso | F | Retention | Cls | Rec | Conf |
|---|---|---|---:|---|---|---|---|---|---|---|---|
| **SemanticMemories** | `SemanticMemory` | Initial | 5 | `MemoryStore.AddSemanticAsync` ← `MemoryPipeline.ProcessAsync` ← `Companion.cs:1049` | `Retriever` → `Companion.cs:401` | `UserId` | **soft delete + embedding purge** (`MemoryCurator:137`) | none | Active | keep unchanged | High |
| **EpisodicMemories** | `EpisodicMemory` | Initial | 5 | same pipeline | same retriever | `UserId` | same | none | Active | keep unchanged | High |
| **Evidence** | `MemoryEvidence` | Initial | 10 | `MemoryStore.AddEvidenceAsync` ← pipeline | `GetEvidenceAsync` ← `MemoryCurator.ForgetAsync:123`, `Companion.cs` | `UserId` | read to *drive* forgetting; rows themselves not deleted | none | Active | investigate with runtime evidence | Medium |
| **Revisions** | `MemoryRevision` | Initial | 10 | `MemoryStore`, `ProcedureStore`, `ConceptStore` | **none in production** — `IMemoryStore.GetRevisionsAsync` and `IProcedureStore.GetRevisionsAsync` have **test-only callers** | `UserId` | not reached | none | **Write-only** | keep and document | High |
| **MemoryAssociations** | `MemoryAssociation` | `20260811191812` | 0 | `AddValidatedAsync` ← `Reflector` (background) | `GetFromSourcesAsync` ← `AssociativeRecallService` ← `Companion.cs:402`, every turn | `UserId` | not reached | none | Dormant-by-design | keep and document | High |

**`Revisions` is write-only in production** (Fact). Three stores append to it; no production
code path reads it back. `Before` is **populated in 0 of 10 rows** — a column no writer fills
(anomaly 18). *Recommendation:* keep — it is an append-only audit trail whose value is
answering "what changed" after the fact, and the API to read it is a legitimate gap rather
than a reason to stop writing. But the unpopulated `Before` column is a real defect: a reader
built against it would silently get nothing.

### Reflection and attention

| Table | Entity | Migration | Rows | W | R | Iso | F | Retention | Cls | Rec | Conf |
|---|---|---|---:|---|---|---|---|---|---|---|---|
| **Reflections** | `Reflection` | `20260810173155` | 16 | `ReflectionStore` ← `Reflector` (background, `ReflectionWorker`) | `Companion.cs:502` `GetNextToVoiceAsync`, every turn | `UserId` | **not reached** | none | Active | **add lifecycle/forgetting** | High |
| **Curiosities** | `Curiosity` | `20260810173155` | 10 | `ReflectionStore` ← `Reflector` | `Companion.cs:502`; `SleepCycle`; `GapStore` | `UserId` | **not reached** | none | Active | **add lifecycle/forgetting** | High |
| **AttentionItems** | `AttentionItem` | `20260811191812` | 4 | `AttentionStore.UpsertAsync` ← `AttentionService.CaptureTurnAsync` ← `Companion.cs:1055`, every remembered turn | `GetActiveAsync` ← `Companion.cs:522`, every turn | `UserId` | **not reached** | `ExpireOldAsync` exists; called from `AttentionService` | Active | **add lifecycle/forgetting** | High |
| **Experiences** | `Experience` | `20260813153459` | **84** | `ExperienceStore.AddAsync` ← turn + `Reflector` + `WorldWorker` | `GetSinceAsync` ← `Reflector` (background only) | `UserId` | **not reached** | `PruneAsync` ← `SleepCycle`, **30 days** | Active | **add lifecycle/forgetting** | High |
| **SharedExperiencePerspectives** | `SharedExperiencePerspective` | `20260811191812` | 1 | `AddValidatedAsync` ← `Reflector` (background) | `GetForExperiencesAsync` ← `Companion.cs:1546`, every turn | `UserId` | **not reached** | none | Active | **add lifecycle/forgetting** | High |

All five hold user-derived text (`Reflections.Musing`, `Curiosities.Question`/`.Reason`,
`AttentionItems.Subject`/`.Summary`, `Experiences.Text`, `SharedExperiencePerspectives.Summary`/`.Evidence`).
None is reachable from `/forget`. Only `Experiences` has any retention at all.

### Mood, relationship, preference

| Table | Entity | Migration | Rows | W | R | Iso | F | Cls | Rec | Conf |
|---|---|---|---:|---|---|---|---|---|---|---|
| **EmotionalSignals** | `EmotionalSignal` | `20260810154458` | 3 | `EmotionStore.AddSignalAsync` ← `Companion.CaptureMoodAsync` (`:1597`), every turn | `GetRecentSignalsAsync` ← `CompanionStateTracker`, `MemoryCurator` | `UserId` | **`ForgetByEvidenceAsync`, exact `MessageId`** ✔ | `PruneAsync` ← `SleepCycle`, **180 days** | Uncertain | investigate with runtime evidence | Medium |
| **CompanionMoodTransitions** | `CompanionMoodTransition` | `20260825103307` | **0** | `AppendAsync` ← `CompanionStateTracker.NudgeAsync` ← `Companion.cs`, every turn with valence | `GetLatestAsync` ← `CompanionStateTracker`, `Reflector` | `UserId` | **`CompactForgottenAsync`, exact evidence id** ✔ | none | Newly integrated / awaiting traffic | keep and document | High |
| **CompanionPreferences** | `CompanionPreference` | `20260811155621` | 5 | `ApplySignalAsync` ← `Reflector` (background) | `GetAllAsync` ← `Companion.cs`, endpoints, tools | `UserId` | **not reached** | none | Active | **add lifecycle/forgetting** | High |
| **UserPreferences** | `UserPreferenceRecord` | `20260825080829` | **0** | `Agent.cs` on an explicit preference command | `GetActiveAsync` ← `Companion.cs:691`, every shadow-gated turn | `UserId` | **`InvalidateByForgottenEvidenceAsync`, exact identity** ✔ | none | Newly integrated / awaiting traffic | keep unchanged | High |
| **Users** | `UserProfile` | Initial | 1 | `ProfileStore` ← `/persona`, `/user`, `/identity` endpoints; `GetOrCreateAsync` ← `Companion.cs:304` | same | `UserId` (PK) | not reached | none | Uncertain | investigate with runtime evidence | Medium |

`Users` is *Uncertain* only because `IProfileStore.SetDisplayNameAsync` has **no caller
anywhere** (Fact) — a method on an otherwise-active store.

`EmotionalSignals` is *Uncertain* because it is the one table whose forget path redacts
in place rather than deleting: the row survives as metadata by design (Phase 0), so "reached
by `/forget`" is true but "removed by `/forget`" is not, and which of those the retention
promise means is a contract question I cannot settle from code alone.

### Frames

| Table | Entity | Migration | Rows | W | R | Iso | F | Cls | Rec | Conf |
|---|---|---|---:|---|---|---|---|---|---|---|
| **FrameSessions** | `FrameSession` | `20260825141447` | 1 | `ApplyAsync` ← `Companion.cs:~690`, unconditional since R-02 | `GetActiveAsync` ← same block, every turn | `UserId` + `ConversationId` | **`ForgetByEvidenceAsync`, exact `MessageId`** ✔ (R-01) | Newly integrated / awaiting traffic | **add lifecycle** | High |
| **FrameBoundaries** | `FrameBoundaryRecord` | `20260825141447` | **0** | `AddBoundaryAsync` — **no production caller** | `GetActiveBoundariesAsync` — **no production caller** | `UserId` + `SceneRef` | reached by the same forget call ✔ | **Unwired** | wire correctly | High |

`FrameSessions.PruneAsync` exists and has **no production caller** (Fact) — `SleepCycle`
prunes diagnostics, experiences, emotional signals and gaps, and does not prune frames. So
frame sessions and their transition logs **grow without bound**.

`FrameBoundaries` is fully implemented — store methods, isolation rules, forget semantics,
tests — and nothing in production creates or reads a boundary. The plan/4 spec treats
scene-scoped boundaries as the mechanism by which a user's in-scene instruction is obeyed;
today that mechanism is inert.

### Activities and procedures

| Table | Entity | Migration | Rows | W | R | Iso | F | Cls | Rec | Conf |
|---|---|---|---:|---|---|---|---|---|---|---|
| **ActivityBranches** | `ActivityBranchRecord` | `20260824185440` | **0** | `UpsertAsync` ← `ActivityShadowObserver` — **class never registered, never constructed** | `GetAsync` ← same dead class | `UserId` | `ForgetAsync` exists, **no production caller** | **Unwired** | wire correctly *or* deprecate | High |
| **Procedures** | `Procedure` | `20260811191812` | 0 | `AddOrUpdateFromTeachingAsync` ← `Companion.cs:1057`, every remembered turn | `SearchAsync` ← `Companion.cs:1534`, every turn | `UserId` | not reached | Uncertain | keep and document | Medium |
| **ProcedureSteps** | `ProcedureStep` | `20260811191812` | 0 | `ProcedureStore` (with parent) | `ProcedureStore` | `UserId` + FK | not reached | Uncertain | keep and document | Medium |
| **ProcedureRevisions** | `ProcedureRevision` | `20260811191812` | 0 | `ProcedureStore` | **none in production** (test-only) | `UserId` + FK | not reached | Uncertain | keep and document | Medium |

`Procedures` is *Uncertain* rather than dormant because both writer and reader are on the
normal turn path and it is still empty after 88 messages — meaning either no teaching-shaped
turn occurred, or the detector never fires. I cannot distinguish those without reading
message content, which this audit is not permitted to do. **Unresolved.**

### Projects

| Table | Entity | Migration | Rows | W | R | Iso | F | Cls | Rec | Conf |
|---|---|---|---:|---|---|---|---|---|---|---|
| **Projects** | `Project` | Initial | 1 | `ProjectStore` ← `ProjectUpdater.ApplyAsync` ← `Companion.cs:1050` | `ProjectContextService` ← `Companion.cs`, every turn | `UserId` | not reached | Active | keep unchanged | High |
| **ProjectEvents** | `ProjectEvent` | Initial | 1 | `ProjectStore` ← `ProjectUpdater` | `ProjectContextService` | `UserId` | not reached | Active | keep unchanged | High |
| **ProjectAliases** | `ProjectAlias` | Initial | 0 | `ProjectStore` | `ProjectStore` resolution | `UserId` | not reached | Dormant-by-design | keep and document | High |
| **OpenLoops** | `OpenLoop` | Initial | 0 | `ProjectStore` ← `ProjectUpdater` | `ContextAssembler`, `CompanionTools`, endpoints | `UserId` | not reached | Dormant-by-design | keep and document | High |
| **Decisions** | `Decision` | Initial | 0 | `ProjectStore` ← `ProjectUpdater` | `ContextPacketRenderer`, `CompanionTools`, endpoints | `UserId` | not reached | Dormant-by-design | keep and document | High |

### Knowledge

| Table | Entity | Migration | Rows | W | R | Iso | F | Cls | Rec | Conf |
|---|---|---|---:|---|---|---|---|---|---|---|
| **KnowledgeGaps** | `KnowledgeGap` | `20260820164629` | 1 | `GapStore.ObserveAsync` ← `Companion.cs:1094`, every turn | `GetOpenAsync`/`GetRecentAsync` ← turn, `Greeter`, endpoints | `UserId` | **not reached** | `ExpireStaleAsync` ← `SleepCycle` | Active | **add lifecycle/forgetting** | High |
| **Concepts** | `Concept` | `20260820161742` | **0** | `ConceptStore` ← `ConceptKnowledge.LearnFromAsync` ← `Companion.cs:1067` | `LookupAsync` ← `Companion.cs:434` | `UserId` | not reached | **Unwired** | investigate with runtime evidence | Medium |
| **ConceptAliases** | `ConceptAlias` | `20260820161742` | **0** | `ConceptStore` | `ConceptStore` | `UserId` | not reached | **Unwired** | investigate with runtime evidence | Medium |
| **ConceptAssertions** | `ConceptAssertion` | `20260820161742` | **0** | `ConceptStore` | `ConceptStore`, `MemoryStore` | `UserId` | not reached | **Unwired** | investigate with runtime evidence | Medium |

The concept tables are classified *Unwired* rather than dormant because `IConceptKnowledge`
is injected as `IConceptKnowledge? concepts = null` (`Companion.cs:106`) — an optional
dependency. It **is** registered, so the path is live; but every concept store method's only
caller is `ConceptKnowledge` itself, and the whole subsystem produced zero rows across 88
messages. Whether the gate is registration, an option, or simply no qualifying turn is
**unresolved** without reading message content.

### Outreach and feedback

| Table | Entity | Migration | Rows | W | R | Iso | F | Cls | Rec | Conf |
|---|---|---|---:|---|---|---|---|---|---|---|
| **Anticipations** | `Anticipation` | `20260811142101` | 0 | `AnticipationStore.AddAsync` ← `Companion.CaptureAnticipationAsync` (`:1647`), every turn | `GetOpenAsync` ← turn, `OutreachService`, `Greeter`, tools | `UserId` | not reached | Dormant-by-design | keep and document | High |
| **OutboundMessages** | `OutboundMessage` | `20260811115608` | 0 | `OutreachStore.AddAsync` ← `OutreachService` (background) | `GetLastSentAtAsync` ← `OutreachService` | `UserId` | not reached | Dormant-by-design | keep and document | High |
| **Feedback** | `FeedbackRecord` | `20260806134105` | 0 | `FeedbackStore.AddAsync` ← `Program.cs` `/feedback` | `CountAsync` ← `Agent` | `UserId` | not reached | Dormant-by-design | keep and document | High |

`OutboundMessages` is empty because outreach requires an `IOutboundChannel` (`NtfyChannel`)
and an anticipation to act on; neither has occurred. Legitimate dormant capacity.

### Diagnostics and observability

| Table | Entity | Migration | Rows | W | R | Iso | F | Retention | Cls | Rec | Conf |
|---|---|---|---:|---|---|---|---|---|---|---|---|
| **ModelCalls** | `ModelCallRecord` | `20260812141652` | **357** | `RecordModelCallAsync` ← `LoggingModelDecorators`, every model call | `GetModelStatsAsync` ← `/diagnostics/models` | **none** | not reached | `PruneAsync` ← `SleepCycle`, **30 days** | Active | keep unchanged | High |
| **ToolCalls** | `ToolCallRecord` | `20260812141652` | 5 | `RecordToolCallAsync` ← `ToolLoop` | `GetRecentToolCallsAsync` ← `/diagnostics/tools` | `UserId` | not reached | same 30-day prune | Active | keep unchanged | High |
| **TurnRecords** | `TurnRecord` | `20260820145159` | 16 | `RecordTurnAsync` ← `Companion.cs:1296`, every turn | `GetRecentTurnsAsync` ← `/diagnostics/turns` | `UserId` | **not reached** | same 30-day prune | Active | keep and document | High |
| **ShadowComparisons** | `ShadowComparison` | `20260817124002` | 33 | `RecordAsync` ← turn, `CognitiveCapture`, `GapPromoter`, `RendererShadowService` | `GetAgreementAsync`/`GetDisagreementsAsync`/`GetCapturesAsync` ← `/diagnostics/shadow*` | **none** | **`ForgetCapturesAsync`, SUBSTRING match** ⚠ | none | Active | **add lifecycle**; see §G | High |
| **Capabilities** | `CapabilityDescriptor` | `20260811191812` | 10 | `CapabilityRegistry` ← `ModelPreflightWorker` | `RenderSummaryAsync` ← `Companion.cs:524`, every turn | **none** (global) | not reached | none | Active | keep unchanged | High |

`ModelCalls` carries **no prompt or completion text** — only `PromptChars`,
`CompletionChars`, `PromptTokens`, `CompletionTokens` (Fact). It is safe telemetry despite
being the largest table and having no `UserId`.

`TurnRecords` **does** carry user-derived text (`UserPreview`, `AssistantPreview`,
`RetrievalQuery`, `Retrieved`, `Plan`), and its privacy mechanism is working: **5 of 16 rows
have all five columns null** (Fact), consistent with the documented skip on
private/sensitive/in-character turns. It is nonetheless unreachable from `/forget`, and is
bounded only by the 30-day prune.

---

## D. Production data-flow map

```
TURN PATH (Companion.CompleteTurnAsync, every turn)
  read  Conversations, Messages, Users, Capabilities, AttentionItems,
        SemanticMemories, EpisodicMemories, Evidence, MemoryAssociations,
        Reflections, Curiosities, CompanionPreferences, UserPreferences,
        Procedures, SharedExperiencePerspectives, KnowledgeGaps, FrameSessions,
        Anticipations, OpenLoops, Decisions, Projects, ProjectEvents, Concepts
  write Messages, FrameSessions, EmotionalSignals, CompanionMoodTransitions,
        AttentionItems, KnowledgeGaps, Anticipations, Experiences, TurnRecords,
        ShadowComparisons, Procedures, SemanticMemories/EpisodicMemories/Evidence
        (via MemoryPipeline), Projects/ProjectEvents/OpenLoops/Decisions
        (via ProjectUpdater), Revisions

BACKGROUND (ReflectionWorker -> Reflector; SleepCycle; OutreachWorker; WorldWorker)
  Reflector      write Reflections, Curiosities, CompanionPreferences,
                       MemoryAssociations, SharedExperiencePerspectives, Experiences
                 read  Experiences, Curiosities, CompanionMoodTransitions
  SleepCycle     prune ModelCalls+ToolCalls+TurnRecords (30d), Experiences (30d),
                       EmotionalSignals (180d); expire Anticipations, KnowledgeGaps
  OutreachService write OutboundMessages   read Anticipations
  ModelPreflight  write Capabilities

API (Program.cs + *Endpoints.cs)
  Conversations, Users, CompanionPreferences, UserPreferences, OpenLoops,
  Decisions, Anticipations, KnowledgeGaps, ModelCalls, ToolCalls, TurnRecords,
  ShadowComparisons

NO PRODUCTION READER
  Revisions, ProcedureRevisions

NO PRODUCTION WRITER OR READER (unwired)
  ActivityBranches (observer class never registered)
  FrameBoundaries  (Add/GetActiveBoundaries have no production caller)
```

---

## E. Empty-table findings

**16 of 41 application tables are empty.** Each is answered individually.

| Table | Writer reachable? | Activation occurred in 88 msgs? | Verdict |
|---|---|---|---|
| **ActivityBranches** | **No** — `ActivityShadowObserver` is never registered in DI and never constructed | n/a | **Unwired.** A no-op registration is not swallowing writes; there is simply no caller. Removing the table would eliminate the intended activity-branch capability, which is a recorded runtime-before-canary blocker. *Keep; wire or deprecate deliberately.* |
| **FrameBoundaries** | **No** — `AddBoundaryAsync` has no production caller | n/a | **Unwired.** Fully implemented and tested; nothing creates a boundary. Removing it would eliminate the plan/4 in-scene boundary mechanism. *Keep; wire.* |
| **Concepts** | Yes (`ConceptKnowledge.LearnFromAsync` ← turn) | **Unresolved** — would require reading message content | Awaiting traffic **or** silently never triggering. *Investigate with runtime evidence.* |
| **ConceptAliases** | Yes, with parent | Unresolved | As above |
| **ConceptAssertions** | Yes, with parent | Unresolved | As above |
| **CompanionMoodTransitions** | Yes — `NudgeAsync` ← every turn carrying valence | Partially: `EmotionalSignals` has 3 rows but all have `Valence` recorded and no transition exists | **Newly integrated (2026-08-25), awaiting traffic.** Table is one day old and the mood path requires a non-null valence nudge. *Keep; recheck after natural traffic.* |
| **UserPreferences** | Yes — `Agent.cs` on an explicit preference command | No such command issued | **Newly integrated (2026-08-25), awaiting traffic.** Correct dormancy. *Keep.* |
| **Procedures** | Yes — every remembered turn | Unresolved | Writer and reader both on the turn path; empty after 88 messages. *Investigate.* |
| **ProcedureSteps** | Yes, with parent | Unresolved | As above |
| **ProcedureRevisions** | Yes, with parent | Unresolved | As above; also **no production reader** |
| **Anticipations** | Yes — `CaptureAnticipationAsync` every turn | No qualifying utterance | **Legitimate dormant capacity.** |
| **OpenLoops** | Yes — `ProjectUpdater` | No open loop detected | Legitimate dormant capacity |
| **Decisions** | Yes — `ProjectUpdater` | No decision detected | Legitimate dormant capacity |
| **ProjectAliases** | Yes — `ProjectStore` | Only one project, no alias | Legitimate dormant capacity |
| **Feedback** | Yes — `/feedback` endpoint | Endpoint never called | Legitimate dormant capacity |
| **MemoryAssociations** | Yes — `Reflector` (background) | Reflection ran (16 rows) but produced no validated association | Legitimate dormant capacity |
| **OutboundMessages** | Yes — `OutreachService` | No outreach triggered | Legitimate dormant capacity |

**No empty table is populated only by simulations or tests.** **No empty table has its writes
swallowed by a null/no-op registration** — with one adjacent exception worth stating:
`ICognitiveCapture` **is** registered as `NoCognitiveCapture` because
`CognitiveModelOptions.Capture` defaults to `false` and is not set in configuration (Fact).
That suppresses *capture rows in `ShadowComparisons`*, not any table's entire population;
`ShadowComparisons` still has 33 rows from other subjects.

I did not manufacture, and do not recommend manufacturing, a single row.

---

## F. Populated-but-unused findings

**Tables with rows and no proven production consumer: 2.**

| Table | Rows | Evidence | Consequence |
|---|---:|---|---|
| **Revisions** | 10 | `IMemoryStore.GetRevisionsAsync` and `IProcedureStore.GetRevisionsAsync` — production callers: **none**. Only test callers (`CorrectionTests`, `BehaviorExpansionTests`) | Written on every supersession/correction, never read back by the application |
| **ProcedureRevisions** | 0 | Same reader gap; currently also empty | Would be write-only the moment procedures start being taught |

### Anomaly searches — results

1. **Rows but no production reader:** `Revisions` (10).
2. **Rows but no cleanup/retention:** `Reflections` (16), `Curiosities` (10), `AttentionItems` (4), `CompanionPreferences` (5), `SharedExperiencePerspectives` (1), `KnowledgeGaps` (1, has expiry not deletion), `ShadowComparisons` (33), `FrameSessions` (1), `Revisions` (10), `Messages` (88), `Conversations` (13), `Users`, `Projects`, `ProjectEvents`, `Evidence`, `SemanticMemories`, `EpisodicMemories`. **Only 5 tables have any retention at all** (`ModelCalls`, `ToolCalls`, `TurnRecords` at 30 days; `Experiences` at 30 days; `EmotionalSignals` at 180 days).
3. **User-derived data with no `/forget` path:** `Experiences`, `Reflections`, `Curiosities`, `AttentionItems`, `CompanionPreferences`, `SharedExperiencePerspectives`, `KnowledgeGaps`, `TurnRecords` — **8 tables**. See §G.
4. **No resolvable user-isolation key:** `ModelCalls` (safe — no user content), `ShadowComparisons` (**not safe** — holds user text), `Capabilities` (safe — global capability roster).
5. **Written every turn, rarely/never read:** `Revisions` (never read); `TurnRecords` and `ShadowComparisons` (read only by diagnostics endpoints, never by cognition); `Experiences` (read only by background reflection).
6. **Read in production with no production writer:** none.
7. **Empty with an active production writer:** `Procedures`, `ProcedureSteps`, `ProcedureRevisions`, `Concepts`, `ConceptAliases`, `ConceptAssertions`, `CompanionMoodTransitions`, `Anticipations`, `OpenLoops`, `Decisions`, `ProjectAliases`, `MemoryAssociations`, `Feedback`, `OutboundMessages` — 14, all explained in §E.
8. **Empty and not wired:** `ActivityBranches`, `FrameBoundaries` — 2.
9. **Empty and correctly awaiting traffic:** `CompanionMoodTransitions`, `UserPreferences`, plus the 7 dormant-by-design tables.
10. **Parallel authority for the same state:** none confirmed — see §H.
11. **Caches never invalidated:** none. `Reflections.Embedding` and `CompanionPreferences.Embedding` are derived vectors with no invalidation on source change — *unresolved*, low severity.
12. **Unbounded history/audit growth:** `ShadowComparisons`, `FrameSessions` (+ its `TransitionLogJson`, which is append-only *within* a row), `Revisions`, `Messages`, `Experiences` beyond the 30-day window's write rate, `Reflections`, `Curiosities`.
13. **Nullable FKs that can orphan:** all three *declared* FKs are `NOT NULL`/`CASCADE`. The risk is the inverse — **every other cross-table reference is an unenforced Guid** (`Curiosities.ReflectionId`, `KnowledgeGaps.CuriosityId`, `SharedExperiencePerspectives.ExperienceId`, `ProjectEvents.SourceMessageId`, `EmotionalSignals.MessageId`, `Evidence.MemoryId`, `Revisions.MemoryId`, `AttentionItems.SourceId`). Deleting a parent leaves silent dangling references; `foreign_key_check` cannot see them.
14. **Null/no-op store registrations:** `ICognitiveCapture → NoCognitiveCapture` (active, `Capture=false`); `IShadowRecorder → NullShadowRecorder` and `IRendererShadow → NullRendererShadow` (registered only when their flags are off — currently both real, since `RendererShadow:Enabled=true`).
15. **Production tables referenced only from tests/tools:** none. Every table has at least one production writer.
16. **Retention depending on an observability flag:** **none remaining.** This was R-02 and is fixed — frame lifecycle no longer sits behind `RendererShadow:Enabled`. `ShadowComparisons` population still depends on `ShadowMode || Capture || RendererShadow:Enabled`, which is correct: that table *is* observability.
17. **Raw prose where typed identity would suffice:** `ShadowComparisons.Input`/`.Legacy`/`.Model` (forgotten by substring because no message id is stored); `AttentionItems.Summary`; `Experiences.Text`; `SharedExperiencePerspectives.Evidence`.
18. **Schema fields no production writer populates:** `Revisions.Before` — **0 of 10 rows** (Fact).
19. **Readers depending on never-populated fields:** none found — because `Revisions` has no production reader at all.
20. **Migration remnants not in the model:** none.

---

## G. Privacy, retention, and `/forget`

`MemoryCurator.ForgetAsync` is the sole `/forget` orchestrator. Its complete fan-out (Fact,
`MemoryCurator.cs:117-230`):

| # | Target | Mechanism | Identity discipline |
|---|---|---|---|
| 1 | `SemanticMemories` / `EpisodicMemories` | soft delete + embedding purge | direct id |
| 2 | `ShadowComparisons` | `ForgetCapturesAsync` | **substring `Contains`, case-insensitive** ⚠ |
| 3 | `UserPreferences` | `InvalidateByForgottenEvidenceAsync` | exact id / exact statement equality ✔ |
| 4 | `EmotionalSignals` | `ForgetByEvidenceAsync` | exact `MessageId` ✔ |
| 5 | `CompanionMoodTransitions` | `CompactForgottenAsync` | exact evidence event id ✔ |
| 6 | `FrameSessions` + `FrameBoundaries` | `ForgetByEvidenceAsync` | exact `MessageId` ✔ (R-01) |

**Tables holding user-derived content that `/forget` does not reach — 8:**

| Table | Rows | Content held | Retention | Risk |
|---|---:|---|---|---|
| `Experiences` | **84** | `Text` — user-derived episode summaries | 30-day prune | Largest body of user-derived text outside `Messages`; forgetting a memory leaves its experience |
| `Reflections` | 16 | `Musing` — Ava's prose about the user | none | Unbounded; may restate a forgotten fact |
| `Curiosities` | 10 | `Question`, `Reason`, `About` | none | Voiced back to the user on later turns |
| `AttentionItems` | 4 | `Subject`, `Summary` | expiry only | Read into the prompt every turn |
| `CompanionPreferences` | 5 | `Subject`, `Reason`, `Observations` | none | Read into the prompt every turn |
| `SharedExperiencePerspectives` | 1 | `Summary`, `Evidence` | none | Read into the prompt every turn |
| `KnowledgeGaps` | 1 | `Subject`, `ResolutionNote` | staleness expiry | Drives what Ava asks about |
| `TurnRecords` | 16 | previews, retrieval query, serialized plan | 30-day prune | Privacy skip works (5/16 null), but survivors are unreachable |

**The substring problem.** `ShadowComparisons` is the one forget path that matches text
rather than identity (`ShadowRecorder.cs:170-202`). It cannot do otherwise as written,
because the table stores no message id. This cuts both ways: an unrelated row containing the
same phrase is deleted (over-deletion), and a paraphrased capture is not (under-deletion).
It also has **no `UserId`**, so the match is global across users. This is the same class of
defect R-01 fixed for frames, in a table that holds verbatim user messages and both replies
for `renderer.*` subjects.

**Live confirmation of the frame remediation** (Fact): `FrameSessions` = 1 row, 3 transition
entries, **0 carrying raw evidence wording**; `FrameBoundaries.EvidenceStatement` column
absent; `integrity_check` = ok.

---

## H. Consolidation and removal candidates

Every pair named in the brief was examined. **None is accidental duplication.** Authority is
stated for each.

| Pair | Authority | Verdict |
|---|---|---|
| `Messages` vs `TurnRecords` vs `ShadowComparisons` | `Messages` authoritative; `TurnRecords` diagnostic; `ShadowComparisons` observability | **Must remain separate.** Different retention, different privacy handling, different consumers. Merging would put diagnostics on the transcript's lifecycle |
| `SemanticMemories`/`EpisodicMemories` vs `Reflections` vs `Experiences` | Memories authoritative; `Experiences` is the raw event log reflection consumes; `Reflections` is Ava's derived commentary | **Must remain separate.** Distinct provenance and trust levels — the packet renders them under different headings precisely so trust is not flattened |
| `Procedures` vs `ActivityBranches` | `Procedures` authoritative (taught behaviour); `ActivityBranches` is unwired shadow evidence | Separate; `ActivityBranches` needs a wiring decision, not consolidation |
| `Users.Persona` blob vs `UserPreferences` | `UserPreferences` authoritative for typed standing preferences; persona is free-text identity | **Must remain separate.** §5.4 precedence exists to keep `user-preference.` above `persona.`; collapsing them would erase that ordering |
| `EmotionalSignals` vs `CompanionMoodTransitions` | Signals are observations about the user; transitions are Ava's own state changes | **Must remain separate.** Documented Phase-0 boundary; merging would recreate the algebraic-residual leak compaction was built to close |
| `FrameSessions` vs `FrameBoundaries` vs `TransitionLogJson` | Session authoritative for frame truth; log append-only history within it; boundaries scene-scoped user instructions | Separate by design; boundaries need wiring |
| Plan/2 vs translated Plan/3 vs native Plan/4 | Plan/2 authoritative for production; V3/V4 shadow evidence | **Must remain separate** — the contract is explicit that native is built from the same upstream state, never *from* plan/2 |
| `Projects`/`OpenLoops`/`Decisions`/`Anticipations` | Distinct lifecycles: project state, unfinished work, recorded decisions, future events | Separate |
| `ToolCalls` vs tool authorization | `ToolCalls` diagnostic only; authorization is typed on `ToolExecutionOutcome`, not persisted | Separate; authorization is deliberately not a table |

### Removal candidates

**There are none I would recommend removing.** The two genuinely unwired tables
(`ActivityBranches`, `FrameBoundaries`) are both recorded capability, and you have already
ruled that neither `Companion.Soak` nor `Companion.PlanV3.Prototype` may be retired before
their invariants live elsewhere — the same logic applies here with more force, because
removing a table is a destructive migration.

The only candidate worth *considering* later:

| Candidate | Evidence | Risk | Prerequisite | Migration implication | Tests | Why removal might be safer |
|---|---|---|---|---|---|---|
| `Revisions.Before` column | 0 of 10 rows populated; no production reader | **Low** to remove, **medium** to leave | Decide whether the audit trail should record before-state at all | SQLite table rebuild | `EfModelSnapshotTests` must be regenerated deliberately | Leaving a column that looks like it records prior state, and never does, invites a reader built on a lie. **Filling it is the better fix** |

---

## I. Refactor impact

**Constructor dependencies tied to unused or dormant persistence.** Of `Companion`'s 40
parameters, these exist wholly or partly to serve tables with no production reader or no
wiring: `IActivityInstanceProvider` (no implementation at all), `IFrameSessionStore`
(boundaries half of it unwired), `IConceptKnowledge` (three empty tables), `IGapStore`,
`IDiagnosticsStore`, `IShadowRecorder`, `ICognitiveCapture` (currently a no-op object).
Extracting the turn pipeline will move all of them; none blocks the move.

**Turn stages that exist only to write data nothing consumes:**

- `Revisions` writes inside `MemoryPipeline`/`MemoryCurator` — write-only in production.
- `TurnRecords` (`Companion.cs:1296`) — read only by a diagnostics endpoint.
- `Experiences` writes on the turn — read only by background reflection.

None should be removed. All three belong in `Turns/Observability` or `Turns/PostTurn` under
the audit's §E map, where "written on the turn, consumed elsewhere" is the expected shape.

**Stores that can be moved cleanly** (single writer, single reader, no cross-table
invariant): `Capabilities`, `ModelCalls`, `ToolCalls`, `Feedback`, `OutboundMessages`,
`ProjectAliases`, `MemoryAssociations`, `Anticipations`.

**Tables requiring behaviour fixes before refactoring:**

1. `FrameSessions` — no cleanup path; `PruneAsync` exists with no caller.
2. `ShadowComparisons` — substring forgetting, no `UserId`.
3. The eight tables with no `/forget` path (§G).

**Schema work that must not be mixed with code extraction.** The audit's Phase 13 (splitting
`CompanionDbContext`'s 41 mappings) must not be combined with: adding `UserId` to
`ShadowComparisons`, adding retention columns, populating `Revisions.Before`, or declaring
the eight currently-unenforced foreign keys. Each is a migration; extraction is not. The
`EfModelSnapshotTests` golden is the guard that keeps them apart — it fails if a mapping move
changes the model at all.

---

## J. Prioritized decisions

### 1. Fix before refactoring

| # | Item | Why now |
|---|---|---|
| J1 | **`/forget` fan-out to the 8 user-derived tables** | A retention promise that reaches 6 of 14 tables is not the promise as stated. Fixing it after extraction means changing behaviour inside moved code |
| J2 | **`ShadowComparisons`: substring → identity, and add `UserId`** | Same defect class as R-01, in a table holding verbatim user messages, matched globally across users |
| J3 | **`FrameSessions` cleanup** | `PruneAsync` is implemented and uncalled; the transition log grows without bound |

### 2. Refactor while retaining the table

`Revisions`, `TurnRecords`, `Experiences`, `ModelCalls`, `ToolCalls`, `Capabilities`, and all
7 dormant-by-design tables. All move cleanly; none needs a behaviour change first.

### 3. Defer until runtime evidence or the Plan/4 canary

| Item | What would resolve it |
|---|---|
| `Procedures` / `ProcedureSteps` / `ProcedureRevisions` empty | Whether a teaching-shaped turn has occurred — needs content-level evidence this audit may not read |
| `Concepts` / `ConceptAliases` / `ConceptAssertions` empty | Same |
| `CompanionMoodTransitions`, `UserPreferences` empty | Natural traffic; both one day old |
| `ActivityBranches` wiring | The activity instance producer, already a recorded runtime-before-canary blocker |
| `FrameBoundaries` wiring | Plan/4 canary — boundaries are the in-scene obedience mechanism |
| `EmotionalSignals` "reached vs removed" | A contract decision about whether redaction-in-place satisfies `/forget` |

### 4. Safe removal candidates — separate approval still required

**None recommended.** The nearest is `Revisions.Before`, and my recommendation there is to
**populate it rather than drop it**. No table in this database is safe to remove on current
evidence, and I am not proposing one.

---

## Unresolved

Stated plainly rather than guessed:

1. Why `Procedures` and the three concept tables are empty — distinguishing "no qualifying
   turn" from "detector never fires" requires reading message content, which this audit is
   not permitted to do.
2. Whether `Reflections.Embedding` and `CompanionPreferences.Embedding` are invalidated when
   their source changes.
3. Whether `EmotionalSignals` redaction-in-place satisfies the `/forget` contract, or whether
   deletion is required.
4. Whether the 8 unenforced cross-table Guid references have produced dangling rows —
   detecting that means joining on ids, which is safe, but interpreting the result means
   knowing which parents were deliberately deleted, which is not determinable from schema.
