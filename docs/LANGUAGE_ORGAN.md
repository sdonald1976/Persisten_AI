# The language organ: making Ava progressively independent of her chat model

_Recorded 2026-08-20, from a full-codebase inspection performed before any implementation._

The premise, stated once so every later decision can be checked against it:

> **Ava is the system. The language model is Ava's language organ.** It understands and
> expresses natural language over information supplied by the surrounding cognitive
> systems. It is not the authoritative source of her identity, memory, knowledge,
> personality, goals, or decisions — and it must not silently substitute pretrained
> assumptions for her actual state. Long-term, the chat model should be replaceable
> without Ava becoming a different entity.

This is an evolution of the existing architecture, not a replacement. The inspection
below maps the proposal onto what already exists; most of it found more to keep than to
build.

## Why now: three failures from live testing (2026-08-19)

1. **"Additive."** Ava asked "What's your favorite kind of magic?" and the user answered
   "Additive." She reinterpreted it as being about the relationship instead of the
   question she had just asked. The question *was* in her prompt — recent conversation
   is first-class in the packet and guaranteed budget space. The failure is that
   interpretation, memory selection, and styled expression all happen in one chat-model
   pass, with the persona rendered above everything else, and nothing in the system
   records that she had an open question.
2. **Personality bending truth.** With the seductive preset active, legitimate but
   irrelevant memories (pizza, Disney, Shipwreck Adventure) were bent into the theme.
   Retrieval embeds the raw current message — a one-word reply is a nearly anchorless
   query — so weak matches reached the packet, and the persona wove them in.
3. **Identity.** The companion's gender/pronouns persist correctly (committed code,
   proven by `SettingGender_PersistsAndSetsPronouns_AndReachesThePrompt`). The real
   residue is smaller: `GET /identity` returns the coalesced value, so stored and
   default are indistinguishable, and a field can never be cleared from the dashboard.

## What the inspection found

### Already system-owned (keep, extend, do not rebuild)

- **Identity** — persisted on `UserProfile`, resolved at one point
  (`PersonalityService.Identity`), rendered into a never-trimmable
  `# AUTHORITATIVE IDENTITIES` block. Authoritative today.
- **Disposition** — spirits stored with decay and an append-only `EmotionalSignal`
  evidence log; energy a pure function of the clock. Derived, provenanced, no
  randomness. The reference implementation for "state the model reads but cannot own."
- **Curiosity** — a persisted entity with provenance (`ReflectionId`), dedupe, a cap,
  a cooldown, and an ask-once budget enforced in code (`MarkVoicedAsync` fires on
  injection). Drives outreach and roaming. What it lacks is *sources* (only reflection
  over biography mints candidates) and *scoring* (priority-order today), not lifecycle.
- **Tool selection** — the chat model has never seen the tool list. `ToolNudge`
  (deterministic) then a separate 3B planner over a persona-free planning context
  decide every call. Already the shape the proposal asks for.
- **Autobiographical memory** — evidence-gated, provenanced, supersession-aware,
  closed predicate vocabulary. Untouched by this work except as a pattern to reuse.
- **The measurement culture** — `Shadow.CompareAsync`/`CaptureAsync` are
  general-purpose (any subject string), `/diagnostics/shadow*` and `harvest.py` give a
  review queue for free, and Tier-0/soak/synthetic evaluation can assert on system
  state. Every new authority in this plan goes through shadow before enforcement.

### The real gaps

1. **No working conversational context.** Recent turns are a flat 6-message text dump
   in the system prompt (the API call is `[system, user]` — no role-structured
   history). No unresolved-question tracking (only project clarifications), no topic
   or entity state, no reference resolution. Retrieval and project resolution embed
   the raw current message. The most load-bearing silent delegation in the pipeline.
2. **No concept knowledge, and no epistemic boundary.** There is no concept store, no
   knowledge graph, no importer — the architecture doc's `Entity`/`Relationship`
   records were never built. Everything Ava "knows" about the world is the chat
   model's weights, unlabeled. `ContextProvenance` grades trust within stored content
   but has no value for "the model's own background knowledge." Nothing can say
   `AvaKnowledge("axe") = unknown`.
3. **No turn-level intent.** What kind of turn this is (answer the question / admit no
   visibility / ask back / follow up on her own question) is inferred by the chat
   model from a static rules block. The only pre-generation decisions in code are
   project ambiguity and tool lookup.
4. **Uncertainty is thresholds, not propositions.** Confidence numbers are everywhere;
   "I don't know whether P" is representable nowhere. `UncertaintyNotes` is unparsed
   prose, last-ranked, first-dropped.
5. **Interpretation and expression are one pass**, so personality de facto owns
   interpretation despite touching neither retrieval nor planning. One true authority
   leak: `PromptIdentityProjector` regex-mines persona text into
   `# AUTHORITATIVE RELATIONSHIP` facts — personality literally becoming truth.
6. **Observability stops at the API boundary.** The rich in-process `TurnTrace`
   (per-signal retrieval scores, exclusions, full packet) is discarded; the
   `/diagnostics/turns` ring keeps flattened strings; there is no trace id; gates,
   filters, and nudges are not recorded as decisions.
7. **Goals are captured, never pursued** — no ranking, no progress. Real, but not on
   this plan's critical path; deferred deliberately.

### Assumptions the code corrected

- Recent conversation does **not** rely on memory search — it is separately sourced
  and guaranteed in-budget. The Additive failure is an authority problem, not a
  context-presence problem.
- Curiosity and tool use are already systems, not prompt instructions. "Be curious"
  appears nowhere as a standing instruction; the standing instruction is restraint.
- There is **no** existing knowledge-import work in this repository. That build starts
  from zero (reusing the memory machinery's provenance/evidence/revision patterns).
- The gender/pronoun persistence bug was the *user's* DisplayName (fixed in
  `3d02c02`); companion identity persistence was never broken in committed code.

## The cautionary tale that shapes the whole plan

`ToolNudge` scores F1 **0.778** on the sentences its author imagined and **0.087** on
real utterances (`SPECIALIST_MODELS.md`). Every interpretation or intent heuristic this
plan adds is presumed wrong in the same way until capture/shadow data says otherwise.
Mechanisms ship in shadow, decisions are recorded per turn, and authority is promoted
only on evidence. Measurement ships before the mechanism — the discipline this
repository already paid to learn.

## The phases

Order chosen so each phase makes the next one measurable. Nothing is a rewrite; every
phase lands inside the existing pipeline.

- **Phase 0 — Decision observability.** A `TraceId` per turn; a `Decisions` list
  (stage, decider, verdict, confidence, reason) on `TurnDiagnostics`, populated from
  decisions the turn already makes (privacy, in-character, register, project
  resolution, nudges, planner rounds, gate verdicts); structured retrieval entries
  instead of flattened strings; the soak client reads all of it; fix the synthetic
  eval client's phantom trace fields; fix `GET /identity` stored-vs-default and field
  clearing. Zero behavior change.
- **Phase 1 — Working context and interpretation authority.** A deterministic
  per-turn working context: the assistant's last open question, current thread,
  recent entities. Two consumers: retrieval query rewriting (an elliptical reply is
  searched as question + answer, not as a bare word), and an explicit interpretation
  line in the packet ("'Additive' answers your question about kinds of magic"),
  recorded as a decision. Deterministic first; capture disagreements; a model only if
  the captured data earns it.
- **Phase 2 — Turn intent, shadow first.** A small closed intent enum decided by
  rules over working context + retrieval, recorded per turn without being enforced;
  promoted into the packet once shadow agreement is read.
- **Phase 3 — Concept knowledge.** A third `MemoryKind` implementing `IMemory` (the
  seam retrieval/vector/assembly are already generic over), with its own predicate
  vocabulary and guard — the existing ones are person-attribute-only and correctly
  reject world facts. Taught-concept path first ("I know X because you told me"), a
  "does Ava know X" lookup, and a `ContextProvenance` value for model-background
  knowledge so the epistemic boundary exists as a labeled thing.
- **Phase 4 — Uncertainty as state, curiosity sources.** `KnowledgeGap` records
  (unknown concept, conflicting memories, ambiguous reference) reusing the Curiosity
  lifecycle; gaps become scored curiosity candidates feeding the existing budget and
  cooldown. Widens what she says unprompted, so it sits **after** the standing
  content-gate decision, not before.
- **Phase 5 — Personality to expression.** Remove the persona→relationship regex
  mining; measure persona repositioned below the rules; two-pass generation (grounded
  draft → styled expression) only if the soak demands it — a second chat pass doubles
  voice latency on a 6 GB GPU.
- **Phase 6 — Model-swap evidence.** Same rendered packet through multiple chat
  models, diffing fact/intent survival. The invariance proof that Ava survives
  replacement of the language organ; meaningful once phases 1–2 give the packet
  explicit interpretation and intent to preserve.
- **Phase 7 — Book learning.** The only true greenfield build, last, feeding the
  Phase-3 store: text → concepts/propositions with confidence and provenance, so "I
  know X because I learned it from Y" is a database answer, not a style.

### Explicitly not doing

- Rebuilding curiosity or tool selection — extend the existing systems.
- Suppressing the model's linguistic competence — the boundary is epistemic labeling,
  never making the model not-know English.
- An LLM interpretation stage as the opening move, or two-pass generation before
  evidence demands it — latency is a real budget on this hardware.
- Touching the reply-gate default — enforcement remains an open decision recorded in
  `HANDOFF.md`, and this plan does not preempt it.

## Relationship to the vision doc

`PERSISTENT_COMPANION_VISION.md` bans "abstract cognitive subsystems." This plan was
checked against that line deliberately rather than drifted past: everything here is
derived state with provenance and an inspectable decision trail — the vision doc's own
standard for what earns a place. The vision doc carries a matching amendment dated
today. "Build continuity, not consciousness" still rules: no emotion simulation, no
speculative inner life, nothing that cannot say where it came from.

## Status

- **2026-08-20** — inspection complete, plan approved, this document recorded.
  Phase 0 begun. No behavior changes yet.
