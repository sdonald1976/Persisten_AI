# Audio setup (speech-to-text and text-to-speech)

The companion's chat/embeddings run on Ollama or LM Studio, but **those can't do audio**. Speech
runs in a separate local container — [Speaches](https://github.com/speaches-ai/speaches) (formerly
`faster-whisper-server`) — which exposes OpenAI-compatible endpoints:

| Endpoint | What | Used by |
|----------|------|---------|
| `/v1/audio/transcriptions` | speech → text (Whisper) | `POST /transcribe` |
| `/v1/audio/speech` | text → speech (TTS) | `POST /speak` |

One container covers both, so this is the whole audio dependency. Together the two endpoints close
the voice loop: **mic → `/transcribe` → a normal chat turn → `/speak` → speaker.**

## Start it

From the repo root:

```bash
docker compose up -d speaches       # start in the background
docker compose logs -f speaches     # watch startup (the first model download is slow)
docker compose down                 # stop it
```

Downloaded models are cached in the `speaches-models` volume, so they survive restarts (they are
**not** re-downloaded every run — that was the downside of the old `docker run --rm` command).

Verify it's up:

```bash
curl http://localhost:8000/v1/models
```

## Point the companion at it

Already present in `src/Companion.Api/appsettings.json` under `Models`:

```jsonc
"Transcription": {
  "BaseUrl": "http://localhost:8000/v1",
  "Model": "Systran/faster-whisper-small"   // -base / -medium / -large-v3 for more accuracy
},
"Speech": {
  "BaseUrl": "http://localhost:8000/v1",
  "Model": "speaches-ai/piper-en_US-amy-low", // any TTS model the server serves
  "Voice": "amy",                             // optional default voice; a /speak call can override
  "AudioFormat": "mp3"                        // mp3 (default) · wav · opus · aac · flac · pcm
}
```

The model name is downloaded on first use. Bigger = more accurate but slower and heavier. Remove a
block entirely to disable that half: no `Transcription` → no `/transcribe`; no `Speech` → no `/speak`.

## Use it

Upload an audio file to the API's `POST /transcribe` endpoint; it returns the transcript, which a
client then sends as a normal chat turn:

```bash
curl -F file=@path/to/audio.wav http://localhost:5266/transcribe
# → { "text": "…" }
```

And speak a reply — `POST /speak` returns raw audio bytes (content type follows `AudioFormat`):

```bash
curl -X POST http://localhost:5266/speak \
  -H 'Content-Type: application/json' \
  -d '{"text":"Hey, good to see you back.","voice":"amy"}' \
  --output reply.mp3
```

`voice` is optional (falls back to the configured `Speech.Voice`). This is **file-based, not a live
mic** yet — the push-to-talk mic loop and streaming playback are the next roadmap items (see
[`FUTURE_UX_ROADMAP.md`](FUTURE_UX_ROADMAP.md)).

## GPU (optional, NVIDIA)

Much faster for the larger models. Install the NVIDIA Container Toolkit on the host, then in
`docker-compose.yml` switch the image to `ghcr.io/speaches-ai/speaches:latest-cuda` and uncomment
the `deploy:` block.

## Troubleshooting

- **`/transcribe` returns 503** — transcription isn't configured (no `Models.Transcription`).
- **`/speak` returns 503** — text-to-speech isn't configured (no `Models.Speech`).
- **`/transcribe` errors / connection refused to :8000** — the container isn't up. `docker compose ps`, then
  `docker compose logs speaches`.
- **First transcription hangs for a while** — that's the model downloading; watch the logs. Later
  runs are fast (cached in the volume).
- **Port 8000 already in use** — change the host side of the mapping in `docker-compose.yml`
  (e.g. `"8001:8000"`) and update `Transcription.BaseUrl` to match.
- **`whisper-1` doesn't work** — that's OpenAI's *cloud* model id; it doesn't exist locally. Use a
  `Systran/faster-whisper-*` name.

## Using it from the web client (push-to-talk)

The reference client (`wwwroot/index.html`, served at `/`) has the round trip wired up:

- **🎤 Hold to talk** — press and hold the mic button, speak, release. It records from your mic,
  posts to `/transcribe`, and sends the transcript as a normal turn. The companion's reply to a
  spoken turn is **spoken back** automatically (voice in → voice out).
- **🔈 Speak** — toggle in the header to read *every* reply aloud (including typed ones).
- **Streaming playback** — the reply is synthesized sentence-by-sentence *as it streams in* and the
  clips play in order, so the companion starts talking within a sentence instead of after the whole
  reply is written. Synthesis runs ahead of playback to keep the gaps between clips small.
- Starting to talk stops in-progress playback and cancels any pending clips (basic barge-in).

Both degrade gracefully: if the server has no `Transcription` the mic disables itself with a note;
if it has no `Speech`, the speak toggle does. Mic capture needs a secure context — `localhost` counts,
but a plain-`http` LAN address won't grant microphone access (use `localhost` or serve over HTTPS).

## What's next for the voice loop

Still to come: **hands-free barge-in** — interrupt playback just by starting to talk (voice-activity
detection), instead of having to hold the mic. After that, a 3D avatar with lip-sync. See
[`FUTURE_UX_ROADMAP.md`](FUTURE_UX_ROADMAP.md).
