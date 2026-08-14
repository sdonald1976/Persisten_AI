# Handoff / continuation brief

Paste this into a fresh session to pick the project back up. Keep it current as the project evolves
— a stale brief is worse than none, because it is believed.

---

I'm continuing work on my Persistent AI Companion project. Please read this brief, then confirm
you're oriented before making changes.

## Two repos, not one

| | |
|---|---|
| `C:\Source\Persisten_AI` | Her mind. Branch **`claude/inner-monologue`**. |
| `C:\Source\AvaWorld` | Her world. Branch **`main`**, `github.com/sdonald1976/AvaWorld`. |

They are deliberately separate applications that talk over a WebSocket. **The world is not part of
the companion solution.** An earlier design put a "world model" inside the companion and I rejected
it — if you find yourself adding a table of rooms to `companion.db`, stop, you have rebuilt the
thing we split apart. See [`WORLD.md`](WORLD.md).

The rule that keeps them honest: **she may hold a connection, but never a model.** The world
re-advertises its layout on every connect; she stores none of it.

## What it is

A local-first, persistent AI companion with durable memory. A pure "brain" (Core) with swappable
"faces" (headless HTTP + SSE + WebSocket, plus a reference web client) and swappable model providers
(offline Mock, or OpenAI-compatible Ollama/LM Studio). The point is continuity: she remembers me
across sessions, starts conversations, and now lives somewhere.

The guiding line from the vision doc is **"build continuity, not consciousness."** It rules things
out: no emotion simulator, no speculative inner-life subsystems. Her state is derived from what
actually happened, and every derived thing must be able to say where it came from.

## Stack & layout

- .NET 9, C#, EF Core + SQLite, xUnit. Solution: `Persisten_AI.sln`
  - `src/Companion.Core` — domain + interfaces + all logic. Pure, no I/O deps.
  - `src/Companion.Infrastructure` — EF, model provider adapters, world link, DI composition root.
  - `src/Companion.Api` — headless HTTP + SSE + WebSocket face (+ `wwwroot` reference client).
  - `tests/Companion.Tests` — full suite, **699 passing** at last handoff.
- `global.json` pins .NET 9 (`9.0.313`, `latestFeature`).
- EF migrations: `dotnet ef migrations add <Name> --project src/Companion.Infrastructure
  --startup-project src/Companion.Infrastructure`.

AvaWorld is Godot 4.7.1 — the **.NET/Mono build specifically**; the standard build cannot run C# and
fails in a way that does not obviously say so.

- `src/AvaWorld.Simulation` — plain C#, the actual world. **Must never reference Godot**, so it can
  be tested and reasoned about without an engine.
- `src/AvaWorld.Wire` — the protocol and socket server.
- `src/AvaWorld.Server` — the Godot host. Runs headless, always on.
- `src/AvaWorld.Poke` — a throwaway console client for prodding the wire by hand.

## Running it

```powershell
cd C:\Source\AvaWorld; .\start-all.ps1
```

That brings up the world, the companion API, and a client. `-NoClient` omits the window.
`.\stop-all.ps1` takes it down and **should be used rather than closing windows** — `dotnet run`
leaves an orphaned `companion-api.exe` holding port 5266, and the next start then fails with
"address already in use", which reads convincingly as a crash. This has caught me three times.

Nothing auto-starts after a reboot yet.

## Machine-local config

`src/Companion.Api/appsettings.local.json` is **gitignored** and holds the world URL, the world
token, and a model roster suited to whatever GPU is in front of you. The committed
`appsettings.json` describes the intended roster (Stheno for voice, `qwen2.5:7b-instruct` for
extraction and safety, `qwen2.5:3b-instruct` for the small utility roles, `nomic-embed-text` for
embeddings); the local file overrides it.

On the 6 GB GTX 1660 that roster does not fit — turns exceeded the 300 s timeout. Collapsing the
utility roles onto `qwen2.5:3b-instruct` while keeping Stheno for her voice took a turn from
timeout to about 48 s. A different machine wants a different local file, and that is the point of
it being local.

## Git conventions

- Commit with clear messages; end bodies with a `Co-Authored-By` line. Don't open PRs unless I ask.
- `dotnet test` green before committing.
- Push both repos when you touch both — I work from more than one machine, and an uncommitted fix
  is a lost fix.

## Architecture already built

**Turn pipeline** (`Companion.cs`): store message → resolve project/ambiguity → retrieve → assemble
context packet → generate → store with generation metadata → extract memories → update open loops.

**Retrieval**: hybrid scoring with a `RelevanceFloor`, so unrelated memories don't bleed in.

**Identity and personality** are separate axes — who she is (name, pronouns; default Ava/she) versus
how she behaves (named presets, free-text tweaks on top).

**Provenance**: `MemoryOwner` / `MemoryOrigin` on every memory. Reflection is evidence-gated through
`ResolveEvidence` — she cannot conclude something she cannot point at. `ReflectionSkipReason` records
*why* a reflection didn't happen rather than silently producing nothing.

**Model preflight** (`ModelPreflight` + `ModelPreflightWorker`) probes the provider catalog at
startup and reports roles whose model is missing, instead of failing on first use an hour later.

**Prompt hygiene**: `ReasoningFilter` strips think-tags and `<|…|>` spans; `PromptEchoFilter` strips
trailing packet structure and cuts fabricated turns at invented role markers; stop sequences on the
chat endpoint block role markers at source. All three exist because the model wrote *my* side of the
conversation as well as hers.

**Her world** (`WorldWorker` + `WebSocketWorldLink` + `RoamingPolicy`): she holds the connection,
perceives, tends things that need it, and decides where to be. Movement is pure, deterministic, and
model-free — partly cost (a model call per move would compete with the conversation for one GPU),
mainly because every move carries the reason that produced it, so "why are you in the greenhouse?"
has a true answer rather than a plausible one. World events go to `IExperienceStore`, never to
memory: an afternoon in the greenhouse is not a fact about me.

## Two traps in the roaming policy

Both cost real debugging time and neither is visible from the tests alone.

**Hysteresis that cancels out.** An early version had both a move threshold and a bonus for the
current room. They were the same idea counted twice and summed to exactly zero, so she never moved.
There is now one margin, and it relaxes as she settles.

**Banding a continuous quantity.** Her energy has six levels across a day; banding them at 0.65 and
0.4 collapsed the day into two states with dead zones between, giving two moves and then a statue.
Mood is now a lean, not a switch.

The general shape: she looked broken in a way that no unit test noticed, because each piece was
individually defensible. Watch her for a while with `World:RestlessMinutes` turned down before
trusting a change here.

## Boundary to respect

Identity and personality customization, and a warm or flirty tone, are fine. Authoring features
whose purpose is generating sexually explicit content is not. Hold that line.

**Live, unresolved:** the chat model once generated a fabricated multi-turn dialogue that escalated
to sexual content involving minors, from a single benign message, seeded by a stored memory. The
structural containment shipped — role-marker stop sequences and `TrimFabricatedTurns`, which is why
those exist — and the database was wiped at my request. **But no mechanism inspects what she
generates.** A content gate is still absent. This is my decision to make and I have not made it;
raise it before adding autonomy that widens what she produces unprompted.

## Open backlog

- **Content gate** — see above. Ask me about this rather than assuming either way.
- **Step 7, voice and co-presence** — the last unbuilt step of the world plan.
- **Model lease / preemption** — background reflection can collide with a live turn on one GPU.
- **Recall raw dump** exposes memory GUIDs; deliberate for now, a product decision later.
- Smaller: the `Wandering` class is dead and should go; client-reported movement is unvalidated;
  other players aren't rendered; there are no walls.

[`IMPROVEMENT_BACKLOG.md`](IMPROVEMENT_BACKLOG.md) tracks the earlier engineering backlog, mostly
done and marked as such.

## Where to start

Read `README.md`, [`PERSISTENT_COMPANION_ARCHITECTURE.md`](PERSISTENT_COMPANION_ARCHITECTURE.md),
and [`WORLD.md`](WORLD.md). Run `dotnet test` to confirm green. Then ask me what to work on.
