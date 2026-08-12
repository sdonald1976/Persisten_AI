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

`ReflectionWorker` (Api) is the clock: a cheap periodic check that, once the user has been idle
for `ReflectionIdleMinutes`, runs the full **sleep cycle** (`SleepCycle`): think first, then
tidy. After a pass that actually processed new material, memory **consolidation** runs (it used
to wait for an explicit command); every cycle also lets go of open curiosities older than two
weeks — a wondering that never found its moment stops being current. Reflection happens *after*
the conversation, never during it, and never on a request path.

## How thoughts surface

Three seams:

- **On demand** — "what's on your mind?" / "what are you thinking about?" / "penny for your
  thoughts" is a recognized intent (`ShareThoughts`), answered straight from the diary: the
  latest musings with their age ("While you were away (2 days ago), I found myself thinking…")
  plus one held curiosity. The answer is honest by construction — the thoughts really were had,
  timestamped, while the user was gone. Sharing a curiosity here spends it as usual, but
  deliberately bypasses the voicing cooldown: the user asked. An opinion question ("what do you
  think about my plan?") is *not* captured — that stays an ordinary turn.

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

## Trains of thought (threads)

Reflections used to be independent rows whose only relationship was a timestamp: each pass saw
her two most recent musings and otherwise started fresh. That made "what has she been working
through this week?" unanswerable, and let a thought she was developing three cycles ago vanish
the moment two newer ones existed.

Two changes give thinking continuity:

**Relevance-based priors.** A pass now sees her latest musing *plus* the most **relevant** older
ones — chosen by similarity between the new conversation material and each stored musing, over a
40-entry lookback. A dormant thread resurfaces when its subject comes back around, instead of
being buried by recency. With no embedding server available this falls back to the old
recency-only selection rather than failing.

**Thread identity.** Each musing carries a `ThreadId`, and optionally the `ContinuesReflectionId`
of the specific earlier thought it develops. The priors are shown to the model with short ids; it
answers `continuesThought` (an id, or null) and `thoughtSettled`. Deterministic code decides
whether to believe it: the claimed id must match a musing **actually offered in that pass**, so a
thought cannot be grafted onto an arbitrary or invented row. No match → a new thread, which is
also the pre-threading behavior.

**Anti-rumination.** When she marks a thread settled, the *whole thread* is excluded from future
prior selection — a resolved thought stops being resumed. Settling only counts on a musing that
continues something; a brand-new thought declaring itself finished is just a thought.

### Seeing it

- Dashboard → **💭 Her mind** shows trains of thought: the thought as it currently stands, with
  its earlier steps beneath it, a `settled` badge, and how many passes it has run for.
- `GET /reflections/threads` returns the same grouping (most recently developed first).
- `GET /reflections` still returns the flat diary.

Databases created before threading upgrade cleanly: existing musings have an empty `ThreadId` and
render as single-entry threads rather than collapsing together.
