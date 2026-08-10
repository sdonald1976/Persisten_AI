# Handoff / continuation brief

Paste this into a fresh session (local Claude Code or otherwise) to pick the project back up. Keep
it current as the project evolves.

---

I'm continuing work on my Persistent AI Companion project. Please read this brief, then confirm
you're oriented before making changes.

## What it is

A local-first, persistent AI companion with durable memory. A pure "brain" (Core) with swappable
"faces" (CLI, headless HTTP+WebSocket API) and swappable model providers (offline Mock, or
OpenAI-compatible Ollama/LM Studio). The point is continuity: it remembers me across sessions,
initiates conversation, and has a configurable identity + personality.

## Stack & layout

- .NET 9, C#, EF Core + SQLite, xUnit. Solution: `Persisten_AI.sln`
  - `src/Companion.Core` — domain + interfaces + all logic (retrieval, extraction, personality,
    reply generation). Pure, no I/O deps.
  - `src/Companion.Infrastructure` — EF, model provider adapters, DI composition root.
  - `src/Companion.Cli` — terminal face.
  - `src/Companion.Api` — headless HTTP + SSE + WebSocket face (+ `wwwroot` reference web client).
  - `tests/Companion.Tests` — full suite, all passing (263 at last handoff).
- `global.json` pins .NET 9 (`9.0.313`, `latestFeature`). Use a .NET 9 SDK; if yours differs, adjust
  `global.json`.
- EF migrations: `dotnet ef migrations add <Name> --project src/Companion.Infrastructure
  --startup-project src/Companion.Infrastructure` (the `dotnet-ef` tool).
- Real-model config (`appsettings.json` in Cli + Api): Ollama at `http://localhost:11434/v1`, chat
  model `huihui_ai/dolphin3-abliterated`, embeddings `nomic-embed-text`. Audio (Whisper STT) runs
  via `docker compose up -d speaches` — see [`AUDIO.md`](AUDIO.md).

## Git conventions

- Work on branch: `claude/persistent-ai-companion-memory-jxz4fz`.
- Commit with clear messages; end commit bodies with a `Co-Authored-By` line. Don't push to other
  branches or open PRs unless I ask.
- Run the full test suite (`dotnet test`) and keep it green before committing. Restore `global.json`
  to its committed state before committing if you changed it to build.

## Key architecture already built

- **Turn pipeline** (`Companion.cs`): store msg → resolve project/ambiguity (deterministic
  clarification control-flow) → retrieve → assemble context packet → generate → store (with
  generation metadata) → extract memories → update project/open-loops.
- **Retrieval**: hybrid scoring with a `RelevanceFloor` so unrelated memories don't bleed into every
  turn.
- **Reply generation** (`IReplyGenerator`): owns "when to keep going" — continues on
  `finish_reason=length`, and for *deliverable* requests (`CompletionSignals`) that look
  structurally unfinished; a repetition guard prevents runaway loops. A semantic completion judge
  exists but is **off by default** (`CompletionCheck`).
- **Identity** (who it is): name/gender/pronouns, default "Ava / female / she/her"; config + talk +
  API (`/identity`). Separate from personality.
- **Personality** (how it behaves): named presets in `PersonalityCatalog` — warm, witty, direct,
  playful, flirty, sage — config default (`Personality:Default`), switch by talking or API
  (`/personality`). Free-text persona tweaks layer on top.
- **Greeting**: `LlmGreeter` writes a natural, memory-grounded opener (deterministic `Greeter` as
  grounding + fallback).
- **Observability**: each assistant `Message` stores finish_reason/rounds/truncated/model/tokens;
  `EndpointOptions.LogPayloads` logs the full prompt+reply.

## Boundary to respect

The assistant previously declined to author presets/features whose purpose is generating sexually
explicit content. Please hold that line: identity/personality customization and tasteful/flirty tone
are fine; explicit sexual content generation is not. I can write my own presets locally.

## What's next (from [`FUTURE_UX_ROADMAP.md`](FUTURE_UX_ROADMAP.md)), not yet built

1. **Voice output (TTS)** — reuse the Speaches container's OpenAI-compatible `/v1/audio/speech`; the
   recommended next step.
2. **Push-to-talk mic loop** (live audio in, not just `/transcribe` file).
3. **3D avatar front-end** (viseme + emotion frames over the existing WebSocket).
4. **Opt-in camera** (vision frames), privacy-gated.
5. **Background job runner** ("work on X, tell me when it's done") — foundation exists (completion
   signals + generation metadata).
6. **Style training (DPO → LoRA)** from captured feedback; `training/` scaffold exists.

Start by reading `README.md`, [`PERSISTENT_COMPANION_ARCHITECTURE.md`](PERSISTENT_COMPANION_ARCHITECTURE.md),
and [`FUTURE_UX_ROADMAP.md`](FUTURE_UX_ROADMAP.md), run `dotnet test` to confirm green, then ask me
what to work on.
