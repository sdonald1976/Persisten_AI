# Companion API (headless brain)

`Companion.Api` is a local HTTP + WebSocket service that wraps the companion **brain**
(`IAgent`) so any front-end — a web page, a Unity/desktop app, a voice + 3D-avatar client —
can drive it without embedding .NET. It's the "brain/face split": `Core` stays pure, and this
project only translates the wire to `IAgent` calls and streams the results back.

The same brain powers the CLI, so behavior is identical across faces: plain language is
understood as intent (`"forget that"`, `"be more concise"`, `"what am I working on?"`), replies
stream token-by-token, and destructive actions require a confirmation.

## Run it

```bash
dotnet run --project src/Companion.Api          # serves http://localhost:5266
# then open http://localhost:5266 for the reference chat client
```

By default it runs on the **offline mocks** (no model server needed). To use a real local model,
set the `Models` section exactly as the CLI does (see the main README) — Ollama/LM Studio for
chat/extraction/embeddings, plus optional vision and a separate Whisper server. The schema is
created/upgraded on startup via EF Core migrations, same as the CLI, against `Database:Path`.

CORS is open to any local origin so a browser front-end served from anywhere on the machine can
call it. It binds to `localhost` only — nothing is exposed off the box.

## Concepts

- **Conversation** — start one with `POST /conversations`; pass its `conversationId` on every
  chat call. (A WebSocket connection auto-creates one and sends it in the `ready` frame.)
- **User** — single-user local app; every request defaults to the demo user. Pass `userId` to
  scope to someone else.
- **AgentReply** — every conversational call returns one: `kind` is `Chat` (a generated turn),
  `Action` (an intent was carried out), or `Confirmation` (a yes/no is required before acting).

## HTTP endpoints

| Method | Path | Body / query | Returns |
|--------|------|--------------|---------|
| `GET`  | `/health` | — | status + active provider/models |
| `POST` | `/conversations` | `{ userId?, title?, source? }` | `{ conversationId }` |
| `POST` | `/chat` | `{ conversationId, message, userId? }` | `AgentReply` (non-streaming) |
| `POST` | `/chat/confirm` | `{ conversationId, confirmationToken, confirmed, userId? }` | `AgentReply` |
| `GET`  | `/chat/stream` | `?conversationId=&message=&userId=` | **SSE** token stream |
| `GET`  | `/memories` | `?userId=` | `[{ id, kind, content, status, validity, confidence }]` |
| `GET`  | `/projects` | `?userId=` | `[{ name, status, purpose }]` |
| `GET`  | `/projects/{name}` | `?userId=` | reconstructed project summary |
| `GET`  | `/loops` | `?userId=` | `[{ id, description }]` |
| `GET`  | `/persona` | `?userId=` | `{ persona }` |
| `PUT`  | `/persona` | `{ persona, userId? }` | `{ persona }` |
| `POST` | `/feedback` | `{ conversationId, rating: "positive"\|"negative", note?, userId? }` | `AgentReply` |

`AgentReply` shape:
```json
{ "kind": "Chat|Action|Confirmation", "intent": "Chat|Recall|Forget|…",
  "text": "…", "confirmationToken": null,
  "trace": { "detectedProject": null, "retrieved": [{ "content": "…", "score": 0.42 }],
             "memoriesExtracted": 0, "openLoopsSurfaced": 0 } }
```
`trace` is present only for `Chat` replies (a compact slice of the full `/why` diagnostics).

### Streaming with Server-Sent Events

```
GET /chat/stream?conversationId=<guid>&message=hello
→ event: token   data: {"text":"I "}
  event: token   data: {"text":"remember "}
  …
  event: done    data: { …AgentReply… }      # or  event: error  data: {"message":"…"}
```
`EventSource` in the browser is GET-only, hence the query string.

## WebSocket `/ws` (the avatar/voice channel)

Connect to `ws://localhost:5266/ws` (optional `?userId=`). The server sends a `ready` frame with
a fresh `conversationId`, then it's request/response.

**Client → server**
```json
{ "type": "chat",    "text": "hello",              "conversationId": "<optional override>" }
{ "type": "confirm", "token": "<confirmationToken>", "confirmed": true }
```

**Server → client**
```json
{ "type": "ready", "conversationId": "…" }
{ "type": "token", "text": "…" }                      // one per chunk, for chat turns
{ "type": "reply", "kind": "Chat|Action|Confirmation", "intent": "…",
  "text": "…", "confirmationToken": null }            // terminates a turn
{ "type": "error", "message": "…" }
```

A chat turn streams `token` frames then a final `reply`. Actions/confirmations send just a
`reply`. On a `Confirmation` reply, send a `confirm` frame with the `confirmationToken`.

## Where this goes next

The WebSocket frame types are the extension point for multimodality: alongside `token`, a turn
can emit **audio (TTS)**, **visemes** (lip-sync timing), and **emotion** cues for a 3D avatar,
and accept **audio** (→ Whisper) and **camera frames** (→ vision) as inputs. See
[`FUTURE_UX_ROADMAP.md`](FUTURE_UX_ROADMAP.md).
