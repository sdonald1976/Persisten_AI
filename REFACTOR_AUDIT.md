# REFACTOR_AUDIT.md

Read-only architectural audit. No code, configuration, database, prompt, test, or running
process was altered in producing this document.

**Inspected commit:** `7ad135b40eef13ed60435e2193c617b0f3ff7abb`
(`7ad135b The card answers: no, at every rank and every sequence length`)
**Branch inspected:** `master`
**Working tree at inspection:** clean — `git status --porcelain` returned no entries.
**Tags at HEAD:** none. (`phase-a-baseline` is at `292eb31`, three commits behind.)
**Date of audit:** 2026-08-26.

Throughout, **Fact** marks something read directly out of the tree at that SHA, with the
citation to check it. **Recommendation** marks my judgement, which you can reject without
contradicting any measurement.

---

## A. Executive determination

**Condition.** This is a healthy layered codebase with one severely overloaded centre.
`Companion.Core` has *zero* references to `Companion.Infrastructure`, `Companion.Api`,
Entity Framework, `System.Net.Http`, or ASP.NET (Fact — §B.5). The domain layer is genuinely
clean, which is unusual and is the single most important fact in this audit: it means every
extraction below is a *move*, not a rewrite.

**Refactor or rewrite: refactor, decisively.** Nothing here justifies a rewrite. The
dependency direction is already correct; what is wrong is that one class — `Companion` —
holds 40 constructor parameters and one method of 1,068 lines. That is a distribution
problem, not a design failure. A rewrite would discard a working privacy model, a working
evidence-identity model, and 1,135 tests, to re-derive the same architecture.

**Largest risks** (detail in §D):

1. **Frame boundary evidence is written on privacy-sensitive turns and `/forget` cannot
   reach it.** Live, not theoretical. This is a retention-guarantee gap that exists today.
2. **Frame lifecycle persistence is gated on `RendererShadow:Enabled`.** Observability
   currently *controls durable conversation state*, which inverts the intended boundary.
3. **There is no byte-level golden fixture for CompactV2/V3/V4 serialization.** The refactor
   has no mechanical proof that serialized plan bytes are unchanged.
4. `CompleteTurnAsync` is 1,068 lines with roughly twenty responsibilities interleaved.
5. Ten dependencies are optional-and-defaulted, so a lost registration degrades silently
   instead of failing loudly.

**Safest starting point.** Not an extraction. Start with characterization: golden
serialization fixtures and a recorded turn-decision transcript. Until serialized bytes and
`DecisionRecord` sequences are pinned, no extraction from `CompleteTurnAsync` is provable.
Phase 1 in §G is entirely test-writing and adds no production code.

**What must not change.** Privacy classification and its skip semantics; secret detection;
audience enforcement; tool authorization; epistemic honesty; evidence identity (exact-id
matching, never substring); retention and compaction semantics; `/forget` behaviour; Plan/2
production authority; Plan/4 shadow-only status; renderer fallback; production routing
(`activeRenderer: production`, canary disabled); Run-1c artifact bytes.

Two findings below (R-01, R-02) describe places where current behaviour *already* departs
from those guarantees. Correcting them is a behaviour change and must be authorized
separately, not folded into a refactor phase. I have not changed them.

---

## B. Measured inventory

### B.1 Projects and dependency direction (Fact)

| Project | Refs | Notes |
|---|---|---|
| `src/Companion.Core` | *(none)* | Only `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.Options` |
| `src/Companion.Infrastructure` | Core | EF Core Sqlite, Polly, OnnxRuntime, ML.Tokenizers |
| `src/Companion.Api` | Core, Infrastructure | |
| `tests/Companion.Tests` | Core, Infrastructure, Api, **Eval** | |
| `tools/Companion.Eval` | Core, Infrastructure | |
| `tools/Companion.RendererBench` | Core | |
| `tools/Companion.DatasetGen` | Core | |
| `tools/Companion.PlanV3.Prototype` | Core | |
| `tools/Companion.Soak` | *(none)* | |

```
        Companion.Api
           |      \
           v       v
  Companion.Infrastructure ---> Companion.Core
           ^                        ^
           |                        |
      tools/Eval              tools/RendererBench, DatasetGen, Prototype
           ^
           |
     tests/Companion.Tests  (also -> Api, Core, Eval)

  ...but ALSO, by <Compile Include> source-linking:

  tools/Companion.RendererBench/{PlanSerialization,RendererChecks}.cs
        |--linked into--> Companion.Infrastructure   <-- production compiles tool source
        |--linked into--> tests/Companion.Tests
        |--linked into--> tools/Companion.DatasetGen
        `--linked into--> tools/Companion.PlanV3.Prototype (PlanSerialization only)
```

**Fact.** Dependency direction through `ProjectReference` is correct everywhere; there is no
cycle and no Core leak. **Fact.** Two files physically located in `tools/` are compiled into
the production assembly `Companion.Infrastructure`
(`src/Companion.Infrastructure/Companion.Infrastructure.csproj:26-27`). This is the only
inversion in the repository, and it is at file level rather than project level.

### B.2 Handwritten line counts (Fact)

Excludes `obj/`, `bin/`, and `Migrations/`.

| Area | Files | Lines |
|---|---:|---:|
| `src/Companion.Core` | 243 | 24,651 |
| `src/Companion.Infrastructure` | 81 | 11,649 |
| `src/Companion.Api` | 13 | 1,970 |
| **src subtotal** | **337** | **38,270** |
| `tests/Companion.Tests` | 146 | 26,774 |
| `tools/*` (5 projects) | 32 | 7,197 |
| **Total handwritten** | **515** | **72,241** |
| *Generated EF migrations (excluded)* | *69* | *45,970* |

Generated migrations are 46k lines — larger than `src` — and are correctly excluded from the
totals above. Their lifecycle is evaluated in §F.

### B.3 Largest handwritten files (Fact)

| Lines | File |
|---:|---|
| 1,764 | `src/Companion.Core/Services/Companion.cs` |
| 1,293 | `tools/Companion.Eval/SyntheticLife.cs` |
| 906 | `src/Companion.Core/Services/Reflector.cs` |
| 845 | `src/Companion.Core/Services/MemoryPipeline.cs` |
| 710 | `src/Companion.Api/Program.cs` |
| 695 | `src/Companion.Infrastructure/DependencyInjection.cs` |
| 575 | `src/Companion.Core/Services/Prompts.cs` |
| 568 | `src/Companion.Infrastructure/Persistence/CompanionDbContext.cs` |
| 565 | `src/Companion.Core/Services/Agent.cs` |
| 549 | `src/Companion.Core/PlanV3/PlanV3Codec.cs` |
| 548 | `src/Companion.Core/Services/WorkingContext.cs` |
| 532 | `src/Companion.Infrastructure/Renderer/RendererShadowService.cs` |
| 532 | `src/Companion.Core/Services/ContextPacketRenderer.cs` |

### B.4 Constructor dependency counts (Fact)

| Params | Type | File |
|---:|---|---|
| **40** | `Companion` | `Services/Companion.cs:69-108` |
| 17 | `Reflector` | `Services/Reflector.cs` |
| 16 | `Agent` | `Services/Agent.cs` |
| 12 | `MemoryPipeline` | `Services/MemoryPipeline.cs` |
| 11 | `OutreachService` | `Services/OutreachService.cs` |
| 10 | `SleepCycle` | `Services/SleepCycle.cs` |
| 9 | `MemoryCurator`, `Greeter` | |

Of `Companion`'s 40, **10 are optional with `= null` defaults** (`Companion.cs:99-108`):
`gate`, `safety`, `shadow`, `capture`, `diagnostics`, `concepts`, `gaps`, `rendererShadow`,
`userPreferences`, `activities`, `frames`.

### B.5 Layer purity checks (Fact)

- `grep "using Companion.Infrastructure|using Companion.Api"` over `src/Companion.Core` →
  **0 matches**.
- `grep "Microsoft.EntityFrameworkCore|System.Net.Http|Microsoft.AspNetCore"` over
  `src/Companion.Core/**/*.cs` → **0 matches** in handwritten source (2 matches in
  `obj/**/GlobalUsings.g.cs`, generated, irrelevant).

### B.6 Interfaces, stores, registrations (Fact)

- `src/Companion.Core/Abstractions/`: **75 files, 77 `public interface` declarations.**
- `src/Companion.Infrastructure/Persistence/`: **27 store implementation files.**
- `CompanionDbContext`: **41 `DbSet<>` properties**, 568 lines, one `OnModelCreating`
  (`CompanionDbContext.cs:78`), one `ConfigureConventions` (`:70`).
- `DependencyInjection.cs`: **110** `AddSingleton`/`AddScoped`/`AddTransient`/`AddHostedService`
  calls in 695 lines.
- `Program.cs`: **19** inline `app.MapGet`/`MapPost`/`MapPut` endpoint definitions in 710 lines.
- Top-level classes in `src`: 190.

### B.7 Persistence hardening coverage (Fact)

- `BeginTransactionAsync` appears in **4 of 27** persistence files: `ActivityBranchStore`,
  `CompanionMoodLog`, `FrameSessionStore`, `UserPreferenceStore`.
- `IsConcurrencyToken()` appears **once**, `CompanionDbContext.cs:163`.

**Recommendation.** This is not automatically wrong — most stores are append-only or
single-row upserts where a transaction adds nothing. It is listed so that any future store
extraction preserves the distinction deliberately rather than by accident. I did not audit
each of the remaining 23 for whether it *needs* one; that is a separate correctness review,
not an architecture question.

---

## C. Current turn sequence

Traced from `Companion.RespondAsync` (`Services/Companion.cs:154`) which, for a normal turn,
delegates to `CompleteTurnAsync` (`:286-1354` — **1,068 lines**).

Owner is `Companion` unless stated. Line numbers are from the inspected SHA.

| # | Stage | Line | Inputs → Outputs | Side effects | Ordering constraint | Failure behaviour | Proposed owner |
|---|---|---|---|---|---|---|---|
| 1 | Admission / empty-message guard | 154-160 | `userId, conversationId, userMessage` → validated | none | first | throws `ArgumentException` | `Turns/Admission` |
| 2 | Conversation resolution | 294 | ids → `Conversation` | none | before privacy | propagates | `Turns/Admission` |
| 3 | **Privacy classification** | 295 | `promptText` → `sensitive: bool` | none | **before everything derived** | propagates | `Infrastructure/Privacy` (keep interface in Core) |
| 4 | Remember gate | 296-298 | `sensitive`, `DoNotRemember`, options → `remember` | logs | after 3 | n/a | `Turns/Admission` |
| 5 | Profile load | 304 | `userId` → `Profile` | get-or-create **write** | — | propagates | `Turns/Context` |
| 6 | Recent transcript | 350 | ids → `IReadOnlyList<Message>` | none | before retrieval | propagates | `Turns/Context` |
| 7 | Retrieval | 401 | query, project → `RetrievalOutcome` | none | after 6 | propagates | `Turns/Context` |
| 8 | Associative recall | 402 | outcome → expanded | none | after 7 | propagates | `Turns/Context` |
| 9 | Concept lookup | 434 | term → `knowledge` | none | conditional | propagates | `Turns/Context` |
| 10 | Raw-query shadow retrieval | 476-478 | — | **second retrieval, shadow only** | only if `_shadow.IsRecording` | swallowed | `Turns/Observability` |
| 11 | Relationship / curiosity / inner state / familiarity | 496-515 | `userId` → state | none | before packet | propagates | `Turns/Context` |
| 12 | Attention + capability note | 522-524 | → notes | none | before packet | propagates | `Turns/Context` |
| 13 | Plan/2 (production packet) build | ~530-615 | all above → `ContextPacket`, `plan` | none | after 11-12 | propagates | `Turns/Planning` |
| 14 | **Native Plan/3+4 build** | **625-651** | same upstream state | none | **gated on `_rendererShadow.IsObserving \|\| IsCanaryFor`** | caught, recorded, turn continues | `Turns/Planning` (ungated) |
| 15 | Tool planning + execution | 654-664 | context → `toolOutcome` | tool side effects | after 13 | propagates | `Turns/Execution` |
| 16 | Contributor assembly (Sources 2/3/4a-c) | 676-727 | typed outcomes, prefs, mood, familiarity | reads preference store | inside 14's gate | caught at 780 | `Turns/Planning` |
| 17 | **Frame transition + persistence** | **730-763** | `promptText` → `nativeFrame` | **durable write `_frames.ApplyAsync`** | inside 14's gate; **before generation** | caught at 780 | `Domain/Frames` + `Turns/Understanding` |
| 18 | Assembler grant | 765-778 | contributors → `AssemblyReport` | none | after 16-17 | caught | `Turns/Planning` |
| 19 | CompactV4 length probe | 800-812 | plan → char count | none | after 18 | recorded | `Rendering/PlanV4` |
| 20 | Canary eligibility | 829-831 | user, tools, `inCharacter` → `canaryTurn` | none | before generation | n/a | `Rendering/Shadow` |
| 21 | Prompt render | 839 | packet → `renderedPrompt` | none | before generation | propagates | `Turns/Planning` |
| 22 | **Generation** | 841-843 | prompt → `generated.Text` | streams to sink unless canary | — | propagates | `Turns/Execution` |
| 23 | **Echoed-turn strip** | 849 | text, recent → `response` | **mutates reply**, logs | after 22 | n/a | `Turns/Execution` |
| 24 | **Canary render + swap** | **864-899** | plan, transcript → `canaryResult` | **replaces `response`**; writes comparison row; `tokenSink.Report` | after 22 | falls back to production reply | `Rendering/Shadow` |
| 25 | **Reply gate** | 909-940 | response → verdict | **replaces `response` if enforcing**; shadow row | after 24 | shadow-only by default | `Turns/Execution` |
| 26 | Plan fidelity checks | 944-978 | plan, response → violations | decisions + shadow rows; **never mutates reply** | after 25 | n/a | `Rendering/Fidelity` |
| 27 | Renderer shadow observe | 985-1030 | snapshot | fire-and-forget row | after 26 | swallowed | `Turns/Observability` |
| 28 | **Store assistant message** | **1035** | response → `Message` | **durable write — reply is final here** | after 23-25 | propagates | `Turns/PostTurn` |
| 29 | Memory extraction | 1049 | exchange → `extraction` | durable writes | after 28 | isolated | `Turns/PostTurn` |
| 30 | Project/open-loop updates | 1050 | → updates | durable writes | after 29 | isolated | `Turns/PostTurn` |
| 31 | Attention capture, procedures | 1055-1057 | | durable writes | after 29 | isolated | `Turns/PostTurn` |
| 32 | Concept learning | 1067 | | durable writes | conditional | isolated | `Turns/PostTurn` |
| 33 | Gap observe / satisfy | 1094-1140 | | durable writes | after 29 | isolated | `Turns/PostTurn` |
| 34 | Cognitive capture | 1153-1154 | | corpus rows | after 29 | isolated | `Turns/Observability` |
| 35 | Mood / anticipation / commitment | 1597-1707 (helpers) | | durable writes | after 28 | isolated | `Domain/Mood` |
| 36 | Reflection mark-voiced | 1220 | | durable write | conditional | isolated | `Turns/PostTurn` |
| 37 | Diagnostics turn record | 1296 | | durable write | last | isolated | `Turns/Observability` |

### C.1 Code that can change the displayed reply after generation (Fact)

Exactly three places mutate `response` after the model produced it, all before storage:

1. `Companion.cs:849` — `EchoedTurnFilter.Strip(generated.Text, recent)`.
2. `Companion.cs:885` — `response = canaryResult!.Reply` when the canary renders successfully.
3. `Companion.cs:938` — `response = _safety.Replacement` when the gate blocks **and**
   `_safety.Mode == GateMode.Enforce`.

**Fact.** After `StoreMessageAsync` at `:1035`, no later stage assigns to `response`. Stages
26, 27 and 29-37 read it only. **The displayed reply is final at line 1035 and the ordering
is correct** — the gate runs before storage precisely so a refused reply never becomes the
next turn's context (`:906-908`, comment).

**Recommendation.** This is a genuine strength and the extraction plan must preserve it as an
explicit invariant, not an accident of statement order. When stages 22-28 move into
`Turns/Execution`, the reply should become a value that is *returned* through the three
transforms rather than a mutable local — so that "can anything change it after this point?"
is answerable from the type, not from reading 200 lines.

---

## D. Findings register

Severity is architectural/correctness risk. Confidence is mine, stated honestly.

| ID | Sev | Category | Evidence | Current risk | Proposed destination | Conf | Prerequisite | Behaviour-preservation test |
|---|---|---|---|---|---|---|---|---|
| **R-01** | **Critical** | Privacy / retention | `Companion.cs:730-763` has **no `sensitive` check**; `FrameSessionStore.cs:81,206` persists `Clip(request.Evidence)`; `Clip` = first **200 chars** of `promptText` (`:235-236`); `MemoryCurator.ForgetAsync` (`:117-213`) fans out to shadow, preferences, emotions, mood — **not frames**; `IFrameSessionStore.ForgetByEvidenceAsync` has **no production caller** (only `FrameSessionStore.cs:173` internal and `FrameIsolationTests.cs:142,153`) | Up to 200 chars of a user's words from a **privacy-sensitive** turn are persisted durably and `/forget` cannot remove them. Compounded because the classifier marks roleplay enter/exit turns sensitive (docs/RENDERER_SHADOW.md §1.3) — exactly the turns that generate transitions | `Domain/Frames` write path gains the `sensitive` gate; `/forget` fan-out gains frames | **High** — read directly, no inference | **Authorize as a behaviour fix first.** Not a refactor phase | New test: sensitive turn → no `FrameTransitionEntry.Evidence` persisted. New test: `/forget` on evidence message → frame boundary `Status == EvidenceForgotten` |
| **R-02** | **Critical** | Layering / observability | `Companion.cs:625` gates the whole native block on `_rendererShadow.IsObserving \|\| IsCanaryFor(userId)`; frame persistence at `:741` is **inside** it; `RendererShadowService.cs:113` → `IsObserving => _options.Enabled && _recorder.IsRecording` | Setting `RendererShadow:Enabled=false` silently stops frame lifecycle from advancing and from persisting. Observability controls durable conversation state — the exact inversion §7 of the brief asks about. **Must be separated before Plan/4 becomes authoritative** | Frame lifecycle → `Turns/Understanding`, always-on. Native plan *recording* stays gated | **High** | Authorize as behaviour fix; needs R-04 fixtures first | Test: with shadow disabled, a frame-entering message still produces a persisted transition |
| **R-03** | High | Oversized type | `CompleteTurnAsync` = **1,068 lines** (`:286-1354`); `Companion` ctor = **40 params** (`:69-108`) | Every turn concern is edited in one file; merge conflicts and accidental reordering are the standing risk. Ordering constraints (§C) are enforced only by statement position | Split per §E into `Turns/*` | High | R-04 | Decision-sequence characterization test (§G Phase 1) |
| **R-04** | High | Test safety net | Plan tests assert **parsed fields** (`PlanV3ContributionTests.cs:97,122,143`), not bytes. No golden file for `CompactV2`/`CompactV3`/`CompactV4`. Only 2 fixture JSONs exist (`Fixtures/roberta-nli-tokenization.json`, `Fixtures/twenty-questions-regression.json`) | **No mechanical proof that serialized plan bytes survive a refactor.** This is the gating gap for every other phase | `tests/Companion.Tests/Golden/` | High | none — do this first | It *is* the test |
| **R-05** | High | Privacy / retention | `IActivityBranchStore.ForgetAsync` (`IActivityBranchStore.cs:35`) implemented at `ActivityBranchStore.cs:101`; **no production caller** anywhere in `src` | Same class of gap as R-01 for activity-branch excerpts. Lower exposure only because no activity producer exists yet (R-11) | `/forget` fan-out | High | R-11 resolution | Test: `/forget` reaches branch excerpts once a producer exists |
| **R-06** | Medium | Encoding | 11 files contain byte pair `C3 A2`; **56 occurrences in `Companion.cs` alone**. Not comment-only: `Companion.cs:250` `"No problem â€" I've dropped that..."` (**displayed to the user**); `:1361`, `:1376` `"â€¦"` used as truncation ellipsis **inside the prompt sent to the model**; `TurnIntentClassifier.cs:39,111,112` | User-visible mojibake, and corrupted ellipses in model input. **Not cosmetic** — it changes displayed bytes and prompt bytes | Fix in place | High | R-04 (it changes bytes on purpose) | Golden prompt-render fixture must be regenerated *deliberately* with a reviewed diff |
| **R-07** | Medium | Dependency inversion | `Companion.Infrastructure.csproj:26-27` source-links `tools/Companion.RendererBench/{PlanSerialization,RendererChecks}.cs`; same files linked into 3 other projects; produces 8+ `CS0436` warnings in `RendererContractTests.cs` | Production compiles source that lives in a tools folder. Today the bytes are identical so behaviour matches; the day anyone adds a conditional compile or a second copy, tests silently stop testing what ships | Move files to `Companion.Core/PlanV3/` or a small `Companion.Rendering`; reference, don't link | High | none | Full suite green; `CS0436` count → 0 |
| **R-08** | Medium | Duplication | Two no-op `IShadowRecorder`s: `NoShadowRecorder` (`Companion.cs`, private nested) and `NullShadowRecorder` (`Infrastructure/Cognition/ShadowRecorder.cs`) | Two behaviours that must stay identical, with nothing forcing them to | Unify on one, in Core beside the interface | High | none | Existing shadow tests |
| **R-09** | Medium | Composition | 10 optional `= null` ctor params (`Companion.cs:99-108`) defaulting to null objects (`:110-116`) | A dropped DI registration degrades **silently** to a no-op. For `_rendererShadow` and `_gate` that means observability or safety quietly switching off with no error | Make required; register explicit null objects in DI | Medium — the defaults exist to keep test construction cheap, which is a real cost to weigh | R-04 | DI-completeness test asserting every dependency resolves |
| **R-10** | Low | Naming | `src/Companion.Core/PlanV3/` contains `Frame.cs`, `PlanV4Codec.cs`; all files declare `namespace Companion.PlanV3` | plan/4 lives under a plan/3 name. Misleads every future reader | `Companion.Core/Planning/` with `PlanV3`/`PlanV4` subfolders, or namespace `Companion.Planning` | High | R-04 | Compile-only; no behaviour |
| **R-11** | Info | Dormant | `IActivityInstanceProvider` — **zero implementations** in `src`, `tests`, or `tools`. Only the injection site (`Companion.cs:36,109`). `ActivityInstanceContributor` `state` param unread (`CS9113`) | Dormant by design and documented (`Companion.cs:700-702`). Not dead | Keep. Resolve when the producer lands | High | — | n/a |
| **R-12** | Medium | Persistence | `CompanionDbContext` — 41 `DbSet<>`, 568 lines, single `OnModelCreating` (`:78`) | One file is the merge point for every schema change across nine domains | Split to `IEntityTypeConfiguration<>` per aggregate under `Persistence/Configurations/` (folder exists, holds 1 file) | High | R-04 | **Model-snapshot equality test**: built `IModel` identical before/after |
| **R-13** | Medium | Composition root | `Program.cs` 710 lines / 19 inline endpoints; `DependencyInjection.cs` 695 lines / 110 registrations | Endpoint logic and wiring share a file with startup ordering | `Api/Endpoints/*Module.cs`; `Infrastructure/Registration/Add*.cs` | High | R-04 | Route-table snapshot test |
| **R-14** | Low | Hygiene | 3 `xUnit1031` blocking-task warnings (`DisputeTargetingTests.cs:56`, `PersistenceHardeningTests.cs:163,164`) | Real deadlock risk in tests | Fix in place | High | none | Suite green |
| **R-15** | Info | Test inventory | **0** `Skip=` attributes and **0** flaky traits declared across 146 test files | No flaky tests are *declared*. I did not run the suite repeatedly, so I cannot claim none exist | — | **Low** — absence of a marker is not absence of flakiness | Phase 1 should run the suite 3× and record | — |

**Not findings.** Things I checked and found clean, recorded so they are not re-litigated:

- No Core → Infrastructure/Api/EF/HTTP leak (§B.5).
- No static mutable test hooks in `src` (grep for non-readonly public/internal static setters → 0).
- No `TODO`/`FIXME`/`HACK`/`XXX` markers anywhere in `src` (0 matches).
- `TurnRecord` and `ShadowComparison` each have exactly one declaration — no duplicate
  diagnostics envelopes.
- Reply finality ordering is correct (§C.1).

---

## E. Target module map

Evaluating the proposed direction rather than accepting it.

**Recommendation on project count: create at most one new project, and not yet.**
`Companion.Core` is already dependency-clean, so splitting it into `Companion.Domain` +
`Companion.Application` would enforce a rule that is not currently being broken. Folders
enforce it well enough while the turn pipeline is being taken apart, and folder moves are
cheaper to revert than project moves. Re-evaluate after §G Phase 9.

The one case where a project boundary buys something real is `Companion.Rendering`: it would
make "the renderer cannot retrieve memory or alter cognition" a *compile error* rather than a
review comment, by giving rendering no reference to the stores. That is worth a project — but
after the extractions, not before.

| Current type / responsibility | Lines | Destination | Project? |
|---|---:|---|---|
| `Companion.RespondAsync` / clarification | 154-283 | `Core/Turns/Admission/` | folder |
| Privacy classification call site | 295 | `Core/Turns/Admission/` (impl stays in Infrastructure) | folder |
| Frame request read + lifecycle + persist | 730-763 | `Core/Turns/Understanding/` | folder |
| Intent / correction understanding | `TurnIntentClassifier.cs` | `Core/Turns/Understanding/` | folder |
| Transcript, retrieval, recall, state loads | 350-524 | `Core/Turns/Context/` | folder |
| `WorkingContext.cs` (548) | — | `Core/Turns/Context/` | folder |
| Plan/2 packet build | ~530-615 | `Core/Turns/Planning/` | folder |
| `ContextPacketRenderer.cs` (532) | — | `Core/Turns/Planning/` | folder |
| Native Plan/3+4 build + contributors | 617-812 | `Core/Turns/Planning/` | folder |
| `PlanV3/*` (16 files) | — | `Core/Planning/PlanV3`, `Core/Planning/PlanV4` | folder (R-10) |
| `ToolLoop.cs` (407) | — | `Core/Turns/Execution/` | folder |
| Generation + echo strip + gate | 841-940 | `Core/Turns/Execution/` | folder |
| Canary render + fallback | 864-899 | `Rendering/Shadow/` | **project, later** |
| `RendererShadowService.cs` (532) | — | `Rendering/Shadow/` | **project, later** |
| `PlanFidelity` checks | 944-978 | `Rendering/Fidelity/` | **project, later** |
| `RendererChecks.cs`, `PlanSerialization.cs` | tools/ | `Rendering/` (R-07) | **project, later** |
| Store message | 1035 | `Core/Turns/PostTurn/` | folder |
| Extraction, projects, attention, procedures, gaps | 1049-1140 | `Core/Turns/PostTurn/` | folder |
| Mood / anticipation / commitment capture | 1597-1707 | `Core/Domain/Mood/` | folder |
| Cognitive capture, diagnostics record | 1153, 1296 | `Core/Turns/Observability/` | folder |
| `MemoryPipeline.cs` (845), `Reflector.cs` (906) | — | **keep where they are** | — |
| `MemoryCurator.cs` — `/forget` orchestrator | — | **keep where it is**; extend fan-out (R-01, R-05) | — |
| `CompanionDbContext` (41 DbSets) | — | split configs, **one context stays** | folder |
| 27 stores | — | `Infrastructure/Persistence/<Aggregate>/` | folder |
| `Program.cs` endpoints | 19 maps | `Api/Endpoints/*Module.cs` | folder |
| `DependencyInjection.cs` | 110 regs | `Infrastructure/Registration/Add*.cs` | folder |
| `Prompts.cs` (575) | — | **keep where it is** | — |
| `Domain/*` (67 files) | — | **keep**; subfolder by aggregate only if it aids navigation | — |

### E.1 On `TurnState`

**Recommendation: yes, a typed `TurnState` with explicit sections — but built incrementally
and never as a property bag.**

The argument for it is concrete rather than aesthetic. `CompleteTurnAsync` currently carries
roughly 40 locals across 1,068 lines, and stage 24 reads eight of them
(`plan`, `recent`, `promptText`, `response`, `nativeV3`, `nativeBuildError`,
`nativeLintRejections`, `nativeAssembly`, `nativeCompactV4Chars`, `nativeFrame`) to build one
observation. Any extraction has to pass that set somehow; a typed record with sections is
strictly better than a ten-parameter method.

Sections should mirror §C ownership: `Identity`, `Understanding`, `Context`, `Plan`,
`Execution`, `Effects`, `Diagnostics`. Two constraints matter more than the shape:

- **No `IDictionary<string, object>`, no `dynamic`.** If a stage needs a value, it gets a
  typed field; if that field is only meaningful for some turns, it is nullable and the
  nullability is the documentation.
- **Do not introduce a generic `IPipelineStage<T>` abstraction.** The stages have genuinely
  different signatures and three of them mutate the reply. A uniform interface would hide
  exactly the ordering constraints §C exists to make visible. A plain sequence of explicit
  method calls in a thin coordinator is clearer and is what I recommend.

---

## F. Dead / duplicate / dormant inventory

| Item | Classification | Evidence | Action |
|---|---|---|---|
| `IActivityInstanceProvider` | **Dormant by design** | Zero implementations; documented at `Companion.cs:700-702` | Keep |
| `ActivityInstanceContributor` `state` param | **Dormant by design** | `CS9113` unread; ctor at `ActivityInstanceContributor.cs:12-15` | Keep |
| `IActivityBranchStore.ForgetAsync` | **Uncertain — needs runtime evidence** | Implemented, no production caller | See R-05; do **not** delete |
| `IFrameSessionStore.ForgetByEvidenceAsync` | **Definitely reachable, but unwired** | Implemented and tested; no production caller | See R-01; **wire, do not delete** |
| `NoShadowRecorder` vs `NullShadowRecorder` | **Accidental duplication** | Two no-op `IShadowRecorder`s | Unify (R-08) |
| `NullRendererShadow`, `NoCognitiveCapture`, `AlwaysOpenGate`, `NullSink` | **Dormant by design** | Null-object pattern | Keep; move beside interfaces |
| `PlanSerialization.cs`, `RendererChecks.cs` | **Misplaced, not dead** | Linked into 4 projects from `tools/` | Move (R-07) |
| `tools/Companion.PlanV3.Prototype` | **Superseded experiment** | 523-line test file; Core has the real `PlanV3Codec` | **Uncertain** — confirm no CI use before any action |
| `tools/Companion.Soak` | **Uncertain** | No `ProjectReference`, not referenced by tests | Confirm before action |
| 69 EF migration files (45,970 lines) | **Migration compatibility** | Generated | Keep; never hand-edit |
| `Fixtures/twenty-questions-regression.json` | **Active** | Regression fixture | Keep |
| Mojibake, 11 files | **Live defect** | §D R-06 | Fix deliberately |

**Not duplication** (checked and rejected): `PlanV3Codec` vs `PlanV4Codec` are one protocol
family with a real version distinction; the Plan/2 packet path and the native plan path
deriving similar meaning independently is the *documented contract* (`Companion.cs:617-619`,
"built from the same upstream state as the v2 plan — never FROM it") and must stay separate;
the several per-aggregate stores following the same EF pattern are a shared idiom, not copied
logic.

---

## G. Phased refactor roadmap

Amendment to the suggested order, justified by evidence: **R-01 and R-02 are behaviour fixes,
not refactor phases, and they should be authorized and landed before the pipeline is taken
apart** — R-02 in particular changes when frame rows are written, and doing that underneath a
half-extracted pipeline makes both changes harder to prove.

| # | Phase | Scope | Forbidden | Tests | Rollback | Risk | Size |
|---|---|---|---|---|---|---|---|
| 0 | **Freeze** | Tag `refactor-baseline` at `7ad135b`. Run suite 3× recording failures (R-15) | Any source change | 1,135 existing | n/a | none | S |
| 1 | **Characterization** | Golden `CompactV2`/`V3`/`V4` byte fixtures; rendered-prompt golden; `DecisionRecord`-sequence transcript for ~10 archetypal turns; `IModel` snapshot test | Any `src` change | new only | delete tests | **low** | **M** |
| 2 | **R-01 fix** (behaviour) | Gate frame-evidence persistence on `sensitive`; add frames to `/forget` fan-out | Anything else | Phase 1 + 2 new | revert commit | **high** | S |
| 3 | **R-02 fix** (behaviour) | Ungate frame lifecycle from `RendererShadow:Enabled`; keep native-plan *recording* gated | Plan/4 authority; routing | Phase 1 + new | revert commit | **high** | M |
| 4 | Naming | R-10 namespace/folder move; R-07 file relocation + project reference | Any logic change | full suite; `CS0436` → 0 | revert | low | M |
| 5 | Extract admission | Stages 1-4 → `Turns/Admission` | Reordering | Phase 1 transcripts | revert | low | S |
| 6 | Extract observability | Stages 10, 27, 34, 37 → `Turns/Observability` | Changing what is recorded | shadow tests | revert | low | M |
| 7 | Extract understanding | Stage 17 + intent → `Turns/Understanding` | Frame semantics | frame tests | revert | **medium** | M |
| 8 | Extract context | Stages 5-12 → `Turns/Context` | Retrieval order | golden prompt | revert | medium | L |
| 9 | Extract planning | Stages 13-19, 21 → `Turns/Planning` | Plan/2 authority; Plan/4 shadow status | golden bytes | revert | **high** | L |
| 10 | Extract execution + fallback | Stages 20, 22-25 → `Turns/Execution`; make reply a returned value (§C.1) | Mutation order | reply-finality test | revert | **high** | L |
| 11 | Extract post-turn | Stages 28-33, 35-36 → `Turns/PostTurn` | Isolation semantics | write-set test | revert | medium | L |
| 12 | Split endpoints + DI | R-13 | Route/registration changes | route snapshot | revert | low | M |
| 13 | Split EF configs | R-12 | **No schema change, no migration** | `IModel` snapshot | revert | medium | M |
| 14 | R-06 encoding | Fix mojibake incl. displayed string + prompt ellipses | Bundling with anything | regenerate goldens with reviewed diff | revert | **medium** | S |
| 15 | R-08, R-09, R-14 | Unify null recorders; required deps; xUnit1031 | — | full suite | revert | low | S |
| 16 | Dead code | Only items proven dead by Phase 1-15 evidence | Deleting anything "uncertain" in §F | full suite | revert | low | S |

**Proof obligation, every phase 5-13:** the phase is complete only when, for the Phase 1
archetypal turns, (a) `CompactV2/V3/V4` bytes are identical, (b) the rendered prompt string is
identical, (c) the `DecisionRecord` sequence is identical in order and content, (d) the set of
durable writes is identical, and (e) the displayed reply is identical.

---

## H. First ten executable refactor commits

Each is one purpose and leaves the suite green. **None are implemented.**

1. `test: pin CompactV2/V3/V4 serialized bytes for ten archetypal plans` — new
   `tests/Companion.Tests/Golden/`; fixtures generated from the current build and committed.
2. `test: pin the rendered production prompt for the same ten turns` — golden for
   `packet.Render()` (`Companion.cs:839`).
3. `test: pin the DecisionRecord sequence per archetypal turn` — order and content.
4. `test: pin the durable write-set per archetypal turn` — table + row count per turn.
5. `test: snapshot the EF IModel` — guards Phase 13 before it starts.
6. `test: record suite stability across three consecutive runs` — resolves R-15 honestly.
7. `chore: move PlanSerialization and RendererChecks out of tools into Companion.Core` —
   R-07; replaces four `<Compile Include>` links with references; `CS0436` → 0.
8. `refactor: rename the PlanV3 namespace and folder to Planning` — R-10; mechanical, no
   logic change.
9. `refactor: unify NoShadowRecorder and NullShadowRecorder` — R-08.
10. `fix: xUnit1031 blocking-task calls in two test files` — R-14.

Commits 1-6 touch no `src` file. Commits 7-10 touch `src` but change no behaviour, and
commits 1-6 are what proves it.

---

## I. Deferred decisions

**Until after Plan/4 corpus freeze:** any change to `PlanV3Builder`, the contributor set, or
the assembler grant tuples. Phase 9 must move these files without editing their logic.

**Until after mouth training:** whether `Companion.Rendering` becomes a real project. Its
right shape depends on how the trained renderer is actually invoked.

**Until after Plan/4 canary:** removing the Plan/2 packet path; making `TurnState` the sole
carrier; R-11's activity producer and character-roster cognition.

**Until more runtime evidence:** R-05 exposure; whether the 23 transaction-less stores need
transactions; whether `tools/Companion.Soak` and `tools/Companion.PlanV3.Prototype` are live.

**Until hardware changes:** nothing. This refactor is entirely GPU-independent — which is
what makes it the right work for this window.

---

## J. Final recommendation

**Begin now, before corpus generation — but begin with Phases 0-1 only, and treat R-01 and
R-02 as separate behaviour authorizations rather than refactor work.**

Reasoning:

- Phases 0-1 add tests and change no production code. They are safe in parallel with anything,
  and they are the prerequisite for every later phase. There is no argument for delaying them.
- **R-01 is a live privacy gap.** It is not a refactor question, and it should not wait for a
  corpus freeze. It needs your authorization because fixing it changes what gets persisted.
- **R-02 must be fixed before Plan/4 becomes authoritative**, and it is much cheaper to fix
  now, while the frame block is still one contiguous region, than after Phase 7 has moved it.
- Phases 4-8 are safe in parallel with dataset work: they touch admission, observability,
  understanding, context and naming, none of which alter plan bytes — which Phase 1's goldens
  prove mechanically rather than by argument.
- **Phases 9 and 10 must not run in parallel with corpus generation.** They touch plan
  construction and reply selection. Even with byte-identical goldens, generating a corpus from
  a pipeline being restructured means any later anomaly has two candidate explanations.
- Phases 11-16 can follow at any time.

**Safe in parallel with data work:** 0, 1, 4, 5, 6, 7, 8, 12, 13, 15, 16.
**Must be serialized against data work:** 9, 10.
**Requires explicit behaviour authorization, independent of all of the above:** 2, 3, 14.
