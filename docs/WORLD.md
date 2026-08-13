# A world for the companion: place, roaming, and dreams

> **⚠ SUPERSEDED — being rewritten. Do not build from this yet.**
>
> This draft assumed the world would be state *inside* this solution. It should not be. The world
> is a **separate application** — a 3D environment that both the user and the companion connect to
> as participants — and this repo stays the brain, with no notion of place in it at all.
>
> What survives the change: dreams as a walk over the memory-association graph (that lives here and
> is unaffected), the model-lease prerequisite, and the rule that world events must never enter the
> memory store. What does not: every section below that puts places, occupancy, or a tick in this
> codebase.

Everything the companion knows is indexed by **time**. `TemporalNote`, `RelativeTime`,
`TimePrecision`, `EnergyAt(hour)`, the reflection watermark, anticipation dates, memory validity —
the temporal axis is modelled richly and used everywhere. The **spatial axis does not exist at all**:
there is no place, room, scene, or location anywhere in the domain, and nothing in the three.js
avatar reaches the backend.

This document designs that missing axis, and the two things it makes possible: **roaming** (she is
somewhere, and moves for reasons) and **dreaming** (associative recombination while she rests).

> Guiding principle, inherited from the project: **build continuity, not consciousness.** The
> vision doc's non-goals rule out "emotion simulators", "cognitive subsystems", and "speculative
> multi-subsystem architecture", and require every component to *earn its place by directly serving
> the companion experience*. A world clears that bar as a **continuity instrument** and fails it as
> a simulation for its own sake. Every section below is written against that test.

## Why a world serves continuity

Today her entire inner life is derived from the user's conversation. Reflection reads messages;
musings are about the user; curiosities are questions for the user. That is a closed loop with one
source, and it has an observable failure mode: asked what she'd been doing when she had no context,
she produced *"we were in the middle of discussing your coding project"* — fluent and invented.

A world gives the reflection loop **material that isn't the user**. "What have you been up to?"
stops being a prompt for confabulation and becomes a question with a true, checkable answer. That is
the whole justification. A world that doesn't feed the loop is scenery.

## Decisions taken

| Question | Decision |
|---|---|
| What is the world for? | **A place you're both in** — the user has a location too, can move, and can meet her somewhere. |
| Does world time run while away? | **Yes, continuously.** She has genuinely been somewhere doing something. |
| What drives roaming? | **Her state, by deterministic rules** — mood, energy, curiosities. No model call to decide where she goes. |

### The two that combine unusually well

"Continuous" and "deterministic" sound like they trade off — continuous implies always-on cost. They
don't, and the reason shapes the whole build:

**If the tick is a pure function of `(state, elapsed time)`, you never have to run it on a clock.**
Store `LastTickedAt`; when anyone asks about the world, replay every tick from then to now. Eight
hours of world time is a few thousand integer steps — microseconds, no GPU, no background thread
competing with anything. The world is *continuous in semantics* and *lazy in execution*.

This is the single most important constraint to preserve. The moment a tick needs a model call, the
world stops being replayable, has to run on a real clock, and starts competing with chat for the one
GPU. **Keep the tick pure.** The language model narrates the world; it never decides it.

## Architecture: the world is headless

The repo's existing split is brain (`IAgent`) / faces (web, avatar, CLI). The world takes the same
shape:

```
FACES:    web chat  ·  three.js scene  ·  voice
                    │  local HTTP + WebSocket
BRAIN:    IAgent ──── reads world state, never owns it
WORLD:    IWorld  ──  places · occupancy · objects · events · clock
```

The three.js scene is a **view of the world, not the world**. If world state lives in the browser,
nothing else can reason about it — reflection can't read it, memory can't cite it, and it dies on
refresh. Everything below assumes `IWorld` is server-side and persisted.

### State

- **Place** — a node with a name, a description, and connections. A graph, not coordinates. Ten
  hand-authored places beat a generated terrain: what makes a world feel real is *consequence*, not
  size.
- **Occupancy** — where she is, where the user is. Both first-class, because presence is shared.
- **Object** — a thing in a place with state that changes on a schedule (the basil needs water; the
  fire burns down). This is what turns a map into a world you can have news about.
- **WorldEvent** — an append-only log of what happened, where, and when. *This is the load-bearing
  table*: it is what reflection reads and what she can truthfully cite.
- **Clock** — `LastTickedAt`, plus the world-time-to-wall-time ratio.

### The tick

Pure, deterministic, no I/O beyond the store:

1. Advance object state by elapsed time.
2. Decide whether she moves, and where.
3. Append the resulting `WorldEvent`s.

Movement is chosen from state she already has. Nothing new needs inventing:

| Existing signal | Where it lives | How it reads spatially |
|---|---|---|
| Energy by hour | `CompanionStateTracker.EnergyAt` (23–05 → 0.25) | Low energy → somewhere restful; high → somewhere active |
| Spirits | `UserProfile.CompanionSpirits`, decaying | Low → a quiet place; buoyant → the garden |
| Open curiosities | `Curiosity` table | A curiosity about your project sends her to the workshop |
| Attention items | `AttentionItem`, TTL'd | What's on her mind picks the room |
| Anticipations | `Anticipation`, dated | Your interview tomorrow → she's somewhere she can think about it |

The payoff of deterministic rules isn't only cost. It makes location an **expression of inner
state** rather than a random walk, which means "why are you in the workshop?" has a real answer, and
the answer is auditable in exactly the way the rest of this codebase insists on.

## Where the world meets the thoughts loop

`Reflector` already reads new messages, prior musings, held curiosities, open loops, and the recent
emotional read. **World events since the watermark become one more material source** — a small
change to `ComposeMaterial`, not a new subsystem. The reflection thread machinery, the quiet-day
watermark, and the skip reasons all work unchanged.

### The load-bearing change: evidence

`Reflector.ResolveEvidence` is the gate that makes *"reflection cannot invent history"* true — a
cited phrase must actually appear in a real message (exact substring, else ≥ 0.5 token overlap) or
the derived item is dropped. Shared moments, procedures, and associations all depend on it.

World events must become a **first-class evidence source** in that same resolver. If they aren't,
one of two failures follows:

- she can't cite what she did, so world material silently produces nothing; or
- the gate is loosened to let it through, and the property that stops her inventing history is gone
  for *everything*, not just the world.

Extending the resolver to match against `WorldEvent` text keeps the invariant and widens what counts
as history. This is the piece to get right first, and the one to test hardest.

### The world does not go in the memory store

Worth stating plainly, because it looks like it should. `MemoryPipeline`'s second roleplay layer
rejects any candidate whose content trips `lexicon.MentionsCompanion` — reason: *"references the
companion's persona — in-character, not biography"*. World events are inherently about her, so the
existing guard would reject them, **and it would be right to**. The fact store is a model of the
user's real life; her afternoon in the garden is not a fact about the user.

So: `WorldEvent` is its own store with its own retention. The one exception already has a home —
when the user is *present* for something, that's a **shared moment**, which reflection already
mints as `Owner = Shared`, `Confidence = 0.7`, `Actor = "reflection"`, evidence-verified. Shared
presence in the world should flow down that existing path and no other.

## Dreams

A dream must be a different kind of object from a musing, or it is just a musing with worse
epistemics.

| | Musing | Dream |
|---|---|---|
| Material | Real conversation + world events | Existing memories, loosely associated |
| Evidence-gated | Yes — `ResolveEvidence` | No — it isn't claiming anything |
| Can become memory | Shared moments only, verified | **Never**, by construction |
| Presented as | "a thought, hold loosely" | "a dream — explicitly not a claim" |
| When | Reflection pass, on idle | Low-energy hours |

### How dreaming earns its place

Not as atmosphere. `IMemoryAssociationStore` already exists, and reflection already mints links
between memories. **A dream is a walk over the association graph**: pick two or three memories that
are weakly or not-yet connected, recombine them, and let the pass propose new associations.

That is real retrieval work — surfacing an unexpected link between two old memories is exactly the
"say something oblique and be understood" capability the vision doc is built around. The dream is
the *expressive surface* of an associative pass that would be worth running anyway. It clears the
non-goals bar because the mechanism is memory work, not consciousness theater.

The hook already exists: `CompanionStateTracker` models energy by hour (23–05 → 0.25) and a `Rested`
flag set by a reflection within 12 hours. Dreaming belongs at low energy, and `Rested` is already
its natural output.

## Prerequisite: nothing currently yields to a live turn

`ReflectionWorker` checks idleness at tick time — `if (now - lastSeen < idle) return;` — but that is
**a check, not a hold**. If the check passes at T and the user types at T+1s, the reflection pass and
the live turn issue model calls concurrently against the same Ollama server. There is no lease, no
busy flag, no priority, and no cancellation of in-flight background work. Today this is survivable
because reflection is occasional.

A continuously-available world plus a thoughts loop plus dreams makes it much less survivable. A warm
turn is already ~74s on the current roster with three resident models; a background pass landing
mid-turn competes for the same GPU and can force a model swap.

**Build the lease first.** A single gate that background work must acquire, that a user turn
preempts, and that cancels in-flight background generation. It is small, it is testable, and every
feature in this document makes its absence worse.

## Shared presence

This is the most demanding decision, because it turns the world into a second interactive surface
next to chat. Two guardrails:

**Conversation stays the UI.** The roadmap's principle — *"every action is something you can say"* —
applies here. "Come find me in the garden", "where are you?", "stay with me" should be intents
routed by `IIntentParser`, not buttons. The existing rule-based-then-LLM parser is the right seam.

**Low affordance on purpose.** Location and a handful of objects, no inventory, no crafting, no
goals. The moment it rewards *playing*, it stops serving continuity and starts competing with it.
Co-presence should mostly change how she talks — referencing where you both are, or noting that
you're not together — rather than giving you things to do.

On the wire, the roadmap already anticipates this: *"the WebSocket frame types are already the place
to add them."* World state and movement are new frame types alongside `token` / `reply` / `confirm`.

## Prompt cost

The context packet is already budget-warned (`PacketTokenWarningThreshold`), and world state is
exactly the kind of section that grows without anyone noticing. Budget it explicitly and keep it to
a line or two of the *current* situation — where she is, what's notable, what she was just doing.
The event log is material for reflection, not for every turn's prompt.

## Suggested order

Each step is useful alone and testable without the next.

0. **Model lease / preemption.** Prerequisite. Background work yields to a live turn.
1. **World state, tick, and replay.** No LLM at all — pure logic, fully unit-testable. Assert that
   replaying N ticks equals ticking N times.
2. **World events into reflection**, with `ResolveEvidence` extended to cite them. She can now
   truthfully answer "what have you been up to?".
3. **Shared presence** — user location, movement intents, WebSocket frames.
4. **Dreams** — associative pass at low energy, never promoted to fact.
5. **The 3D view** — a face over state that already exists and already works headless.

## Open questions

- **World time ratio.** Does an hour away equal an hour there? A faster clock makes returning
  eventful; a 1:1 clock makes it honest. Leaning 1:1, because every other temporal claim she makes
  is literal.
- **Does she know it isn't real?** She must never assert the world as physical fact about the user's
  life, but whether she treats it as her lived experience or as acknowledged imagination is a
  persona decision with real consequences for the roleplay guard.
- **Retention.** `WorldEvent` grows forever at one row per tick. Needs the same treatment
  diagnostics got (`PruneAsync`, 30 days) — or aggressive summarisation into "yesterday, mostly the
  garden".
- **Multiple faces, one world.** Two browser tabs now resume the same conversation; they'd also
  share one world position. Probably correct for a single-user companion, worth confirming.
