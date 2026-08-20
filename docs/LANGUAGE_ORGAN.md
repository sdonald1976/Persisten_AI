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

## The typing line, and the heuristic ledger (2026-08-20 audit)

**Strings represent language. Types represent cognition.** `ConversationMove`,
`ResolutionConfidence`, and `TurnIntent` are enums; the kebab labels every diagnostic,
capture row, and dashboard reads ("answers-open-question", "clarify") are derived
mechanically in one place (`CognitionLabels.ToKebab`) and serialize identically to
before — the typing changed the inside, not the boundary.

The regex/lexical heuristics, classified rather than purged:

1. **Durable deterministic invariants/guards** — earned by a production failure, cheap,
   and not expected to be replaced: `UnresolvedReferentGuard`'s three patterns, the
   reaction-token exclusion in binding, list-line/ordinal parsing, sentence-start logic,
   the secret detector, whole-word focal matching.
2. **Provisional natural-language heuristics under measurement** — each has a capture
   trail and is presumed wrong until the corpus says otherwise: correction markers,
   directive shape, offering/lead-in cues, first-person-share, interjections,
   progress-question, the entity capitalization heuristic, pronoun preference order.
3. **Lexical growth that points at a future generalized mechanism** — four hand-kept
   word lists now do shallow lexical semantics in three files (WorkingContext's
   stopwords + function words, RelevanceSignals' scaffolding, the classifier's cue
   sets). Each is individually defensible; together they are the same shape ToolNudge
   was. When the captured corpora are large enough, the honest successor is a small
   local model through the existing `ICognitiveModel` seam, adopted by measurement —
   the SPECIALIST_MODELS discipline, not a bigger word list.

## Preserved specimens

### The Epcot pizza turn (2026-08-20, normal use, chat model qwen-family)

The user's turn, verbatim: *"Most of the food I don't eat. I just try to get wasted"* —
after establishing that Epcot is their favorite park **because** they drink around the
world. Ava's reply: *"…Though I'm curious: do you ever sneak in a bite of pizza or spicy
snack to balance things out…"* — reintroducing the user's stored pizza/spicy preference
into a turn whose entire content was "I don't eat the food."

Kept because it separates three properties this architecture must stop conflating:

- **Truth** — the pizza/spicy memory is VALID. The user does like it. Nothing in the
  store is wrong, and no store-side fix applies.
- **Topical similarity** — the memory is linguistically ABOUT food, and the message is
  about food. Every similarity signal (embedding, keyword) legitimately fires. The
  relevance floor did its job as designed.
- **Turn usefulness** — the memory is USELESS here, and worse: the message *negates* its
  topic ("don't eat"), so surfacing it invites exactly the follow-up the user just
  declined. Usefulness is a third axis neither of the other two measures.

Status of the trace: the exchange ran on the OTHER machine; this box's store holds no
pizza memory and the packet-level record is unrecoverable — the diagnostics ring keeps
five turns, in memory, per process. Which names the observability gap precisely: **tool
calls and model calls get durable telemetry rows; turns do not.** The smallest fix is a
durable, pruned per-turn record (traceId, message preview, retrieved content + score +
topical, decisions) in the DiagnosticsRecords pattern — an observability change, not a
retrieval change. Until it exists, `CognitiveModels:Capture` provides partial durable
coverage (the turn.intent rows carry move, top topical, and the message).

Deliberately NOT patched in Phase 2: the fix direction (a negation-aware usefulness
signal, or packet-side downranking of memories whose topic the message negates) is a
retrieval-consumer decision that needs its own evidence pass. Focal coverage does not
catch this case and was not expected to — the memory IS focal-relevant; it is useful-less.

## Relationship to the vision doc

`PERSISTENT_COMPANION_VISION.md` bans "abstract cognitive subsystems." This plan was
checked against that line deliberately rather than drifted past: everything here is
derived state with provenance and an inspectable decision trail — the vision doc's own
standard for what earns a place. The vision doc carries a matching amendment dated
today. "Build continuity, not consciousness" still rules: no emotion simulation, no
speculative inner life, nothing that cannot say where it came from.

## Status

- **2026-08-20** — inspection complete, plan approved, this document recorded.
- **2026-08-20 — Phase 0 landed.** Every turn now carries a `TraceId` shared between the
  in-process `TurnTrace` and the diagnostics ring, and a `Decisions` list recording each
  system-level verdict (privacy, roleplay, derived-memory, project, curiosity, register,
  packet budget, tools, reply gate, extraction) with decider and reason. Retrieval enters
  the ring structured (content/score/source) beside the prose summaries. The soak
  harness's `Turn` record now reads trace id, sections, and decisions per turn; the
  synthetic evaluator's HTTP client no longer parses fields that don't exist. The
  identity API distinguishes stored overrides from configured defaults, and an empty
  string on `PUT /identity` explicitly clears a field (blanking it was a silent no-op).
  No behavior changed; 1007 tests green.
- **2026-08-20 — Phase 1, answer-binding slice landed.** `AnswerBindingDetector`: when her
  previous message ends with a question and the user's reply is a short elliptical fragment
  (≤6 words, no question of its own), the system binds reply to question. Three effects:
  an authoritative `## Reading this turn` section in the packet quoting both halves,
  rendered right after the transcript and never reinterpretable by personality; the
  retrieval query becomes question + answer instead of a near-anchorless fragment; and an
  `interpretation` decision (bound/unbound, with the question as reason) on every turn.
  Whenever a turn follows a hanging question, the rule's verdict is captured under shadow
  subject `context.binding` — the ToolNudge discipline: its real-world hit rate gets
  measured, not assumed. New soak scenario `context` reproduces the Additive failure
  verbatim and faults on the decision record, not the reply prose. The word-count bound
  exists because the first cut bound "Never mind that — I finally got the irrigation pump
  running", which is a topic change, not an answer. 1015 tests green.
- **2026-08-20 — Phase 1 complete: working context.** `WorkingContext` reads the recent
  transcript into explicit per-turn state — open questions she asked that no user turn
  addressed, current topic, salient entities (speaker-tagged), reference markers, a move
  classification (answers-open-question / resolves-reference / correction /
  continues-thread / new-topic), and the resolved retrieval query. Three confidence bars:
  detect (classify only), resolve-by-guess ("her" → most recent user-introduced entity;
  query rewritten, nothing asserted), resolve-exactly (enumerated item, the user's own
  prior message; query rewritten AND the packet told). Ephemeral by design — traced on the
  ring, stored nowhere. Captures: `context.binding`, `context.reference`; rewritten-query
  turns also trace what the raw message would have retrieved.
  **Live run against qwen3:8b** (soak `context`, scratch DB, this GPU): all system-side
  checks clean — binding fired on the Additive-shaped turn, "the second one" resolved
  against the model's own bulleted list, open questions tracked. One real flaw surfaced
  and was fixed before proceeding: "her" first resolved to "Will Precious" (an auxiliary
  verb plus a name lifted from her own reply) — entity extraction now sheds leading
  function words and pronouns prefer user-introduced entities; the regression is pinned
  verbatim. Re-validated live: "her" → Beth. Also observed, deliberately not fixed here:
  extraction stored "a small dinner for someone named her" — the resolved referent is not
  yet fed to the memory pipeline (recorded as a Phase-2-adjacent candidate); and the
  first live turn's reply contained a Chinese token, a qwen3 quirk the filters don't
  touch. 1029 tests green.
- **2026-08-20 — Phase 1 boundary closed: resolutions reach extraction; guesses cannot.**
  `ReferenceResolution` flows from working context into the memory pipeline. Exact and
  unambiguous resolutions are CONSUMED: the extractor is told ("in the user's message,
  'her' refers to 'Beth'; evidence must still quote the user's original words"), and the
  stored fact carries dual provenance — verified live, "The user is making dinner for
  Beth." cites both "I'm making dinner for her." and "My sister Beth is visiting on
  Saturday." Guesses are the opposite of consumable — a warning: `UnresolvedReferentGuard`
  rejects pronoun-as-person facts ("someone named her" — live specimen #1), dangling
  object pronouns ("knitting a scarf for her." — live specimen #2, quieter garbage caught
  by this validation), and, on ambiguous turns, candidates naming a person the user did
  not name (live specimen #3, the worst one: the chat model's reply GUESSED a name for an
  ambiguous pronoun and the extractor laundered it into a fact cited against the user's
  own pronoun sentence). All three specimens are pinned as regression tests verbatim.
  Final live run: unambiguous → `consumed-unambiguous`, dual-evidence fact; ambiguous
  (two cousins, "pie for her") → `withheld-guess`, candidate rejected, store clean.
  Anticipation/project capture still read surface text — recorded as follow-up, not
  wired this pass. 1047 tests green.
- **2026-08-20 — Phase 2 landed, in shadow.** `TurnIntentClassifier`: what Ava should DO
  this turn — a closed vocabulary of nine acts (answer-question, acknowledge,
  respond-to-answer, clarify, continue-topic, accept-correction, follow-topic-change,
  admit-unknown, unknown), deterministic over working context + retrieval, no model call.
  Selection needs a 0.6 bar; below it the verdict is "unknown" = continue naturally,
  preferred over a confident mistake. Recorded as `TurnIntentState` (with competing
  candidates) on the ring, as a decision, and captured under shadow subject `turn.intent`
  — and deliberately absent from the generation packet: intent names acts, never prose,
  and it earns authority only from the shadow data. **Live run against qwen3:8b, 9 turns:
  7 correct**, including clarify on "what should I cook for her?" with two sisters in the
  window (the model, uninstructed, answered without asking — the exact case authoritative
  intent would improve). The two misses are the run's product: (1) "Ask me one short
  question about my garden" classified follow-topic-change — imperatives/requests are a
  vocabulary gap, recorded, not patched; (2) the carburetor progress question missed
  admit-unknown because two irrelevant memories (the dog, at score 1.60) cleared the
  relevance floor — `retrieved==0` is a broken proxy for "nothing relevant"; needs the
  topical signal, decided on capture data, not tuned blind. The shadow also caught a
  Phase-1 flaw, fixed and pinned: "lol" after her question bound as an ANSWER — the
  binding detector now refuses bare reactions (laughter/sighs) while keeping polar
  answers (yeah/no/sure). Promotion into generation context is NOT done and awaits the
  shadow verdict. 1069 tests green.
- **2026-08-20 — Phase 2 evidence pass (no promotion).** Three additions, all
  evidence-plumbing: `RetrievalResult.Topical` exposes the raw relevance the floor already
  computed (sim + kw + project), traced per memory and stamped into every `turn.intent`
  capture; `request-directive` exists as an evidence-only intent candidate capped at 0.55
  — visible in every competing-candidates list, never selectable; and the canonical
  clarify specimen is a permanent soak stage (faults if the system misses clarify, notes
  whether the model asked which). An 18-turn live run against qwen3:8b found:
  **directives are a real vocabulary gap** (6/6 classified follow-topic-change with
  request-directive competing at 0.55 every time — a perfectly consistent signature, and
  the model performed the requested act in every case); **max raw topical does NOT
  separate known from unknown** (carburetor known 1.95 and dog 1.41 vs treehouse unknown
  1.49 and fence 1.43 — interrogative scaffolding contaminates the overlap; the promising
  candidate is focal-entity containment — does any retrieved memory contain the
  question's subject noun — which separates all four cases here and needs corpus
  validation before anyone thresholds on it); **the canonical clarify case reproduced**
  (system clarify 0.75, model hedged plural instead of asking — and when the user
  volunteered "the taller one", the model INVENTED "Clara has always been the adventurous
  type"; the store stayed clean); **the model beat the system on the unknowns** (it
  honestly admitted no treehouse/fence data while the classifier said answer-question) —
  admit-unknown never fires with the broken retrieved==0 proxy. Recommendation recorded:
  one more shadow iteration (promote request-directive to selectable, build the
  focal-entity relevance signal, teach the ordinal resolver prose enumerations) before
  any controlled promotion. 1078 tests green.
- **2026-08-20 — Phase 2 iteration 2 (still shadow).** The three recommendations landed
  and validated live: `request-directive` is selectable (0.7 — above the topic-shape
  readings, below answer-question so "can you…?" stays an answer) and fired correctly on
  live directives, model complying each time; focal coverage ships as an observed-only
  feature (`FocalCoverage` on the ring, `|focal=` in captures) and **separated known from
  unknown on live data where topical could not** — carburetor covered / treehouse
  uncovered with retrieval returning records for both; the enumeration parser handles
  prose alternatives (or-boundary primary, commas split only into short parallel nouns —
  the descriptive-comma bug is pinned) plus emoji-decorated trailing questions, and
  resolved "the second one" against qwen's flowing two-option reply live. The canonical
  clarify specimen reproduced a THIRD time (system clarify 0.75; model hedged plural,
  never asked which). New finding for the corpus: "Ask me a question." after her own
  trailing question BINDS as an elliptical answer and respond-to-answer (0.85) outranks
  request-directive (0.70) — a genuine precedence collision ("ask Beth" can truly answer
  "who should I invite?"), recorded, not reflex-patched. The Epcot specimen is preserved
  verbatim in `SPECIMENS.md` with the truth/topical/usefulness analysis and the named
  observability gap (turns lack the durable telemetry tool/model calls already have).
  1083 tests green.
