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

The companion logic (memory, retrieval, turn pipeline, curation, intents) is a **headless,
streaming service**; every UI is a thin client.

```
FACES:      Web (WebRTC mic/cam + three.js avatar)  │  Unity/desktop
                       │  local HTTP + WebSocket (stream text · audio · visemes · emotion)
BRAIN:      Companion API → IAgent (intents + persona) → ICompanion pipeline
PROVIDERS:  chat · embeddings · vision · STT (whisper) · TTS   (all behind interfaces)
```

**Built (this repo):** the split is real. `IAgent` / `Agent` in `Core` is the one brain surface
— parse an utterance, then run a turn or carry out an intent, returning structured
`AgentReply` data (no printing). The face drives it:

- **`Companion.Api`** is a headless local HTTP + **WebSocket** service wrapping the same
  `IAgent`. Replies **stream** token-by-token over Server-Sent Events (`GET /chat/stream`) and
  over the bidirectional `/ws` channel (`token` → `reply` frames, plus a `confirm` frame for
  destructive actions). Structured read endpoints (`/memories`, `/projects`, `/loops`,
  `/persona`, `/feedback`) serve rich UIs; the conversational path covers the same ground for
  voice. A tiny `wwwroot/index.html` chat client ships as a reference face. See
  [`API.md`](API.md).

Next on this axis: emit **audio (TTS) + visemes + emotion** frames alongside the text tokens
so an avatar can lip-sync and emote — the WebSocket frame types are already the place to add them.

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

- **Now (built):** a configurable **identity** and **personality**, kept separate. Identity is who
  it *is* — name / gender / pronouns (`IdentityOptions`, default "Ava", she/her) — set in config,
  by talking ("your name is Ava", "you're a woman"), or over the API (`GET`/`PUT /identity`); it
  carries across every personality and into voice later. Personality is how it *behaves*: named
  presets — `warm`, `witty`, `direct`, `playful`, `flirty`, `sage` (`PersonalityCatalog`) — give the
  companion an actual voice instead of the flat default of a dry/abliterated model, set in config
  (`Personality:Default`), by talking ("switch to the witty personality"), or via `PUT /personality`.
  On top of the preset, an editable free-text **persona** layers tweaks ("be more concise", "talk
  like a pirate"). Reply **feedback** ("that was great/bad") is captured as training signal.
- **Later:** feedback → **DPO** shapes a style LoRA (see `training/`). Facts always stay in the
  forgettable memory layer, never baked into weights.

## Proactivity & time-awareness

- **Now (built):** the companion initiates instead of only reacting.
  - **Time-aware greetings** — it knows how long it's been (`RelativeTime`, from the user's last
    message across all conversations) and leads with it ("It's been 3 days. Good to see you back…"),
    in the deterministic opener and the model-written one.
  - **Follows up on its own commitments** — when it promises something in a reply ("I'll check in
    tomorrow"), a conservative `CommitmentDetector` captures it as a *companion-owned* open loop
    (`Owner = "companion"`), which the greeter re-raises next session ("Last time I said I'd … —
    want to pick that up?"). Deduped, and it expires after ~14 days so it never nags.
- **Later:** follow up on the user's own upcoming events ("you mentioned an interview Thursday —
  how did it go?"), which needs the extractor to capture planned events with timing; and a
  background job runner (below) for check-ins that happen off-turn.

## Relational & emotional memory

- **Now (built):** the companion remembers not just *facts* but *how things have felt*, and lets that
  shape its tone across sessions.
  - **Emotional signals** — a deterministic, offline `MoodDetector` reads each user message's tone
    (valence + a dominant emotion like "stressed"/"excited", with negation and intensifier handling)
    and appends it to an **append-only** `EmotionalSignal` log. Flat/ordinary messages capture nothing,
    so the log stays signal, not noise. Skipped on private ("don't remember this") turns.
  - **Derived relationship state** — `RelationshipTracker` computes a `RelationshipSnapshot` (recent
    mood, dominant recent emotion, and a rising/slipping trend) *purely from the log*, so it can never
    drift from what actually happened — the same "relational store is authoritative, everything else is
    derived" rule the memory system already follows.
  - **Tied to what it's about** — a deterministic `MoodTopic` extractor pulls the subject a feeling is
    about from the same message ("stressed about **the deadline**", "excited for **the trip**"), and
    when the message doesn't say, the mood attaches to the project the turn resolved to (`ProjectId`).
    Contentless objects ("it", "everything") are rejected — you can't follow up on "it". This is what
    turns a general mood into a specific, caring follow-up: the greeter opens with "Last time you
    seemed nervous about the interview — how's that going?" and the in-turn tone note becomes "the
    user has seemed anxious about the interview lately — it's fine to gently ask how that's going."
  - **Used, not stated** — the snapshot becomes one line of *tone guidance* in the context packet
    ("the user has seemed stressed lately — be a little gentler; don't bring this up unless they do"),
    rendered as prose the model attunes to rather than reads back. The greeter opens with care after a
    low stretch ("you seemed a bit low last time — I'm here if you want to talk about it") or shares in
    good spirits.
- **Later:** longer-horizon mood history beyond the recent window, resolving a follow-up once the
  user answers it (so the same worry isn't re-raised), and emotion cues from voice tone once the audio
  loop lands.

## Long tasks: finish in-turn now, background jobs later

- **Now (built):** an `IReplyGenerator` owns "when to keep going" using **deterministic, topic-free
  signals** — no list of topics like "stories", because the signal isn't the subject:
  1. `finish_reason: "length"` — the server truly cut the reply off mid-token → always continue.
  2. **Deliverable request?** (`CompletionSignals.IsDeliverableRequest`) — did the user ask for a
     *produced artifact* ("write…", "draft…", "list…", "explain in detail", "continue")? A property
     of the request, not the topic. Ordinary conversation is never auto-continued (this is what
     killed the runaway on a plain "how are you").
  3. **Structural incompleteness** (`CompletionSignals.LooksUnfinished`) — a deliverable reply that
     stops mid-sentence, leaves a code fence open, dangles a colon, or ends "…want me to continue?".
  A deliverable request that produced an unfinished-looking reply is continued with **no model
  call**. Each continuation feeds the text so far back so the model resumes the **same** task, it
  streams across rounds, and a **repetition guard** stops the instant a continuation repeats what's
  already written (bounded anyway by `MaxContinuations`).
- **Opt-in last resort — semantic completion check (`CompletionCheck`, off by default):** only for a
  deliverable reply that looks structurally complete but might be a grammatical self-truncation, a
  cheap model can be asked "finished, or cut off?". It's only as good as that judge model, so it's
  off until yours is trusted (watch the logs) and it fails closed. Every reply is stored with its
  generation metadata (`finish_reason`, rounds, truncated, model, token usage) so "why did it stop"
  is answerable, and `LogPayloads` logs the exact prompt+reply for debugging.
- **Later (if in-turn ever isn't enough):** a small **background job runner** — "work on X and
  tell me when it's done": queue a task, run the same auto-continuing generation off-turn,
  persist progress + result, and surface completion as a WebSocket frame / next-session opener
  ("I finished that story — want to hear it?"). Same completion signal, just detached from the
  chat turn. Deliberately not built until a real need shows up: an in-turn streamed answer
  covers the "finish the task" case with far less machinery.

## Privacy line (design in, don't bolt on)

Always-on mic/camera needs explicit, local-only consent and a dead-obvious mute. This fits the
project's local-first, forgettable ethos and must precede any ambient capture.

## Suggested order

1. ✅ Natural-language intents + editable persona + feedback capture.
2. ✅ Headless streaming API (brain/face split) — `Companion.Api` (HTTP + SSE + WebSocket).
3. Voice loop (push-to-talk → whisper → turn → Piper TTS).
4. 3D avatar front-end (visemes + emotion).
5. Opt-in camera (vision frames), privacy-gated.
