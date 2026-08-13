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

## Presence, without the neediness

Seeing the user arrive and leave is information the companion has never had. It is also the fastest
route to something unpleasant, because a normal week looks like dozens of departures. The user is not
at their desk all day, and the world must not treat that as a series of events requiring comment.

Four rules, all mechanical rather than tonal — asking a model to "not be clingy" in a prompt is not a
control:

1. **Absence is the resting state, not an event.** The world is mostly unattended. Departure is a
   transition to normal, and nothing that returns to normal is notable.
2. **Presence is never an outreach trigger.** `OutreachService` has a deliberate priority ladder —
   a dated anticipation, then a passed anticipation, then the freshest open curiosity, and *no
   curiosity means no message*. Presence must not be added to that ladder. "You haven't visited in
   three days" is the exact failure being avoided, and it can only exist if something makes it
   possible.
3. **No accounting.** Visit frequency, session length, and time-since-last-seen are not tracked, not
   totalled, and not surfaced. The moment a number exists, some prompt will eventually cite it.
4. **One absence clock, not two.** The companion already answers "how long has it been?" gracefully
   through `TemporalNote` and `GetLastMessageAtAsync`, at conversational granularity. World presence
   must not create a second, finer-grained tracker beside it.

What presence *is* good for: colouring a reply when you are both in the same place, and knowing
whether to speak aloud or wait. That is the whole remit.

## Restarts: she doesn't know

A restart is invisible to her. The world resumes from its last save, and the downtime leaves a hole
in the event log that is never explained, apologised for, or filled.

**Nothing generates activity for a gap.** The temptation is real — a plausible eight hours is easy to
synthesise and makes her seem more alive. It is also precisely the confabulation this entire design
exists to prevent, and it would be undetectable once written, because it would look exactly like a
real event. If there are no world events for Tuesday afternoon, she has nothing to say about Tuesday
afternoon.

This also retires the deterministic-replay idea from the earlier draft. An always-running server
never needs to catch up, and a restarted one must not.

The world clock tracks wall-clock time and simply skips the downtime. The alternative — pausing world
time while the server is down — avoids the hole but drifts world time away from the user's real
time-of-day, and then "this morning" stops meaning the same thing to both of them. A shared,
literal sense of when it is matters more than an unbroken log.

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

## What is built

Steps one to six are done. The world is [a separate repo](https://github.com/sdonald1976/AvaWorld):
a headless Godot server that keeps running unwatched, five connected places, a client to walk around
in, Ava living there, and a WebSocket wire the companion drives her over.

In *this* repo, the whole footprint is three things, exactly as scoped above:

| Piece | Where |
|---|---|
| The outbound client | `Companion.Infrastructure/World/WebSocketWorldLink.cs` |
| The roaming policy | `Companion.Core/Services/RoamingPolicy.cs` |
| The clock that applies it | `Companion.Api/WorldWorker.cs` |
| Her own experiences | `Companion.Core/Domain/Experience.cs` + `ExperienceStore` |

Configure it with the `World` section in `appsettings.json`. An empty `Url` means no world, and
nothing about her changes.

### The boundary held

`IWorldLink.Places` is what the world last said, and it is emptied the moment the connection drops.
Nothing about the world reaches `companion.db` — no place table, no position, no cached menu. The
test still applies: delete the world application and this repo's database contains nothing that
refers to it.

`RoamingPolicy` is a pure function of her state and the menu she was just handed. It hard-codes no
room names — it reads the world's own words for each place — so a completely different world works
unchanged. And every choice carries the reason that produced it:

```
She's heading to the greenhouse — she has the energy for something.
She's heading to the greenhouse — she's been wondering about the tomatoes.
```

That reason is the point. A move she cannot explain is decoration, which the vision doc's non-goals
rule out; a move she *can* explain is continuity.

### Her day reaches her thoughts

World perceptions land in an **experience log** — timestamped sentences about what happened to her —
and reflection reads them alongside the conversation, under a heading that says whose they are.

The predicted problem did not appear. The design worried that `ResolveEvidence` would have to accept
world events or world material would silently produce nothing. It turned out the gate was already
right: a musing is not evidence-gated (it is her own thought, held loosely), while shared moments,
procedures and preferences require a real user message and therefore refuse anything sourced from
her day. Which is correct — her afternoon in the greenhouse is not a fact about the user, and the
existing gate says so without being touched. There is a test asserting a model that claims "we
repotted the basil together" is still refused when only her day happened.

One gate did have to change. Reflection required new *user messages*, so a day she spent in her world
without being spoken to produced no thought at all — precisely the gap the world was built to fill.
It now runs on enough new experiences alone, with the watermark advancing past both sources so a
quiet day is not re-read forever.

The experience log is not a world model: no places, no occupancy, no layout, nothing to keep in step
with anywhere. Just sentences, pruned after thirty days, with whatever reflection made of them
surviving as musings.

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

## Settled

- **World time runs 1:1 with wall-clock time.** Every other temporal claim she makes is literal, and
  "this morning" should mean the same morning to both of them.
- **Presence never becomes neediness** — see the four rules above.
- **Restarts are invisible and gaps are never filled.**

## Still open

- **Does she know it's a world?** She must never assert it as fact about the user's real life, but
  whether she treats it as lived experience or as acknowledged imagination is a persona decision, and
  it changes what the roleplay guard has to allow. This one can wait until she is actually in there
  and the right answer is obvious from hearing her talk.
- **Where this document lives** once `AvaWorld` exists — probably there, with a pointer here.
