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
deterministic mock/rule-based providers. The full test suite passes (`dotnet test`), including
the five reference scenarios. Design docs are under `docs/`.

The companion logic is a **headless brain** (`IAgent`) that every face drives identically: the
CLI is a thin client over it, and a local **HTTP + WebSocket API** (`Companion.Api`) exposes the
same brain — streaming replies token-by-token — so a web page, desktop app, or future voice + 3D
avatar can plug in without embedding .NET. See [`docs/API.md`](docs/API.md).

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
| [`docs/CURRENT_ARCHITECTURE_ASSESSMENT.md`](docs/CURRENT_ARCHITECTURE_ASSESSMENT.md) | The initial (now historical) assessment — the repo began empty, hence a greenfield build. |
| [`docs/PERSISTENT_COMPANION_ARCHITECTURE.md`](docs/PERSISTENT_COMPANION_ARCHITECTURE.md) | Components, data flow, storage, retrieval, memory lifecycle, model boundaries, Mermaid diagram. |
| [`docs/IMPLEMENTATION_PLAN.md`](docs/IMPLEMENTATION_PLAN.md) | Phased tasks, dependencies, acceptance criteria, test requirements. |
| [`docs/FIRST_VERTICAL_SLICE.md`](docs/FIRST_VERTICAL_SLICE.md) | Exact files proposed for the first vertical slice. |

## Stack

.NET 9 · EF Core + SQLite (authoritative store) · in-process vector index (swappable) ·
mockable model providers (local/hosted/mock) · xUnit.

## Project layout

```
src/Companion.Core            domain records, interfaces, retrieval + extraction + project + curation logic,
                              the IAgent brain facade (intents + persona + turn, returns structured replies)
src/Companion.Infrastructure  EF Core store, SQLite BLOB vector index, mock + rule-based/LLM extractors, DI
src/Companion.Cli             thin console face over IAgent: chat + plain-language intents, plus /why and
                              /seed, /remember, /projects, /loops, /forget, /correct, /consolidate … shortcuts
src/Companion.Api             headless HTTP + WebSocket face over IAgent: /chat, SSE /chat/stream, /ws,
                              and structured /memories, /projects, /loops, /persona, /feedback (+ reference web client)
tests/Companion.Tests         the test suite (run `dotnet test`): retrieval, packet, provenance, isolation, score math,
                              extraction (accept/merge/reject/review/supersede), confidence, normalizer,
                              LLM parsing, resolution + clarification, project summary, open-loop create/close,
                              supersession, correction/forget/dispute/merge, project merge/split,
                              consolidation, and the Scenario A–E benchmark
```

## Build & run

Requires the **.NET 9 SDK** (the repo pins `9.0.3xx` via `global.json`; a .NET 8 SDK will not
build it). Install it from https://dotnet.microsoft.com/download, then:

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
upgrades in place as new versions add tables. Before applying any pending migration the database
is **backed up** (`companion.db.bak-<timestamp>`), and if a migration fails it is **restored** —
you never lose memories to a schema change. You can also snapshot/restore explicitly:

```bash
dotnet run --project src/Companion.Cli -- export my-memories.db   # consistent snapshot
dotnet run --project src/Companion.Cli -- import my-memories.db   # restore (backs up current first)
```

(A database created before migrations existed can't be upgraded in place — `export` it, delete
`companion.db`, re-run to recreate, then `import`.)

## Using a real model (Ollama / LM Studio)

By default the app runs on offline mocks. To use a real local model, set the `Models` section
in `src/Companion.Cli/appsettings.json` (or override with env vars, e.g.
`Models__Provider=OpenAiCompatible`). Both Ollama and LM Studio speak the OpenAI `/v1` API, so
the same adapter works for both — just change the base URL and model names.

Each job can use its **own** model — a big conversational model, a small structured-output
extraction model, a cheap/fast summarizer, and a dedicated embedder. `Extraction` and
`Summarizer` are optional; omit them to reuse the `Chat` model.

**Ollama** (default port 11434):
```jsonc
"Models": {
  "Provider": "OpenAiCompatible",
  "Chat":       { "BaseUrl": "http://localhost:11434/v1", "Model": "dolphin-mixtral:8x7b" }, // conversation: larger/better
  "Extraction": { "BaseUrl": "http://localhost:11434/v1", "Model": "llama3.1:8b" },          // structured-output-friendly
  "Summarizer": { "BaseUrl": "http://localhost:11434/v1", "Model": "llama3.2:3b" },          // cheap/fast
  "Embeddings": { "BaseUrl": "http://localhost:11434/v1", "Model": "nomic-embed-text" }      // dedicated embedder
}
```
```bash
ollama pull dolphin-mixtral:8x7b   # or your conversational pick
ollama pull llama3.1:8b
ollama pull llama3.2:3b
ollama pull nomic-embed-text
```
The four jobs map to distinct model slots internally
(`IChatModel` per role via keyed DI, plus `IEmbeddingModel`), so they can point at different
models — even different servers — independently.

Each chat/vision endpoint also accepts optional sampling controls: `Temperature` (lower =
less random; ~0.2 for extraction, ~0.6 for conversation), `MaxTokens`, and — the fix if a small
local model **repeats itself** — `FrequencyPenalty` and `PresencePenalty` (both honored by Ollama
and LM Studio; try ~0.6 / ~0.3 on the conversational model, which the shipped config sets). Leave
any of them out to use the server's defaults. On startup the CLI prints a banner showing the
active provider and the per-role models (or `Mock (offline)`), so you can tell at a glance what
you're running.

**LM Studio** (start its local server; default port 1234):
```jsonc
"Models": {
  "Provider": "OpenAiCompatible",
  "Chat":       { "BaseUrl": "http://localhost:1234/v1", "Model": "<your chat model id>" },
  "Embeddings": { "BaseUrl": "http://localhost:1234/v1", "Model": "<your embedding model id>" }
}
```
Load both a chat model and an embedding model in LM Studio, and use the exact model ids it
shows. `ApiKey` can be left empty (LM Studio accepts anything).

When a real model is configured, extraction and summarization use it too
(`LlmMemoryExtractor`, `LlmSummarizer`) instead of the rule-based/mock stand-ins.

### Streaming, images, and voice

- **Streaming** — with a real provider, the assistant's reply streams to the console as it's
  generated (nice with a big local conversational model). No config needed; the full reply is
  still stored and shown by `/why`.
- **Vision** (`/image <path> [caption]`) — add a `Vision` block to `Models` with a multimodal
  model, and the companion will describe the image and remember it as part of the conversation:
  ```jsonc
  "Vision": { "BaseUrl": "http://localhost:11434/v1", "Model": "llama3.2-vision" }
  ```
  ```bash
  ollama pull llama3.2-vision   # or load a vision GGUF (e.g. llava/qwen2-vl) in LM Studio
  ```
- **Voice / Whisper** (`/transcribe <audio file>`) — transcribes an audio file and sends it as a
  turn. **Ollama and LM Studio cannot run Whisper** — they have no audio support, so there's
  nothing to `ollama pull`. You need a *separate* server that exposes an OpenAI-compatible
  `/v1/audio/transcriptions` endpoint. Easiest is **Speaches** (formerly `faster-whisper-server`):
  ```bash
  docker run --rm -p 8000:8000 ghcr.io/speaches-ai/speaches:latest-cpu   # or :latest-cuda with --gpus=all
  ```
  ```jsonc
  "Transcription": {
    "BaseUrl": "http://localhost:8000/v1",
    "Model": "Systran/faster-whisper-small"   // -base / -medium / -large-v3; downloaded on first use
  }
  ```
  Alternatives: `whisper.cpp`'s `whisper-server` (model chosen at launch, so `Model` is ignored),
  or LocalAI (name the model in its config). `whisper-1` is OpenAI's *cloud* model id — it does
  not exist locally. File-based, not live mic. Leave the block out to disable.

> **Important:** pick your provider **before** running `seed`. Memories are stored with the
> embedding model's vectors; if you seed with the mock (128-dim) and later switch to a real
> model (e.g. 768-dim), the dimensions won't match and old memories stop being retrieved.
> Delete `companion.db` and re-seed after switching embedding models.

If the model server isn't running, a turn prints a clear `⚠` message instead of crashing.

**You don't have to start — it does.** At the top of each session the companion greets you and
offers a few concrete openers drawn from what it remembers (your open loops, recent projects), so
there's never a blank prompt to fill. Pick one, type your own, or just say "hi" — a bare greeting
gets the same memory-grounded welcome, not a canned reply.

**You mostly just talk to it — no slash commands needed.** Plain language is understood as
intent: "what do you remember about me?", "forget that", "that's wrong", "be more concise" /
"talk like a pirate" (editable **persona**), "that was great" / "that was unhelpful" (reply
**feedback**, saved as style-tuning signal), "what am I working on?", "what's unfinished?",
"consolidate your memories". Destructive actions ask for confirmation. Anything unrecognized is
just a normal turn. See [`docs/FUTURE_UX_ROADMAP.md`](docs/FUTURE_UX_ROADMAP.md) for where this
is heading (voice + 3D avatar).

Slash commands remain as optional shortcuts: `/project <name>`, `/projects`, `/loops`,
`/remember` (shows short ids), `/forget <id>`, `/dispute <id>`, `/correct <id> <fact>`,
`/reassign <id> <project>`, `/mergeprojects <a> into <b>`, `/consolidate`. `/why` shows the full turn diagnostics — retrieval scores/reasons,
project resolution (ranked candidates + any clarifying question), open loops surfaced,
project-state updates, and extraction verdicts (incl. supersessions). The model providers are
deterministic (`MockChatModel`, `MockEmbeddingModel`, `RuleBasedMemoryExtractor`,
`MockSummarizer`), so everything runs offline. Real local/hosted providers — including
`LlmMemoryExtractor` and an LLM summarizer — plug in behind the same interfaces.

## Run it headless (HTTP + WebSocket)

The CLI is just one face. The same brain runs as a local service for web/desktop/voice front-ends:

```bash
dotnet run --project src/Companion.Api     # http://127.0.0.1:5266 (loopback only)
#  open http://127.0.0.1:5266 for the reference chat client (paste the token when prompted)
```

**Local-first security.** It always binds to **loopback only** and restricts browser access to an
explicit **CORS allow-list** (`Api:AllowedOrigins`). Token auth is **available but shipped off**
for solo local convenience (`Api:AuthEnabled=false` in `appsettings.json`) — the web client just
connects. Turn it on by setting `Api:AuthEnabled=true`: a token is then auto-generated on first
startup and saved to `.companion-api-token` next to the DB, and every REST/SSE/WebSocket call must
present it (`Authorization: Bearer <token>` / `X-Companion-Key`, or `?access_token=` for
SSE/WebSocket — the web client prompts for it and remembers it). Errors always come back sanitized
as `{ error, message, correlationId }`; the detail stays in the server log.

It defaults to the offline mocks (no model server needed) and uses the same `Models`
configuration as the CLI when you want a real model. Replies stream token-by-token over
Server-Sent Events (`GET /chat/stream`) and the bidirectional `/ws` channel; plain-language
intents, persona edits, and feedback all work over the wire, and there are structured
`/memories`, `/projects`, `/loops`, and `/persona` endpoints for rich UIs. A request for a
conversation that doesn't exist or isn't yours returns 404. Full endpoint, auth, and frame
reference: [`docs/API.md`](docs/API.md).

## Fine-tuning (optional, experimental)

You can turn the companion's own validated data into a small LoRA fine-tune (best target: the
extraction model) and load it back into Ollama. It's entirely optional and runs **outside** the
app — the chat loop never trains or swaps models on its own, and **facts stay in memory, never
baked into weights**. See [`training/README.md`](training/README.md) for the build → train →
evaluate → promote (→ rollback) workflow. `training/build_dataset.py` reads `companion.db`
directly (skipping anything you've `/forget`-ten), so there's no separate export step.

## Safety, privacy & provider hardening

The companion is built to be safe for real local use, not just a demo:

- **The local API is locked down.** Loopback-only binding, a required local **API token** on
  every REST/SSE/WebSocket call, and an explicit **CORS allow-list** (no wildcard, no cookies).
  Errors are returned sanitized (`{ error, message, correlationId }`) with detail kept server-side.
- **User isolation is enforced in the database, not just the app.** Every user-owned read/write
  (memories, evidence, revisions, projects, events, decisions, open loops, conversations,
  messages, pending clarifications) is scoped by `userId` in the query itself — possession of a
  record id owned by another user reveals nothing and mutates nothing. The active user comes from
  a trusted `IUserContext`, never from an API request. A message can only belong to an existing,
  owned conversation (enforced by a foreign key and the store); an unknown/foreign conversation is
  a 404, never a silent orphan.
- **No fabricated provenance.** An extracted memory is stored only when its cited excerpt is
  actually found in a real user message; unverifiable candidates are rejected, never back-filled.
- **Ambiguity is a control-flow state.** When a reference like "that board" matches two projects,
  the turn asks a deterministic clarifying question and stores a **pending clarification** — it
  does not retrieve-to-answer, call the model, extract memories, or touch project state. The next
  message resolves it ("the buoy one", "the first one", "never mind"); vague replies ask again
  rather than guess, and the pending item survives a restart.
- **Model output is untrusted.** LM Studio/Ollama calls go through Microsoft's `IHttpClientFactory`
  (named clients per role, managed handler lifetime) with a **Polly** transient-retry policy
  (429/5xx, honoring `Retry-After`), per-role timeouts, clean cancellation-vs-timeout mapping,
  provider-specific errors, a response-size cap, and embedding-dimension validation. The provider
  value itself is validated at startup (unknown providers fail fast, no silent fallback).
  Extraction requests JSON mode and is parsed by a validated, balanced-bracket parser (not
  "first `[` to last `]`"); the model proposes memories, deterministic code decides what persists.
  `/health` (CLI) tests the configured endpoints.
- **Privacy controls.** Say "don't remember this conversation" (or "keep this off the record") and
  the turn still replies but creates **no durable memory** — extraction and project updates are
  skipped (raw messages are still stored for in-session context). Obvious credentials (API keys,
  tokens, private keys, `password: …`) are rejected from memory persistence outright.
- **Safe upgrades.** Migrations back up the database first and restore it on failure; `export` /
  `import` give explicit snapshots — a schema change never costs you your memories.

Schema changes ship as EF Core migrations applied automatically on startup, each preceded by an
automatic backup.

## Where next

The six-phase plan in `docs/IMPLEMENTATION_PLAN.md` is complete, and the brain now runs behind a
headless streaming API. The natural next step (see [`docs/FUTURE_UX_ROADMAP.md`](docs/FUTURE_UX_ROADMAP.md))
is the **voice loop** — push-to-talk → Whisper → turn → Piper TTS — followed by a 3D-avatar
front-end that consumes audio/viseme/emotion frames over the same WebSocket. Other follow-ups:
harden privacy controls before any ambient capture, and swap the in-process vector index for a
dedicated ANN store.
