# Source 5 — inspection: world, vision, embodiment

Read-only audit, 2026-08-25. No contributor was built, nothing was changed.
Revised the same day after review: **these are observation sources, not register
sources.** The first draft asked which register dimension a place implies. That
was the wrong question and the answer it produced ("none, therefore stop") was
the wrong conclusion for the wrong reason. A perception source contributes
*typed observations* at `background_only`, with expression reachable only
through a recorded planner promotion. It has no business touching register at
all, so having no register mapping is the expected shape, not a defect.

Reported separately per source, because they are in three genuinely different
states.

| source | classification | verdict |
|---|---|---|
| world | observation source | **implementable — blocked on ownership** |
| vision | observation source | **blocked — no caller, no typed producer** |
| embodiment | — | **absent — not integration work at all** |

**Authority shape, for all three.** The registry gives them
`observation → background_only`, `observation → may_express` (promotable, world
and vision), and `state → background_only` (world and embodiment). So an
observation contributor could place a background fact in a plan, and could reach
expression **only** when a planner promotes it — which the assembler records.
None of them holds restriction authority or `MayProposeRegisterRestrictions`, so
none can create a restriction, a mandatory claim, consent, a preference, or
epistemic authority. None appears in the §5.4 register precedence list, which is
consistent with their not being register sources.

---

# Source 5a — World

**Verdict: implementable as typed `background_only` observations with
planner-controlled promotion — and blocked until ownership is fixed. No world
contribution may ship before that.**

## The read surfaces, as they actually are

`WebSocketWorldLink` exposes exactly five things, and the shape of each matters:

| surface | type | how it updates |
|---|---|---|
| `Places` | `volatile IReadOnlyList<WorldPlace>` | **replaced wholesale** on each world snapshot |
| `Concerns` | `volatile IReadOnlyList<WorldConcern>` | replaced wholesale, derived from `things[].needsAttention` |
| `CurrentPlace` | `string?` | set from the snapshot's `place` field |
| `Connected` | `bool` | socket open **and** `Places.Count > 0` |
| `Perceived` | `event Action<WorldPerception>` | raised per world message |

`WorldPlace(Id, Name, Description)` and
`WorldConcern(ThingId, PlaceId, Name, Condition, Text)` carry the world's own
identifiers — genuinely stable *within a connection* — alongside the world's own
prose in `Description` and `Text`. `WorldPerception(At, Kind, Body, Place, Text)`
is the only surface with a timestamp, and its `Kind` is an open set in "the
source's own vocabulary".

**Nothing carries a user.** Not one of these five surfaces mentions who the
observation belongs to. `WorldWorker` stamps `IUserContext.UserId` when it writes
an `Experience`, and that is the *only* place ownership is applied.

## The six questions

1. **Typed state vs prose.** Real typed records with world-assigned ids —
   better than most of what Source 4 inspected. Every one also carries a prose
   field written by an external system, which must be treated as quoted data if
   it ever travels, exactly as tool results are in Source 2.
2. **Ownership.** The *world's*. The design rule is stated in the interface:
   *the companion may hold a connection, but never a model.*
3. **Lifetime.** Transient and connection-scoped — `Places`, `Concerns` and
   `CurrentPlace` are emptied on disconnect deliberately, "because a remembered
   menu is the first step toward keeping a model of somewhere else." The only
   durable residue is `Experience` rows written from perceptions, pruned at 30
   days by `SleepCycle`.
4. **Identity / provenance / confidence / expiry / correction.** Identity is the
   world's and resolves to nothing after a disconnect. Provenance is
   `Source = "world"` plus a timestamp — no snapshot id, no link back to the
   perception that produced an `Experience`. **No confidence. No explicit
   validity or expiry** (the wholesale replacement is an implicit one). No
   correction path.
5. **Production use.** World state reaches neither the prompt nor the
   ResponsePlan — `ContextPacket` has no world, place or perception field,
   verified. Its two real effects are driving `RoamingPolicy`, and writing
   `Experience` rows that `Reflector` may read into an inner monologue. So today
   the world affects *what she does*, and reaches speech only via
   model-generated reflection prose.
6. **Isolation.** **This is the blocker.** `IWorldLink` is registered as a
   process-wide **singleton**; `WorldOptions` configures exactly one world URL;
   `WorldWorker` is a single hosted service attributing every perception to the
   ambient `IUserContext.UserId`, which is a `FixedUserContext` today. There is
   no per-user or per-conversation partition anywhere in the path. A second user
   would silently inherit the first user's world, and a world observation
   entering a plan would be a cross-user leak by construction.

## Blocker 1 — ownership, which must be solved first

No world contribution ships until an observation can name whose it is. The
smallest change that achieves it:

- **Bind a world to a subject.** Either a per-user link registry
  (`IWorldLinkRegistry.For(userId)`) or an explicit `WorldOptions.OwnerUserId`
  making the single-world deployment's ownership declared rather than ambient.
- **Remove `IUserContext` from `WorldWorker`.** Perceptions must carry their
  owner from the link that produced them, not from whoever the process happens
  to think is logged in.
- **Conversation scope is a separate decision.** A world observation is not
  obviously per-conversation — she is in one place regardless of which
  conversation is open — so `ConversationId` should be *nullable and recorded*,
  not invented. Where it is null, the observation is user-scoped and must be
  excluded from any conversation-scoped disclosure check rather than defaulted
  into one.

## The proposed typed snapshot

`WorldObservation` — captured at the link boundary, before anything is prose:

| field | purpose |
|---|---|
| `ObservationId` (Guid) | stable identity; what a contribution cites as `evidenceRef` |
| `UserId` | ownership — **required**, from the link, never ambient |
| `ConversationId` (Guid?) | recorded when one applies; never invented |
| `Subject` (typed ref) | `place:{id}` / `thing:{id}` / `body:{id}` — the world's id, namespaced so it cannot collide with anything of ours |
| `Kind` (closed set) | `location`, `arrival`, `presence`, `departure`. **An unrecognised kind contributes nothing** — the world's vocabulary is open, so ours must not be |
| `Value` (closed-set token) | the observation's typed value; never the world's prose |
| `Confidence` (double) | asserted-by-world = high; degraded as the snapshot ages |
| `ObservedAt` | from `WorldPerception.At`, or snapshot receipt time |
| `ValidUntil` | snapshot TTL. **On disconnect every observation expires immediately** — which is already the link's behaviour, made explicit rather than implicit |
| `Provenance` | world endpoint identity + snapshot sequence, so an observation resolves to the message that produced it |
| `Text` (optional) | the world's own prose, carried as **quoted data** only, never as authority |

**Contribution rules, if it is ever built:**

- `background_only` by default; expression only through a recorded planner
  promotion, exactly as the registry already permits and no further.
- **Expired, ambiguous, cross-user or low-confidence observations contribute
  nothing** — silence, not a hedged observation.
- Never a register vote. Never a mandatory claim. Never speech on its own
  authority.

## Blocker 2 — concerns stay above the policy seam

`WorldConcern` must **not** become an ordinary background observation, and the
proposed snapshot deliberately has no place for it. The reason is recorded in
`RoamingObservation`'s own documentation and it is a real incident: feeding
concerns in as ordinary preoccupations scored a stove going cold at 0.5 against
the study's 0.4 — a gap under the move threshold — so she sat and read while the
fire went out. *A need is not a preference.* Code decides that something needing
doing outranks where she would like to be; models judge only the latter.

If concerns are ever wanted in a plan, they need their own typed channel with
that precedence intact, not a demotion into the observation stream.

---

# Source 5b — Vision

**Verdict: blocked. No caller, no typed producer.**

The entire surface is
`Task<string> DescribeAsync(string prompt, IReadOnlyList<ImageInput> images, ct)`
— bytes in, **model-generated prose out**. No domain type, no record, no
persistence, no identity, provenance, confidence, timestamp, or expiry.

`IVisionModel` is registered only when a vision endpoint is configured, and
**nothing in `src/` or `tests/` calls `DescribeAsync`** — verified. No endpoint
accepts an image; nothing constructs an `ImageInput`. The capability is
advertised by `/capabilities` and is unreachable.

**`DescribeAsync` prose must not be parsed**, and no contributor is specified
here. What is specified is only the minimum future boundary, so that whoever
builds the producer knows the shape it has to land in:

`VisualObservation` — captured where an image is *ingested*, not where prose is
returned:

| field | purpose |
|---|---|
| `ObservationId` | stable identity |
| `UserId`, `ConversationId?` | ownership, from the ingestion path |
| `SourceImageRef` | identity of the image, so the observation resolves to it |
| `Kind` (closed set) | what sort of visual claim this is; unrecognised → nothing |
| `Value` (closed-set token) | the typed assertion; never the description text |
| `Confidence` | the model's, if it reports one; absent means the observation cannot be promoted |
| `ObservedAt`, `ValidUntil` | an image describes a moment, so validity is bounded |
| `Provenance` | model identity and version |
| `Text` | the description, quoted data only, never parsed into any of the above |

The ordering matters: **image ingestion first, then a typed producer, then a
contributor.** A contributor built before the producer would be an
`evidenceRef` with nothing to reference — the shape Source 3 already stopped on.

---

# Source 5c — Embodiment

**Verdict: absent. This is not blocked integration work; there is nothing to
integrate.**

Every occurrence of "embodiment" in the source tree:

- `Contributors.cs` — the `Cap("embodiment", …)` registry entry and its two
  grant descriptions;
- `V3ShadowEnvelope.cs` — the string `"embodiment"` in a list of perception
  source names used for counting.

No domain type, no interface, no store, no service, no producer, no consumer, no
configuration, no test.

**No contributor is defined here, and none should be.** Writing one against an
empty registry entry would be fiction: a component with no input, tested against
fabricated data, claiming an integration that does not exist.

**The capability stays dormant.** Leaving it registered is correct —
pre-declaring authority for a planned organ is the pattern the contribution
boundary was designed around, and a dormant entry grants nothing to nobody. It
should simply not be mistaken for a subsystem awaiting connection. If embodiment
is ever wanted, it starts with deciding what signal it produces.

---

# Recommendation

1. **World** — implementable, and blocked on ownership. Fix the process-wide
   ambient-user singleton first; then build `WorldObservation` at the link
   boundary; then a contributor at `background_only` with planner-controlled
   promotion. Concerns stay above the policy seam throughout.
2. **Vision** — blocked. Build image ingestion and a typed producer before any
   contributor. Do not parse `DescribeAsync` prose.
3. **Embodiment** — dormant. Define nothing.

Source 5 therefore has **no implementable phase today**, with world being a
genuine candidate the moment ownership is resolved.

Nothing in this inspection changed any code. V2, Run-1c, routing and displayed
output are untouched.
