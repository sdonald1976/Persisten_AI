# The inner monologue: between-session reflection

Everything else in the companion runs when the user speaks. Reflection is the first thing that
runs on the **companion's own clock**: while the user is away, it thinks the recent
conversations over, writes a short private diary entry (a *musing*), and mints *curiosities* —
things it genuinely wants to know. The next session can then truthfully say *"I was thinking
about what you said…"* — the thought exists, timestamped, written while the user was gone.

> Guiding principle, inherited from the project: build continuity, not consciousness. A musing
> is bookkeeping for attention and warmth — it is never presented as more than that.

## The pass

`Reflector` (Core) runs one pass:

1. Read everything **rememberable** since the last watermark — `IConversationStore.
   GetRememberableMessagesSinceAsync` excludes do-not-remember conversations *in the query*, so
   private turns can never reach a thought. Fewer than `ReflectionMinNewMessages` new user
   messages → no pass (a one-line visit isn't worth a thought, and quiet days cost nothing).
2. Assemble the material: the new turns, the companion's own last musings (so the monologue
   continues rather than restarting), the curiosities it already holds (so it never wonders the
   same thing twice), open loops, and the recent emotional read.
3. Ask the conversational model to think, returning JSON:
   `{"musing": string|null, "curiosities": [{"question","about","reason"}]}`.
4. Store the result in the reflection diary (`Reflections` + `Curiosities` tables):
   - **Musing written** — embedded (a past thought can be found again) and kept, capped in
     length; up to `ReflectionMaxCuriosities` deduped curiosities are minted alongside.
   - **Quiet day** (`"musing": null`) — a watermark-only entry. The pass happened; there was
     nothing worth writing down. The material is not re-read.
   - **Unusable output** — nothing stored, watermark untouched: a model failure is not a quiet
     day, so the material is retried next pass.

`ReflectionWorker` (Api) is the clock: a cheap periodic check that runs a pass only once the
user has been idle for `ReflectionIdleMinutes`. Reflection happens *after* the conversation,
never during it, and never on a request path.

## How thoughts surface

Two existing seams, no new machinery:

- **The greeting** — the freshest open curiosity becomes an opener: *"Something I found myself
  wondering while you were away: how did the interview go?"*
- **The turn** — the context packet gains two labeled sections:
  - *"A thought you had while they were away (your own musing — private)"*: prose, hold
    loosely, never recite; it colors attention, like the relationship note.
  - *"Something you've been genuinely curious about"*: at most one question, raise it **only
    if it fits naturally**, otherwise let it go.

## Why it never becomes an interrogation

- **Voiced-on-offer.** Surfacing a curiosity (greeter or packet) marks it `Voiced`
  immediately — whether or not the model chose to raise it. Asked once is the whole budget;
  the same pattern as emotional follow-ups and commitment surfacing.
- **Cooldown.** `IReflectionStore.GetNextToVoiceAsync` returns nothing while any curiosity was
  voiced within `CuriosityCooldownHours`, so a greeting and the turns after it can't each ask.
- **Minting caps.** One pass keeps at most `ReflectionMaxCuriosities` (default 2), deduped
  against everything already held.

## Guardrails

- **Thoughts are not facts.** Musings live in their own diary, are rendered only under their
  "your own musing" label, and never enter semantic memory, extraction, or retrieval-as-fact.
- **Privacy is structural.** The do-not-remember exclusion is inside the store query; there is
  no code path from a private turn to a musing. (Covered by tests.)
- **Musings expire.** A musing stops shaping turns after a week — the companion doesn't carry
  one stale thought forever.

## Surface area

- `POST /reflect` — run a pass now (demo/debug).
- `GET /reflections` — the diary, newest first (quiet days omitted).
- `GET /curiosities` — what it currently wonders about.
- Options (`Companion` section): `EnableReflection`, `ReflectionIdleMinutes`,
  `ReflectionCheckMinutes`, `ReflectionMinNewMessages`, `ReflectionMaxMessages`,
  `ReflectionMaxCuriosities`, `CuriosityCooldownHours`.

Offline (mock provider) the reflector's output never parses, so nothing is stored and the
worker's idle spacing keeps retries rare — the feature simply stays dormant until a real model
is configured.
