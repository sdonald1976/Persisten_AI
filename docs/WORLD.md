# Ava's world — a separate Godot application

The world is **not part of this solution**. It is its own application, in its own repo, with its own
authority over space and time. The user is a participant in it. Ava is a participant whose decisions
come from the brain in *this* repo, over a connection.

This is the project's existing "split the brain from the face" principle taken one step further. A
web page is a face with a text box; the world is a face with a body, a position, and weather.

```
  AvaWorld  (new solution — Godot 4 + C#)          Persisten_AI  (this repo)
  ─────────────────────────────────────            ──────────────────────────
  geometry · collision · navmesh                   identity · persona
  bodies · positions · pathfinding      ◀───ws───  memory · retrieval
  objects · day/night · weather                    reflection · curiosity · dreams
  the user's presence                              the decision to go somewhere
```

## The boundary rule

> **The companion may hold a connection, but never a model.**

No `Place`, `Position`, or `Occupancy` entity. No tick. Nothing about the world persisted in
`companion.db`. On connect, the world **advertises what exists** — a menu of place names and
available actions — and the companion chooses from that menu. It does not remember the menu between
connections; it asks again.

The test for whether this has been violated: *delete the world application entirely, and the
companion's database should contain nothing that refers to it* — except conversation and reflection
text, which are just words she said and thought.

This matters because the drift is subtle and one-directional. Caching the place list "for
performance", storing her last position "so she resumes", adding a `WorldEvent` table "just for
reflection" — each is individually reasonable and collectively rebuilds the world model inside the
brain.

## Who owns what

| Concern | Owner |
|---|---|
| Geometry, collision, navmesh, pathfinding | **World** |
| Where any body is, and how it gets there | **World** |
| Objects and their state over time | **World** |
| Day/night, weather, ambience | **World** |
| The user's presence and position | **World** |
| Who she is, her mood, spirits, energy | **Companion** |
| Memory, retrieval, reflection, dreams | **Companion** |
| *Where she chooses to go, and why* | **Companion** |
| What she says | **Companion** |

The interesting row is the choice of destination. It belongs to the companion because it is a
function of her state — spirits, energy by hour, open curiosities, attention items — all of which
live here. But its *output* is only a place name selected from the menu the world advertised. The
companion decides **where and why**; the world decides **how to get there**.

## The wire

Godot runs a WebSocket server; the companion connects out to it as a client. Both sides already
speak WebSocket, and Godot 4 has `WebSocketPeer` built in.

**Perception** (world → companion) — the world *describes*; the companion never parses geometry.

```
{ "type": "arrived",   "place": "greenhouse", "notable": "the basil has wilted" }
{ "type": "presence",  "who": "user", "state": "joined", "place": "kitchen" }
{ "type": "ambient",   "text": "rain has started against the glass" }
```

**Intention** (companion → world) — goals, never motion.

```
{ "type": "goto",   "place": "greenhouse" }
{ "type": "look",   "at": "user" }
{ "type": "speak",  "text": "…" }
```

`{"type":"goto","place":"greenhouse"}` — never `move x+0.1`. Pathfinding is the engine's job and the
main reason to use one. If the companion ever sends coordinates, the boundary rule has already been
broken.

## What this repo actually needs

Three things, none of which is a world model:

1. **An outbound client**, behind an interface in `Companion.Infrastructure` — the same shape as the
   model, TTS, and transcription providers. Absent or unreachable is a normal state, not an error:
   she simply isn't in the world right now.

2. **A non-user experience input.** Every input path today is user-authored, and world perceptions
   entering as chat would be attributed to the user and reach the fact store. They need different
   provenance. The machinery already exists — `MemoryOwner.Companion` / `Shared`, and the roleplay
   guard that already rejects candidates mentioning her persona. Reuse it; do not add a world
   concept to the memory pipeline.

3. **A roaming policy** — a pure function from her existing state to a place name. No model call, no
   world dependency, testable without Godot running.

That is the whole footprint. Anything beyond it is drift.

## Local-only realities

Both applications and Ollama share one machine and one GPU.

**VRAM is the binding constraint.** The current roster is ~10.7 GB resident (Stheno 4.58 + Qwen 7B
4.36 + Qwen 3B 1.8), before Godot renders anything. On a 12–16 GB card, adding a real-time renderer
means either model eviction — a ~5 s swap landing mid-sentence — or dropped frames. Two mitigations,
both cheap: keep the world stylised and light (few dynamic lights, baked where possible), and use the
two-model roster in [`MODELS.md`](MODELS.md) while the world is running.

**The model lease is now a prerequisite, not a nicety.** `ReflectionWorker`'s idle check is a check
at tick time, not a hold: if it passes and the user speaks a second later, a reflection pass and a
live turn issue model calls concurrently against the same server. Today that is occasional. With a
renderer on the same GPU and a companion that is *always connected to somewhere*, it stops being
occasional. Build the lease — a gate background work acquires and a user turn preempts — before any
of this.

## Godot specifics

- **Requires the .NET/Mono build of Godot 4**, not the standard build. Not currently installed;
  nothing else is missing (.NET 8, 9 and 10 SDKs are present).
- Ava's body: `CharacterBody3D` + `NavigationAgent3D` over a baked `NavigationRegion3D`.
- **The avatar work is not wasted.** The `.glb`/VRM model from [`AVATAR.md`](AVATAR.md) loads in
  Godot too. The amplitude-driven lip-sync port cleanly: `AudioStreamPlayer` plus a spectrum analyzer
  driving the same blend shapes.
- A handful of hand-authored places beats generated terrain. What makes a world feel inhabited is
  consequence and persistence — the basil you saw wilting yesterday is dead today — not extent.

## Suggested order

Each step is worth having alone, and the first three don't touch this repo at all.

1. **The world, empty.** Godot scene, a few connected places, your own controller. Walk around it.
2. **A body for her.** NPC with navmesh pathfinding, driven by a hardcoded script. No brain yet.
3. **The wire.** WebSocket server in Godot plus a throwaway console client that sends `goto`. This
   proves the protocol without touching the companion, which is the point.
4. **The companion connects.** Outbound client and roaming policy. She now moves for reasons, and
   you can ask her why.
5. **Perception reaches her thoughts.** World events enter through the non-user input path and
   become material for reflection — the first point at which the world improves continuity rather
   than just existing.
6. **Voice and co-presence.** She speaks in-world with lip-sync; being in the same room colours how
   she talks.

## Open questions

- **Does the world exist when Godot is closed?** Local-only and Godot-hosted means no — and then
  "what were you doing this afternoon?" has no answer while the app was shut. If it should, the
  world needs either a headless mode or a deterministic clock it can replay forward on launch. That
  replay trick is cheap and worth building in from the start, but it belongs to the world app now.
- **Does the world persist across restarts?** Saved state, or a fresh morning each launch.
- **Does she know it's a world?** She must never assert it as fact about the user's real life. But
  whether she treats it as lived experience or acknowledged imagination is a persona decision with
  direct consequences for the roleplay guard.
- **Where does this document live** once `AvaWorld` exists? Probably there, with a pointer here.
