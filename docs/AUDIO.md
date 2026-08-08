# Audio setup (speech-to-text, and text-to-speech later)

The companion's chat/embeddings run on Ollama or LM Studio, but **those can't do audio**. Speech
runs in a separate local container — [Speaches](https://github.com/speaches-ai/speaches) (formerly
`faster-whisper-server`) — which exposes OpenAI-compatible endpoints:

| Endpoint | What | Used by |
|----------|------|---------|
| `/v1/audio/transcriptions` | speech → text (Whisper) | `/transcribe <file>` today |
| `/v1/audio/speech` | text → speech (TTS) | the upcoming voice-output step |

One container covers both, so this is the whole audio dependency.

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

Already present in `src/Companion.Cli/appsettings.json` and `src/Companion.Api/appsettings.json`
under `Models`:

```jsonc
"Transcription": {
  "BaseUrl": "http://localhost:8000/v1",
  "Model": "Systran/faster-whisper-small"   // -base / -medium / -large-v3 for more accuracy
}
```

The model name is downloaded on first use. Bigger = more accurate but slower and heavier. Remove the
`Transcription` block entirely to disable audio.

## Use it

```
/transcribe path/to/audio.wav
```

It transcribes the file and feeds the text in as a normal turn. This is **file-based, not a live
mic** yet — the push-to-talk mic loop is the next roadmap item (see
[`FUTURE_UX_ROADMAP.md`](FUTURE_UX_ROADMAP.md)).

## GPU (optional, NVIDIA)

Much faster for the larger models. Install the NVIDIA Container Toolkit on the host, then in
`docker-compose.yml` switch the image to `ghcr.io/speaches-ai/speaches:latest-cuda` and uncomment
the `deploy:` block.

## Troubleshooting

- **`/transcribe` errors / connection refused** — the container isn't up. `docker compose ps`, then
  `docker compose logs speaches`.
- **First transcription hangs for a while** — that's the model downloading; watch the logs. Later
  runs are fast (cached in the volume).
- **Port 8000 already in use** — change the host side of the mapping in `docker-compose.yml`
  (e.g. `"8001:8000"`) and update `Transcription.BaseUrl` to match.
- **`whisper-1` doesn't work** — that's OpenAI's *cloud* model id; it doesn't exist locally. Use a
  `Systran/faster-whisper-*` name.

## Text-to-speech (coming with the voice loop)

When we add voice output, TTS will reuse this same Speaches container via `/v1/audio/speech` — no
new service to run. That's why the compose file and this doc are named for "audio" rather than just
Whisper.
