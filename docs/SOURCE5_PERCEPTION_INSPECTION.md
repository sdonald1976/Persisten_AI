# Source 5 — inspection: world, vision, embodiment

Read-only audit, 2026-08-25. No contributor was built, nothing was changed.
Reported separately per source, because two of the three do not exist as
subsystems and a combined verdict would hide that.

**Verdicts up front.**

| source | subsystem exists | typed state | reaches prompt/plan | verdict |
|---|---|---|---|---|
| world | **yes** | yes, transient | **no** | **STOP — no honest register mapping** |
| vision | adapter only | none | no | **STOP — nothing to integrate** |
| embodiment | **no** | none | no | **STOP — the source does not exist** |

A note on authority shape before the detail: unlike Source 4, these three
**do** hold item grants in the registry — `observation` at `background_only`,
`observation` at `may_express` with planner promotion (world and vision), and
`state` at `background_only` (world and embodiment). So they are not
votes-only sources; they can place content in a plan. That raises the bar on
what counts as a real typed signal, and none of the three currently clears it.

---

# Source 5a — World

**Verdict: STOP.** Not for want of a subsystem — this one is real, careful, and
better designed than most of what Source 4 inspected. It stops because *no
honest register or item mapping exists*, and inventing one would be exactly the
move the whole V3 design exists to prevent.

**1. Typed state vs prose.** Genuinely typed records:
`WorldPlace(Id, Name, Description)`, `WorldConcern(ThingId, PlaceId, Name,
Condition, Text)`, `WorldPerception(At, Kind, Body, Place, Text)`, plus
`Connected`, `Configured`, `CurrentPlace`.

But every one of them carries a `Text` or `Description` field that is **the
world's own prose**, written by an external system. `WorldConcern.Text` is
explicitly documented as "the world's own words for it". `Kind` is an open set
in "the source's own vocabulary" — `arrived`, `presence`, `refusal` today, and
nothing constrains tomorrow's.

**2. Ownership.** The **world's** — neither Ava's nor the user's. The design
states its central rule outright: *the companion may hold a connection, but
never a model.* There is no place table, no occupancy, no layout.

**3. Lifetime.** `Places`, `Concerns` and `CurrentPlace` are **transient and
connection-scoped**, empty when disconnected — deliberately, because "a
remembered menu is the first step toward keeping a model of somewhere else."
Unplug the world and the database contains nothing referring to it.

The one durable residue is `Experience` rows written from `WorldPerception`:
timestamped sentences about her own life, pruned at 30 days by `SleepCycle`.
Privacy stakes are low here — an `Experience` is about Ava, not the user, and
the type documents that nothing in it reaches the fact store.

**4. Identity / provenance / confidence / expiry / correction.**
- Identity: `PlaceId` / `ThingId` are stable within a connection, but they are
  the *world's* identifiers. Nothing on Ava's side resolves them, and after a
  disconnect they resolve to nothing at all.
- Provenance: `Experience.Source = "world"` and a timestamp. No event id, no
  link back to the perception that caused it.
- Confidence: **none.**
- Expiry / validity: none on the perception; the 30-day `Experience` prune is
  the only lifecycle.
- Correction: **none.**

**5. Production use.** World state **never reaches the prompt or the
ResponsePlan.** `ContextPacket` has no world, place, or perception field at all
— verified. Its two real effects are: driving `RoamingPolicy` (where she goes),
and writing `Experience` rows that `Reflector` may read into an inner monologue.
So today the world affects *what she does*, and reaches speech only through
model-generated reflection prose — never as typed state.

**6. Isolation.** `IWorldLink` is a process-wide **singleton**, and `WorldWorker`
attributes every perception to the ambient `IUserContext.UserId`, which is a
`FixedUserContext` for the single-user deployment. **There is no multi-user
isolation here at all** — a second user would silently inherit the first user's
world. This is fine for the current single-user deployment and a real blocker
for anything else; it is stated now rather than discovered later.

**7. Why it stops.** Ask the honest question — *which register dimension does a
place imply?* — and there is no answer. "She is in the greenhouse" says nothing
about warmth, bluntness, verbosity, playfulness or intensity. Neither does
`Connected`. Neither does a concern: the design deliberately keeps concerns
*above* the policy seam ("a need is not a preference"), and the recorded reason
is a real incident — feeding concerns in as ordinary preoccupations let a stove
go cold while she read in the study.

The available *item* path is worse rather than better: an observation item's
content would be `WorldPerception.Text`, i.e. an external system's prose
entering the plan. That is possible to do safely — Source 2 does exactly this
with tool results, quoted as data — but it needs the same apparatus tools got:
typed capture at the boundary, an evidence id, disclosure and retention, and a
planner disposition. None of that exists for world today.

**Smallest missing layer, if this is ever wanted:** a typed
`WorldObservation` captured at the perception boundary with a durable
`EvidenceEventId`, a closed `Kind` set (or an explicit "unknown kind
contributes nothing" rule), and a planner disposition — the Source 2 shape,
applied to perception. That is a producer layer, not a contributor, and it
should not be built until there is a concrete use for it. **Voting nothing is
the correct current state, not a gap.**

---

# Source 5b — Vision

**Verdict: STOP. There is no vision subsystem to integrate — only an unused
model adapter.**

**1. Typed state vs prose.** The entire surface is:

```csharp
Task<string> DescribeAsync(string prompt, IReadOnlyList<ImageInput> images, ct);
```

Input is bytes plus a media type. Output is **a string of model-generated
prose**. There is no domain type, no observation record, no structured result —
nothing typed anywhere in it.

**2. Ownership.** Would be Ava's perception, if it ran.

**3. Lifetime.** No persistence of any kind. Nothing is stored.

**4. Identity / provenance / confidence / expiry / correction.** **None of the
six.** There is no record to carry them.

**5. Production use. None.** `IVisionModel` is registered only when a vision
endpoint is configured, and **nothing in the codebase calls `DescribeAsync`** —
verified across `src/` and `tests/`. There is no image ingestion path: no
endpoint accepts an image, no turn constructs an `ImageInput`. The capability
is advertised (`/capabilities` reports the configured vision model) but is
unreachable.

**6. Isolation.** Not applicable — nothing runs.

**7. Why it stops.** A vision contributor would have exactly one possible
input: a prose paragraph a model wrote about a picture. Turning that into
register votes or plan items means parsing model prose into structure, which is
the banned move, stated in the builder's own comment. And it would be a
contributor with no producer — the same shape as an `EvidenceRef` field with
nothing to reference, which the Source 3 stop already established as
unacceptable.

**Smallest missing layer:** an image ingestion path first (an endpoint, a
message attachment, *something* that produces `ImageInput`), then a typed
`VisualObservation` with an evidence id and a confidence, and only then a
contributor. That is a feature, not an integration, and it is out of scope here.

---

# Source 5c — Embodiment

**Verdict: STOP. The source does not exist.**

Every occurrence of "embodiment" in the entire source tree:

- `Contributors.cs` — the `Cap("embodiment", …)` registry entry and its two
  grant descriptions;
- `V3ShadowEnvelope.cs` — the string `"embodiment"` in a list of perception
  source names used to count contributions.

That is all of it. There is no domain type, no interface, no store, no service,
no producer, no consumer, no configuration, and no test. The capability entry
describes authority for a subsystem that was never built.

Nothing can be inspected against the six questions, because there is nothing to
inspect: no typed state, no owner, no lifetime, no identity, no production use,
no isolation story.

**Recommendation:** leave the capability entry in place — it is harmless, it
costs nothing, and pre-registering authority for a planned organ is the pattern
the contribution boundary was designed around. But **it should not be mistaken
for a subsystem awaiting integration.** If embodiment is ever wanted, it starts
with deciding what signal it produces, not with writing a contributor against
an empty registry entry.

---

# Cross-cutting

**No prose parsing, and none proposed.** All three sources' only substantial
content is prose — the world's, a vision model's, or nothing. Every proposal
above sources from a typed boundary that would have to be *built*, never from
parsing the prose that exists.

**No regex inference proposed anywhere.**

**The registry is already correct and already stricter than the producers.**
World, vision and embodiment hold item grants capped at `background_only`, with
`may_express` reachable only through a recorded planner promotion, and none of
them holds restriction authority or `MayProposeRegisterRestrictions`. So even
if a contributor existed, it could not create a restriction, a mandatory claim,
consent, a preference, or epistemic authority. §5.4 does not rank any of these
three families at all, which means their votes would fall to `int.MaxValue`
precedence — last, behind everything. That is the right default and worth
knowing before anyone adds one.

**Isolation is the one live risk.** The world link is a process-wide singleton
bound to a single ambient user. Nothing else in this inspection is exposed to
multi-user concerns because nothing else runs.

# Recommendation

1. **World** — no contributor. Its typed state is real but has no honest
   register mapping, and its item path needs a Source-2-shaped producer layer
   that should wait for a concrete use.
2. **Vision** — no contributor. Build an image ingestion path and a typed
   observation first; a contributor over model prose would be the banned move.
3. **Embodiment** — no contributor. There is nothing there.

Source 5 therefore has **no implementable phase today**. That is a finding, not
a delay: three sources were audited, and the honest result is that the ones with
authority entries do not yet have producers worth connecting them to.

Nothing in this inspection changed any code. V2, Run-1c, routing and displayed
output are untouched.
