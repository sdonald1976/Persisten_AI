# Companion API (headless brain)

`Companion.Api` is a local HTTP + WebSocket service that wraps the companion **brain**
(`IAgent`) so any front-end — a web page, a Unity/desktop app, a voice + 3D-avatar client —
can drive it without embedding .NET. It's the "brain/face split": `Core` stays pure, and this
project only translates the wire to `IAgent` calls and streams the results back.

Plain language is
understood as intent (`"forget that"`, `"be more concise"`, `"what am I working on?"`), replies
stream token-by-token, and destructive actions require a confirmation.

## Run it

```bash
dotnet run --project src/Companion.Api          # serves http://localhost:5266
# then open http://localhost:5266 for the reference chat client
```

By default it runs on the **offline mocks** (no model server needed). To use a real local model,
set the `Models` section (see the main README) — Ollama/LM Studio for
chat/extraction/embeddings, plus a separate Whisper server for `/transcribe`. The schema is
created/upgraded on startup via EF Core migrations (with an automatic pre-migration backup),
against `Database:Path`.

## Security (secure-by-default)

- **Loopback only.** The API binds to `http://127.0.0.1:5266` by default and is never exposed on
  LAN interfaces unless you explicitly set `Urls` / `ASPNETCORE_URLS`.
- **Local token auth (available; shipped off).** The bundled `appsettings.json` sets
  `Api:AuthEnabled=false` for solo local convenience, so calls need no token out of the box. Set
  `Api:AuthEnabled=true` to require one: a random token is generated on first startup and saved to
  `.companion-api-token` next to the database (or set `Api:Token`). Send it as
  `Authorization: Bearer <token>` or `X-Companion-Key: <token>`; because `EventSource` and browser
  WebSockets can't set headers, SSE/WS also accept `?access_token=<token>`.
- **CORS allow-list.** Only origins in `Api:AllowedOrigins` (default `http://localhost:5173`,
  `http://127.0.0.1:5173`) may call the API from a browser. There is no wildcard, and no cookies
  are used — auth is an explicit header/token, so CSRF does not apply.
- **Sanitized errors.** Failures return `{ "error", "message", "correlationId" }` with a safe
  message; the detailed exception is only in the server log, keyed by the same correlation id.

Example `Api` configuration:
```jsonc
"Api": {
  "AuthEnabled": true,
  "Token": "",                                  // empty → generated + saved to .companion-api-token
  "AllowedOrigins": [ "http://localhost:5173", "http://127.0.0.1:5173" ]
}
```

## Concepts

- **Conversation** — start one with `POST /conversations`; pass its `conversationId` on every
  chat call. (A WebSocket connection auto-creates one and sends it in the `ready` frame.) A
  request for a conversation that doesn't exist or isn't yours returns **404** — nothing is stored,
  retrieved, or extracted.
- **User** — single-user local app; the active user is derived from the server's trusted context,
  **never** from the request. There is no `userId` parameter on any endpoint.
- **AgentReply** — every conversational call returns one: `kind` is `Chat` (a generated turn),
  `Action` (an intent was carried out), or `Confirmation` (a yes/no is required before acting).

## HTTP endpoints

All endpoints require the API token (see Security). Bodies/queries never carry a user id.

| Method | Path | Body / query | Returns |
|--------|------|--------------|---------|
| `GET`  | `/health` | — | status + active provider/models |
| `GET`  | `/greeting` | — | `{ message, openers[] }` — memory-grounded session openers |
| `POST` | `/conversations` | `{ title?, source? }` | `{ conversationId }` |
| `POST` | `/chat` | `{ conversationId, message }` | `AgentReply` (404 if unknown conversation) |
| `POST` | `/chat/confirm` | `{ conversationId, confirmationToken, confirmed }` | `AgentReply` |
| `GET`  | `/chat/stream` | `?conversationId=&message=` | **SSE** token stream |
| `GET`  | `/memories` | — | `[{ id, kind, content, status, validity, confidence }]` |
| `GET`  | `/projects` | — | `[{ name, status, purpose }]` |
| `GET`  | `/projects/{name}` | — | reconstructed project summary |
| `GET`  | `/loops` | — | `[{ id, description }]` |
| `GET`  | `/persona` | — | `{ persona }` — free-text style tweaks layered on the personality |
| `PUT`  | `/persona` | `{ persona }` | `{ persona }` |
| `GET`  | `/personality` | — | `{ active, presets[] }` — the active personality preset and the catalog to choose from |
| `PUT`  | `/personality` | `{ preset }` | `{ active }` (400 on an unknown preset name) |
| `GET`  | `/identity` | — | `{ name, gender, pronouns }` — who the companion is |
| `PUT`  | `/identity` | `{ name?, gender?, pronouns? }` | `{ name, gender, pronouns }` (only provided fields change) |
| `POST` | `/feedback` | `{ conversationId, rating: "positive"\|"negative", note? }` | `AgentReply` |
| `POST` | `/transcribe` | multipart form-data, field `file` (audio) | `{ text }` (503 if no Whisper server configured) |
| `POST` | `/speak` | `{ text, voice? }` | audio bytes (`audio/mpeg` etc.; 503 if no TTS server configured) |

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
GET /chat/stream?conversationId=<guid>&message=hello&access_token=<token>
→ event: token   data: {"text":"I "}
  event: token   data: {"text":"remember "}
  …
  event: done    data: { …AgentReply… }      # or  event: error  data: {"error":"…","message":"…","correlationId":"…"}
```
`EventSource` in the browser is GET-only and can't set headers, hence the query string (including
`access_token`).

## WebSocket `/ws` (the avatar/voice channel)

Connect to `ws://127.0.0.1:5266/ws?access_token=<token>`. The server sends a `ready` frame with
a fresh `conversationId`, then it's request/response. The `ready` greeting is the instant
deterministic opener; when a real model is configured, a `greeting` frame follows once the model
has rephrased it in the companion's own voice — replace the shown greeting text in place (the
openers don't change). It may never arrive (offline mocks, model failure): the `ready` message
is always a complete greeting on its own.

**Client → server**
```json
{ "type": "chat",    "text": "hello",              "conversationId": "<optional override>" }
{ "type": "confirm", "token": "<confirmationToken>", "confirmed": true }
```

**Server → client**
```json
{ "type": "ready", "conversationId": "…", "message": "…", "openers": ["…"] }  // openers = session starters
{ "type": "greeting", "message": "…" }                // model-phrased greeting upgrade (optional, once)
{ "type": "token", "text": "…" }                      // one per chunk, for chat turns
{ "type": "reply", "kind": "Chat|Action|Confirmation", "intent": "…",
  "text": "…", "confirmationToken": null }            // terminates a turn
{ "type": "error", "error": "…", "message": "…", "correlationId": "…" }   // sanitized
```

A chat turn streams `token` frames then a final `reply`. Actions/confirmations send just a
`reply`. On a `Confirmation` reply, send a `confirm` frame with the `confirmationToken`.

## Where this goes next

The WebSocket frame types are the extension point for multimodality: alongside `token`, a turn
can emit **audio (TTS)**, **visemes** (lip-sync timing), and **emotion** cues for a 3D avatar,
and accept **audio** (→ Whisper) and **camera frames** (→ vision) as inputs. See
[`FUTURE_UX_ROADMAP.md`](FUTURE_UX_ROADMAP.md).
