# ResponsePlan v3 — audit of the existing system (2026-08-24)

Read-only inspection on branch `responseplan-v3`. Nothing here changes run-1c,
its serializer, its checks, or live routing. Line references are to master at
`ce422a6`.

## 1. The complete current path

```
conversation turn
  → Companion.HandleAsync (Companion.cs)
      recent = last RecentMessageCount(6)+1 messages            [context window]
      privacy classifier → sensitive flag                        [gates recording+extraction]
      working context (WorkingContextState): move, interpretation, reference markers
      retrieval → RetrievalResult list (memories, concepts, shared)
      concept lookup (knowledge boundary)                        [ConceptLookupResult]
      turn intent (TurnIntentState)                              [act]
      packet assembly (ContextPacket) — the PROMPT for production
  → ResponsePlanner.Build (ResponsePlanner.cs:18)                [THE PLAN — shadow]
  → (PromoteResponsePlan only) one prompt line injected into packet.InterpretationNote
  → tool loop (may add ToolResults to packet)
  → production reply: _replyGenerator.GenerateAsync(packet.Render(), …) → Stheno
  → EchoedTurnFilter.Strip
  → CANARY (RendererShadowService.RenderForDisplayAsync):
      PlanSerialization.CompactV2(plan) → BuildUserPrompt → renderer-shadow (Ollama)
      RendererShadowChecks.Score on BOTH replies; critical-failure fallback
      displayed reply = renderer's (clean) or production's
  → reply gate (shadow) → PlanFidelity checks (recorded) → renderer shadow queue (non-canary)
  → StoreMessageAsync — ONLY the displayed reply enters history
  → extraction / project updates / reflection — all downstream of the displayed reply
```

Key structural fact: **the plan is a shadow consolidation of decisions the
packet already made, not the packet's source.** Production speaks from
`packet.Render()`; the renderer speaks from `CompactV2(plan)`. Two parallel
descriptions of the turn, converging only because both derive from the same
upstream state. V3's central migration question is whether the plan becomes
the single authoritative middle.

## 2. ResponsePlan field inventory (Domain/ResponsePlan.cs)

| field | type | producer | consumers |
|---|---|---|---|
| TraceId | Guid | Companion | trace correlation, shadow rows |
| Act | TurnIntent (enum, 10 values) | TurnIntentClassifier via intent state | CompactV2 CONTROL `act =`; decision records |
| Acknowledgments | list of Acknowledgment(Kind, ErrorOwner, Text) | working-context move + TeachingDetector | CompactV2 SITUATION (templated English); PlanFidelity correction/contrition checks; PromoteResponsePlan trigger |
| Content | list of PlannedContent(Kind, Requirement, Text, Provenance?) | interpretation note; retrieval loop (≤8, clipped 200 chars); concept definition | CompactV2 SITUATION (MustState) + PALETTE (MayUse) + CONSTRAINTS (MustNotContradict); plan-echo check; palette-leak proxy |
| Epistemic | list of EpistemicNote(Kind, Subject) | ConceptLookupResult familiarity | CompactV2 CONSTRAINTS; PlanFidelity.CheckEpistemic; admission proxy |
| Question | PlannedQuestion(Kind, Text, Mandatory)? | intent==Clarify (mandatory) or curiosity (optional) | CompactV2 CONTROL `question =`; question-discipline checks |
| Tone | ToneGuidance(Register?, MoodNote?, PersonaStyle?) | packet RegisterNote/MoodNote + persona | CompactV2 STYLE (free prose) |

Enums: ContentRequirement {MustState, MayUse, MustNotContradict};
ContentKind {Interpretation, Memory, SharedMemory, LearnedKnowledge};
AckKind {CorrectionAccepted, AgreementConfirmed, FactTaught, AnswerReceived};
ErrorOwner {Companion, User, Nobody}; EpistemicKind {NotLearned, Uncertain,
Disputed}; QuestionKind {Clarify, Curiosity}. All serialize kebab-case via
KebabEnumConverter — the typed-with-stable-wire-names discipline v3 extends.

## 3. CompactV2 semantics (PlanSerialization.cs — FROZEN, hash in all three freeze manifests)

- `[plan/2]` header; sections CONTROL / SITUATION / PALETTE / CONSTRAINTS / STYLE.
- CONTROL: `act = <kebab>`; `question = <kind>:<mandatory|optional>` or `none`.
  Non-speakable by system prompt + deterministic check.
- SITUATION: acknowledgments rendered as **templated third-person English with
  hard-coded names** ("Ava made an error; Scott corrected her: …"), then every
  MustState content text verbatim, then the question's meaning when mandatory.
- PALETTE: MayUse texts. CONSTRAINTS: MustNotContradict + epistemic lines
  ("Ava has NOT learned what "X" is — say so; never explain it…"). STYLE:
  the three tone strings joined.
- CRLF line endings (Windows AppendLine); byte-exactly mirrored by
  `train_run1a.build_user_prompt` (LF only before final `Ava's reply:`).
- SystemPromptV2 hard-codes the Ava/Scott frame and the section rules.

## 4. Deterministic fidelity checks — four bodies, one family

1. **RendererChecks.Check** (frozen, linked into bench/DatasetGen/Infrastructure):
   plan-echo (first 40 chars of a MustState text verbatim), control vocabulary,
   "the user" narration, scenario required/requiredAny/forbidden token lists,
   + PlanFidelity battery.
2. **PlanFidelity** (Core, ResponsePlanner.cs:145): correction ownership
   ("we both"), invented contrition on agreement, shared-history claims,
   epistemic honesty. Run per turn on PRODUCTION replies since Phase 5.
3. **curate.py py_gates** (frozen with the corpus): same contract in Python +
   question-mode discipline + invented-experience regex + sludge statistics.
4. **RendererShadowChecks** (Infrastructure, real-turn proxies): question
   discipline from the plan, palette-leak (distinctive-token), MustState
   omission proxy, invented-experience port, epistemic-admission phrase list.

Scenario token lists (required/forbidden/requiredAny) exist ONLY in datasets —
real turns rely on the proxies. V3's item IDs + typed values are what make
more of the real-turn checks deterministic (see spec §7).

## 5. Dataset / training / evaluation representations

- Scenario JSONL: {id, family, stratum, source, transcript, userMessage,
  plan{act, acknowledgments, content, epistemic, question, tone}, required,
  forbidden, requiredAny} — the plan block deserializes into the SAME Core
  types (DatasetGen references Companion.Core).
- `plan2` strings are precomputed per row (plan2-current.jsonl / unseen-plan2.jsonl)
  and hashed by freeze manifests; training prompts rebuild byte-identically.
- Prompt builders: C# `BuildUserPrompt` and Python `build_user_prompt` (CRLF
  mirror) — two implementations, one contract, drift-checked by contract tests.

## 6. Shadow/canary integration

Subject `renderer.plan2` rows carry Legacy/Model/Applied + JSON envelope
(plan hash, adapter sha, model version, latency, VRAM, question mode,
palette/muststate flags, both violation lists, both sludge lists, user
message, full plan2 text). Version-agnostic by construction: the envelope
already names the serializer's product, so a `plan3` subject can ride the
identical machinery. Forget-path sweeps renderer rows by all three texts.

## 7. Tool-turn behavior

Tool turns are EXCLUDED from canary and shadow (`toolOutcome.Calls.Count == 0`
gate) because the corpus never covered tool results; ToolResults exist only in
the packet, invisible to the plan. V3 names tool results as an envelope block
so this exclusion can eventually end (with new training coverage — that is a
new semantic, spec §4.2).

## 8. Privacy and provenance today

- Provenance: `PlannedContent.Provenance` is a free string ("working-context",
  "taught", "shared-history", or a memory-status word). No enum, no subsystem
  identity, no confidence, no timestamps.
- Privacy: the turn-level `sensitive` flag gates recording and extraction;
  no per-item sensitivity exists — a plan cannot say "use this, never quote it".

## 9. Mixed factual content + coaching (the echo-bait inventory)

Proven by the politeness provenance trace: SITUATION prose written as
second-person coaching gets echoed by weaker renderers, verbatim.
- `working.InterpretationNote` → MustState Interpretation items: authored/
  derived texts like "…left you in arguing form. Own it honestly." — fact
  ("register was sharper; cause: tile debate") fused with instruction
  ("own it honestly").
- Corpus SITUATION lines: "You can hold an aesthetic opinion about the
  PRACTICE from everything you've absorbed" (nix-media-08) — echoed
  capitalization and all by run-1a/1b.
- Ack templates themselves are meta-prose ("Ava accepts it as her own
  mistake.") — behavioral instruction living inside a "fact" line.
V3's hard rule: items carry facts; policies carry behavior; nothing
model-facing may fuse them in one prose string (spec §2.4).

## 10. Hard-coded special cases that should become generic policy

| special case | where | v3 generalization |
|---|---|---|
| "Scott"/"Ava" proper names | SystemPromptV2, CompactV2 templates, BuildUserPrompt speaker tags, checks' vocative counting | participants block in envelope; templates take names as data |
| Ack → English templates | CompactV2 switch | acks become typed items (semantic_type correction/agreement/teaching/answer) with structured owner; serializer renders from type, not per-kind prose |
| PromoteResponsePlan single prompt-line injection | Companion.cs:593 | subsumed: correction item with must_express + owner=self is the authority; no packet splicing |
| question kinds Clarify/Curiosity, mandatory tied to kind | planner | question policy (ask_required / may_ask / question_forbidden) independent of kind |
| Tone = 3 free strings | ToneGuidance | typed register vector (spec §5) |
| model name "renderer-shadow" | RendererShadowService | config (already per-endpoint; name joins options) |
| holdout primitive detection by serialized phrases | unseen/select_combination.py | detect from typed plan JSON, not prose markers |
| Clip(200 chars) silent truncation | planner | explicit token budget policy with drop order + diagnostics (spec §8.6) |

## 11. What already supports v3 / where the proposal conflicts

**Supports:** typed records + kebab wire names; the MustState/MayUse/
MustNotContradict ladder is expression policy in embryo; provenance slot
exists; scenario JSONL already round-trips typed plans; check battery mostly
plan-derived; shadow/canary is serializer-version-agnostic; the density-map
law gives the curriculum design its physics; freeze/hash discipline transfers
unchanged.

**Conflicts (the real work):**
1. The plan is not authoritative — packet and plan are parallel derivations;
   v3 as "single middle" is a cognition-side migration, phased (spec §9).
2. SITUATION fuses facts and coaching — requires planner-side content
   separation, not just a new serializer.
3. No extension mechanism anywhere: unknown fields are impossible today
   (fixed records), so "must not break deserialization" is a new contract.
4. No item identity: checks match text substrings; stable IDs change how
   omission/leak checks are written and how supersession is expressed.
5. Register: three free-text strings collapse personality; independent
   dimensions do not exist upstream either — persona/mood must learn to emit
   them (ownership matrix, spec §6).
6. The renderer itself was trained on plan/2 prose: any v3 wire format needs
   a NEW training run (run-2 family); v3 therefore ships as protocol + tooling
   first, model later — run-1c keeps consuming byte-identical CompactV2
   through its whole tenure (spec §8).
