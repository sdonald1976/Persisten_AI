# Ava's world — a separate Godot application

The world is **not part of this solution**. It is its own application, in its own repo, with its own
authority over space and time. The user is a participant in it. Ava is a participant whose decisions
come from the brain in *this* repo, over a connection.

This is the project's existing "split the brain from the face" principle taken one step further. A
web page is a face with a text box; the world is a face with a body, a position, and weather.

```
                    AvaWorld.Server  (Godot 4 --headless, always running)
                    ───────────────────────────────────────────────────
                    geometry · collision · navmesh · pathfinding
                    bodies · positions · objects · day/night · weather
                              ▲                        ▲
                     ENet     │                        │  WebSocket
                              ▼                        ▼
        AvaWorld.Client                          Persisten_AI  (this repo)
        ───────────────                          ────────────────────────
        renders the world                        identity · persona · memory
        your keyboard and mouse                  reflection · curiosity · dreams
        connects and disconnects                 the decision to go somewhere
```

**The world is a server, not a window.** It keeps running when nothing is watching it — that is the
whole point of her being somewhere. The Godot client is a viewer that attaches to a world already in
progress, and closing it changes nothing except who is present.

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

## Headless first, or not at all

"Always running" is a requirement that decays quietly if it isn't designed for. Godot makes the
wrong thing easy: simulation logic drifts into `_process` on visual nodes, and one day the world only
advances while someone is looking at it.

So the same discipline this repo already applies to the brain applies inside the world app:

- **Simulation** is plain C# with no dependency on rendering. It runs under `--headless` and is
  testable without a display.
- **Presentation** is the scene tree — meshes, cameras, animation. It renders state it is given and
  decides nothing.

The check is the same shape as the boundary rule: the server must produce identical world history
with no client ever connected. If closing the viewer changes what happened, presentation has
absorbed logic that belongs to simulation.

This is also what makes the world portable. A headless, network-addressable server moves to another
machine as a deployment change rather than a rewrite.

## Two channels

The server accepts two different kinds of participant, and they want different transports.

- **Rendering clients** — your Godot client, over Godot's built-in multiplayer (`ENetMultiplayerPeer`).
  It gets state replication, interpolation, and the engine's own synchronisation for free.
- **The brain** — over plain WebSocket. The companion renders nothing and needs no state
  replication; it needs events in and intentions out. Keeping it off the game-networking stack means
  the companion never links a Godot assembly, and the protocol stays readable and loggable.

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

## Running it

Four processes: the world server, a world client, the companion, and Ollama. Development is on one
machine; the target is a dedicated system, which the headless split already accommodates — the
server's address is configuration, not architecture.

While developing on the workstation, the current roster is ~10.7 GB resident (Stheno 4.58 + Qwen 7B
4.36 + Qwen 3B 1.8) before Godot renders a frame, so expect either model eviction mid-sentence or
dropped frames on a 12–16 GB card. Both mitigations are cheap and temporary: keep the world stylised,
or use the two-model roster in [`MODELS.md`](MODELS.md) while the client is open. Neither should
shape the design, since the deployment target is different hardware.

**The model lease is still worth building, for latency rather than memory.** `ReflectionWorker`'s
idle check is a check at tick time, not a hold: if it passes and the user speaks a second later, a
reflection pass and a live turn issue model calls concurrently against the same server. More VRAM
keeps models resident and removes the swap, but two inferences still split throughput, so the user
waits on thinking they didn't ask for. A gate that background work acquires and a live turn preempts
fixes that on any hardware — it just stops being urgent enough to block the world on.

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

1. **A headless server that survives being ignored.** Before any geometry: a Godot project running
   under `--headless` with a world clock, saving and restoring state, logging that time passed. Run
   it overnight and confirm the morning looks like eight hours later. Doing this first is what makes
   "always running" true rather than aspirational — retrofitting it once logic lives in the scene
   tree is the expensive version.
2. **The world, empty.** Places, collision, navmesh. A Godot client that connects over ENet and lets
   you walk around a world the server already owns.
3. **A body for her.** NPC with navmesh pathfinding, driven by a hardcoded script. No brain yet.
4. **The wire.** WebSocket endpoint on the server plus a throwaway console client that sends `goto`.
   This proves the protocol without touching the companion, which is the point.
5. **The companion connects.** Outbound client and roaming policy. She now moves for reasons, and
   you can ask her why.
6. **Perception reaches her thoughts.** World events enter through the non-user input path and
   become material for reflection — the first point at which the world improves continuity rather
   than just existing.
7. **Voice and co-presence.** She speaks in-world with lip-sync; being in the same room colours how
   she talks.

## Open questions

- **What happens over a server restart?** Always-running removes the need to simulate gaps while
  nobody is home, but not the need to survive a reboot or a redeploy. Simplest honest answer: save
  periodically, resume from the save, and let her treat the gap as unremembered rather than
  inventing it. Fabricating a plausible eight hours is exactly the confabulation this whole design
  exists to prevent.
- **How fast does world time run?** Leaning 1:1, because every other temporal claim she makes is
  literal. A faster clock makes returning more eventful at the cost of her being able to say "this
  morning" and mean it.
- **Does she know it's a world?** She must never assert it as fact about the user's real life. But
  whether she treats it as lived experience or acknowledged imagination is a persona decision with
  direct consequences for the roleplay guard.
- **What does she do about you being away?** She can see you leave and return, which is presence
  information she has never had before. It could feed anticipation and greeting naturally — or
  become clingy. Worth deciding deliberately rather than discovering.
- **Where does this document live** once `AvaWorld` exists? Probably there, with a pointer here.
