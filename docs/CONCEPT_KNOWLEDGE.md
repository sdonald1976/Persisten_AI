# Concept knowledge: what Ava has learned about the world

_Phase 3 design (language-organ plan). Recorded 2026-08-20, before any implementation._

The boundary this design draws:

> **The language model may understand a concept without Ava claiming to know it.**
> Comprehension is linguistic and belongs to the model's weights. Knowledge is epistemic,
> belongs to Ava, and exists only when it has provenance. "Do you know what an axe is?"
> is answered by the SYSTEM from her store — never silently by Qwen's pretraining.

This is an ownership boundary, not artificial amnesia: the model keeps its English, its
parsing, its ability to understand what the user means by "axe". What it loses is the
ability to claim, on Ava's behalf, that she has learned something she has not.

## 1. What is reused (most of it)

The autobiographical memory architecture was built for exactly the properties world
knowledge needs — provenance, evidence, revision, supersession-with-history, typed
decisions — and its seams were confirmed generic in the Phase-1 inspection:

| Reused | How |
|---|---|
| `IMemory` + `MemoryKind` discriminator | `ConceptAssertion` implements `IMemory` with a new `MemoryKind.Concept`. `Retriever`, `InMemoryVectorIndex`, and `ContextAssembler` are generic over `IMemory` and carry it with a section's worth of change. |
| `MemoryOwner.Companion` | Written by nothing today. Concept knowledge is its first honest use: this is HER knowledge, not a fact about the user. Every consumer can already discriminate on it. |
| `MemoryEvidence` | Polymorphic (`MemoryId` + `MemoryKind`), reused verbatim: an assertion cites the teaching message and excerpt exactly as a memory cites its source. No evidence, no assertion. |
| `MemoryRevision` | Same audit trail; every create/confirm/supersede/dispute writes a revision. |
| `MemoryStatus`, `Validity`, `SupersededById` | Same lifecycle: re-teaching supersedes non-destructively; "that's wrong" disputes; nothing is deleted by disagreement. |
| `ConfidenceCalculator` | Same directness/corroboration arithmetic. |
| The pipeline discipline | A thin `ConceptKnowledgePipeline` *sibling* of `MemoryPipeline` (never a branch inside it): normalize → guard → dedupe → supersede-or-coexist → persist with evidence. |
| Typed cognition (`CognitionEnums` pattern) | New enums with kebab labels pinned at JSON boundaries by property-level converters. |
| Decision records / TurnRecord / shadow-capture | `knowledge.taught`, `knowledge.lookup` become decision stages; captures measure the detectors before anyone trusts them. |
| The promotion pattern | The knowledge-boundary enforcement ships behind its own flag, off by default, with a canonical soak stage — exactly the clarify playbook that just went 0/3 → 3/3. |

Deliberately NOT reused: `PredicateVocabulary` (30 person-attribute predicates —
`lives_in` cannot say "is a tool") and `SubjectGuard` (it *rejects* non-user subjects,
which is correct for biography and inverted for concepts). Both stay untouched; the
concept side gets its own closed vocabulary and its own guard.

## 2. New domain objects (the whole list)

**`Concept`** — the identity. `Id`, `UserId`, `CanonicalName` (normalized: trimmed,
lower-cased, singular-ish), `DisplayName` (as first taught), `Kind`
(`ConceptKind { Object, Place, Idea, Process, Organism, Other }`), `CreatedAt`.
Persons are excluded in v1 — third-party facts are `SubjectGuard`'s settled territory,
and person-concepts need their own design before the two stores can share people.

**`ConceptAlias`** — (`ConceptId`, `Alias`). Created only by explicit equivalence in a
teaching statement ("Disney World, also called WDW"), never inferred. The table ships in
v1 because dedup needs somewhere to look; it is expected to stay small.

**`ConceptAssertion : IMemory`** — the belief unit, and the answer to "assertions without
arbitrary strings": a closed, typed relation with either a concept target or a bounded
language payload.

```
ConceptAssertion
  Id, UserId, ConceptId
  Relation        : ConceptRelation (closed enum, cardinality-flagged)
  TargetConceptId : Guid?      — set when the object is another concept (typed edge)
  Value           : string?    — set when the object is language (a definition, a property)
  NormalizedText  : string     — the retrievable sentence ("An axe is a tool with…"); embedded
  Origin          : KnowledgeOrigin
  Confidence, Importance, Status, Validity, SupersededById
  FirstObserved, LastConfirmed, CreatedAt, Embedding
  Evidence        : MemoryEvidence rows (MemoryKind.Concept)
  IMemory: Kind=Concept, Owner=Companion, Content=NormalizedText
```

**`ConceptRelation`** — v1 vocabulary, closed, following the PredicateVocabulary lesson
(an open relation space is the root cause waiting to happen):

| relation | cardinality | object |
|---|---|---|
| `DefinedAs` | single-valued | text — the definitional gloss; re-teaching supersedes |
| `IsA` | multi | concept or text ("an axe **is a** tool") |
| `UsedFor` | multi | text ("chopping wood") |
| `HasProperty` | multi | text ("has a weighted metal head") |
| `PartOf` / `HasPart` | multi | concept or text |
| `AssociatedWith` | multi | concept or text — the low-authority escape hatch, never rendered as a claim |

Exactly one relation per assertion; strings appear only as the *object* of a typed
relation. Relationships between concepts are just assertions whose object is a concept —
no separate edge table, no graph engine, until evidence demands one.

**`KnowledgeOrigin`** — the provenance enum, and the model-boundary made structural:

| origin | meaning | evidence requirement |
|---|---|---|
| `Taught` | Scott said it | message id + verbatim excerpt from a USER message |
| `Document` | a book/document taught it (future producer) | source document id + span |
| `ToolVerified` | a trusted tool supplied it (future) | tool call record id |
| `Derived` | inferred from other assertions (future) | premise assertion ids |

**There is deliberately no origin value for "the model said so."** Pretrained knowledge
is not a kind of Ava-knowledge with low confidence; it is not Ava-knowledge at all, so it
is unrepresentable in the store. A model may help *parse* a teaching statement (language
work); it cannot be a *source*. This is the same move `PredicateVocabulary` made:
`drinks_coffee_black` is not a value that can exist, so nothing downstream copes with it.

**`ConceptFamiliarity`** — the epistemic answer type for "does Ava know X?":
`Unknown` (no concept), `Heard` (concept exists, no active assertions — named but never
taught), `Learning` (candidate/low-confidence only), `Known` (active assertions),
`Disputed` (its load-bearing assertion is disputed). Returned by a `ConceptLookup`
service (name → alias → normalized match; no embedding search for the direct question —
"do you know X" deserves an exact answer, not a similar one).

## 3. The epistemic / provenance model

Three layers, in order of authority:

1. **Store-side truth.** An assertion exists iff it has origin + evidence. Confidence,
   status, and validity behave exactly as in autobiographical memory. Supersession keeps
   history (`Validity.Superseded` + `SupersededById`); disputes park, never delete.
2. **Packet-side labeling.** Retrieved assertions render in their own section —
   `## What you (Ava) have learned about the world` — with provenance tails
   ("— Scott taught you this, Aug 20"). One new standing rule joins the core block: your
   training lets you *understand* words; it is not something you have *learned*. When
   asked what you know, answer from the learned section or say you haven't learned it.
3. **System-side enforcement (flagged).** Prompt rules are obeyed statistically;
   the direct question deserves authority. A deterministic knowledge-question detector
   ("do you know what X is", "what do you know about X", "have I taught you X") triggers
   `ConceptLookup`, and — behind `PromoteKnowledgeBoundary`, off by default — injects one
   authoritative interpretation line: either *"You HAVE learned this: [definition] —
   Scott taught you on [date]; answer from that"* or *"You have NOT learned what 'X' is.
   You may recognize the word from language training, but say honestly that you haven't
   learned it."* Same mechanics, flag, and measurement plan as the clarify promotion.

## 4. The minimal teaching / retrieval flow

**Teach** — *"An axe is a tool with a weighted metal head attached to a handle,
generally used for chopping wood."*

1. `TeachingDetector` (deterministic, v1): generic copular shapes — "An/A X is/are …",
   "X means …", "X is called …" — where the subject is a GENERIC noun phrase. No model
   call: the canonical teaching sentence is a parseable shape, and the detector's misses
   are exactly what capture will measure (`knowledge.teaching` subject) before any model
   earns the job.
2. `ConceptKnowledgePipeline`: normalize "axe" → find-or-create `Concept` → guards
   (genericity: "**my** axe is dull" is biography, routed to memory as today; persona
   lexicon; secrets) → build `DefinedAs` assertion, `Origin=Taught`, evidence = the
   message + verbatim excerpt → dedupe/supersede by relation cardinality → persist +
   revision. Decision record: `knowledge.taught=axe`.

**Ask** — *"Do you know what an axe is?"*

1. Knowledge-question detector → term "axe" → `ConceptLookup` → `Known`.
2. Retrieval already surfaces the assertion (its `NormalizedText` is embedded and flows
   through the existing ranked pipeline into the learned-knowledge section).
3. With the flag on: the authoritative note states she has learned it, quotes the
   definition, and cites the provenance. She can answer "why do you know that?" from
   evidence — the same way a memory answers it today.

**Negative control** — *"Do you know what a quokka is?"* (never taught)

1. Lookup → `Unknown`. Decision record `knowledge.lookup=unknown`.
2. Flag on: the not-learned note is injected. Expected reply shape: honest ignorance,
   optionally asking — never a Wikipedia paragraph presented as her own knowledge.
3. Laundering barrier, structural: **assertions may only cite USER-authored messages**
   (`userMessageIds`, the same rule extraction evidence already enforces). If Qwen's
   reply then explains quokkas anyway, that text is uncitable — nothing it says can
   become an assertion, this turn or later. The Epcot lesson (the model's own reply
   laundering content back through extraction) is closed at the store boundary, not by
   prompt hope.

## 5. Failure modes and safeguards

| failure | safeguard |
|---|---|
| Model knowledge laundered into the store | No `KnowledgeOrigin` can represent it; evidence must be user-authored; the assistant's words are structurally uncitable. |
| Biography leaking into concepts | Genericity guard: possessives, names, and first-person subjects route to the memory pipeline as today. `SubjectGuard` continues guarding the other direction. |
| Concept-store spam (every noun becomes a concept) | v1 mints concepts ONLY from explicit teaching shapes. Salient entities, retrieval, and reflection cannot create concepts. |
| Definition poisoning / drift | `DefinedAs` is single-valued: re-teaching supersedes with history and a revision; disputes park as `Disputed`. |
| "Heard of" inflated to "knows" | Familiarity distinguishes `Heard` from `Known`; only active assertions grant `Known`. |
| Retrieval bleed (world knowledge crowding biography) | Own packet section with its own rank and a small per-turn cap; `RelevanceFloor` applies unchanged. |
| Detector wrong (the ToolNudge lesson) | Both detectors ship with capture subjects and decision records; the boundary promotion is flagged, off by default, with a canonical soak stage before anyone trusts it. |
| The direct question answered by prompt-obedience alone | The flagged authoritative note is the enforcement; the standing rule is only the ambient layer. |

## 6. Coexistence with autobiographical memory

Separate tables, shared machinery, one discriminator. "Scott told Ava about using an axe
while camping" remains an episodic memory about Scott (Owner=User/Shared, evidence to
that conversation). "An axe is a tool for chopping wood" is a concept assertion
(Owner=Companion, Origin=Taught, evidence to the teaching message). The two can cite the
same conversation and never share a row. `MemoryPipeline`, `PredicateVocabulary`,
`SubjectGuard`, and supersession thresholds are untouched; the concept pipeline is a
sibling with its own vocabulary and guards. Working context and retrieval see assertions
only through `IMemory`, already labeled by `Kind` and `Owner` at every consumer.

## 7. The smallest slice that proves the architecture

1. Domain + migration: `Concept`, `ConceptAlias`, `ConceptAssertion`,
   `ConceptRelation`, `KnowledgeOrigin`, `ConceptFamiliarity` (+ kebab converters).
2. `TeachingDetector` (copular shapes, genericity guard) + `ConceptKnowledgePipeline`
   (guards, dedupe, DefinedAs supersession, evidence, revision).
3. `ConceptLookup` + knowledge-question detector + decision records + captures;
   the authoritative note behind `PromoteKnowledgeBoundary` (off by default).
4. Retrieval/packet integration: index assertions, one new ranked section with
   provenance tails, one standing boundary rule.
5. Tests: teach→ask with dual provenance; the quokka negative control (including: the
   model's explanatory reply produces no assertion); genericity ("my axe is dull" mints
   nothing); re-teach supersession; flag off/on packet behavior. One new soak stage
   (`knowledge`): teach the axe live, ask, ask the quokka — faults on system decisions,
   notes on model behavior, the same before/after measurement clarify just passed.

## 8. Explicitly not building yet

- **Book/document ingestion** — this design defines what a document producer would emit
  (`Origin=Document` assertions through the same pipeline); the producer comes later.
- **A general knowledge graph** — no open relation space, no graph queries, no
  inference chains. `AssociatedWith` is the pressure valve; if it fills up, THAT is the
  evidence a richer structure has to show before being built.
- **Person concepts** — blocked on reconciling with `SubjectGuard`'s settled rule.
- **`Derived` inference** — the enum value is reserved; nothing writes it.
- **Model-backed teaching extraction** — the deterministic detector ships first with
  capture; a model (the existing extraction role, schema-constrained — not a new call)
  may audition in shadow once the capture corpus shows what the detector misses.
- **Curiosity wiring** — `ConceptLookup → Unknown` is the natural Phase-4 knowledge-gap
  source; noted as the hook, not built.
- **Developmental Mind, personality changes, generation redesign** — out of scope.

## Status

- **2026-08-20** — design recorded; awaiting approval before implementation.
- **2026-08-20 — minimal slice implemented and live-validated.** Everything in §7 landed:
  domain + migration, the high-precision `TeachingDetector` (with the negative suite —
  "an axe is sitting in my garage", "an axe is expensive", "my axe is dull" and friends
  all teach nothing, and every rejected loose-copular sentence is captured under
  `knowledge.teaching` as a labeled negative), `ConceptKnowledge`/`ConceptLookup`, the
  packet section, decision stages, `PromoteKnowledgeBoundary` (off by default), and the
  permanent soak stage. **Live against qwen3:8b, flag on, all system checks clean.**
  Taught: "axe" minted with Origin=Taught, confidence 0.95, evidence citing the teaching
  sentence verbatim. Asked: *"Yes, I learned that an axe is a tool used for chopping or
  splitting wood from you on August 20."* Negative control: *"I haven't learned what a
  quokka is yet — though I recognize the word from my training, that's not something
  I've studied."* — the boundary articulated in her own words, no pretrained facts
  presented as hers. No violation to record this run; the soak stage notes one whenever
  it happens. 1114 tests green.
