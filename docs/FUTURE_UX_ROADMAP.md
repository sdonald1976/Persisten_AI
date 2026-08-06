# Future UX & Multimodal Roadmap

Where the companion's interface is heading: away from slash commands, toward a
talk-to-it experience that a voice + 3D-avatar front-end can drive. Captured here so the
direction survives across sessions; not all of it is built.

## Principle: conversation is the UI

Slash commands are a CLI crutch. The companion understands **intent from plain language**, so
every action is something you can *say* — essential for voice, where you can't speak a slash.
Destructive intents (forget, merge) get a spoken confirmation, since voice has no undo button.

**Built (this repo):** `IIntentParser` / `RuleBasedIntentParser` routes utterances like
"forget that", "that's wrong", "be more concise", "what do you remember about me", "that was
great" to actions; unrecognized input is a normal turn. Slash commands remain as optional
shortcuts. An LLM tool-calling parser can replace the rule-based one behind the same interface.

## Split the brain from the face

The companion logic (memory, retrieval, turn pipeline, curation, intents) should be a
**headless, streaming service**; the UI is a thin client. `Core` is already pure and the turn
already streams, so wrapping `ICompanion` in a local HTTP + WebSocket API is a small step — and
it's what lets a web/Unity avatar plug in without embedding .NET.

```
FACES:      CLI  │  Web (WebRTC mic/cam + three.js avatar)  │  Unity/desktop
                       │  local HTTP + WebSocket (stream text · audio · visemes · emotion)
BRAIN:      Companion API → ICompanion pipeline + IntentRouter + Persona
PROVIDERS:  chat · embeddings · vision · STT (whisper) · TTS   (all behind interfaces)
```

## Multimodal turn + voice loop

A turn's inputs can be text, **audio** (→ whisper), or a **camera frame** (→ vision); outputs
are text + **audio (TTS)** + expression cues.

```
mic → VAD → STT → [turn] → LLM tokens (stream) → TTS → speaker
                                              └→ visemes → avatar lip-sync
camera → frames (opt-in) → vision model → context
```

Local building blocks: whisper (built — `/transcribe`), **Piper** for TTS (fast, local, emits
phoneme timing for lip-sync), a web avatar (three.js + Ready Player Me) or Unity. Start with
**push-to-talk**; add wake-word and **barge-in** (interrupt mid-sentence) later.

## Style: editable now, trainable later

- **Now (built):** an editable **persona** prepended to the system prompt, adjustable by
  talking ("be more concise", "talk like a pirate"). Reply **feedback** ("that was great/bad")
  is captured as training signal.
- **Later:** feedback → **DPO** shapes a style LoRA (see `training/`). Facts always stay in the
  forgettable memory layer, never baked into weights.

## Privacy line (design in, don't bolt on)

Always-on mic/camera needs explicit, local-only consent and a dead-obvious mute. This fits the
project's local-first, forgettable ethos and must precede any ambient capture.

## Suggested order

1. ✅ Natural-language intents + editable persona + feedback capture.
2. Headless streaming API (brain/face split).
3. Voice loop (push-to-talk → whisper → turn → Piper TTS).
4. 3D avatar front-end (visemes + emotion).
5. Opt-in camera (vision frames), privacy-gated.
