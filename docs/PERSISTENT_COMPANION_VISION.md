# Persistent AI Companion — Vision

> **Guiding principle: Build continuity, not consciousness.**

## 1. Product goal

Build a conversational AI system that **remembers a person over months and years** —
their history, projects, preferences, decisions, and unfinished conversations — and
**retrieves the right context naturally** when it becomes relevant.

Language generation is delegated to an existing model (local or hosted). The value of
this project lives entirely in the layers *around* the model:

- durable memory,
- continuity across sessions,
- relevance-ranked retrieval,
- temporal reasoning,
- project awareness,
- and honest handling of uncertainty.

The companion should become steadily more knowledgeable about the user **without
pretending to know things that were never stated.**

## 2. Non-goals

This project is explicitly **not**:

- an attempt to simulate consciousness or emotion;
- a model of the human brain, neurons, or cognitive "stages";
- a blank-slate general intelligence;
- a from-scratch foundation-model training effort;
- a speculative multi-subsystem architecture.

We do not add developmental-learning engines, emotion simulators, or abstract
"cognitive" subsystems. Every component must earn its place by directly serving the
companion experience.

> **Amendment (2026-08-20).** The "no cognitive subsystems" line above bans
> *speculative* subsystems — components justified by an analogy to minds rather than
> by an observed failure. It does not ban explicit, inspectable state that the system
> owns instead of the chat model: working conversational context, concept knowledge
> with provenance, recorded uncertainty, turn intent. Those are being added
> deliberately under [`LANGUAGE_ORGAN.md`](LANGUAGE_ORGAN.md), each traceable to a
> live failure and each meeting the standard this document already sets — derived
> from what actually happened, able to say where it came from. The guiding principle
> is unchanged: continuity, not consciousness.

## 3. Target user experience

A user talks with the companion regularly over a long period. Later — possibly months
later — they can say something oblique and be understood:

> **User (months later):** "I finally tested that board at home."
>
> **Companion:** "Nice — the Jetson Nano you deployed the object-detection service to
> back in March? You'd flashed it but hadn't run it on your home network yet. How did
> the latency look?"

The user should **never** have to re-explain:

- who they are,
- what they like,
- what projects they're working on,
- what decisions were already made,
- what approaches already failed,
- what happened in earlier conversations,
- or what questions remain open.

When the companion is unsure, it says so plainly and asks a short clarifying question
rather than inventing certainty.

## 4. Major capabilities

| # | Capability | One-line description |
|---|-----------|----------------------|
| 1 | Conversation persistence | Every message stored durably with full metadata, available beyond the model's context window. |
| 2 | Episodic memory | "Things that happened" — events with when-it-happened vs. when-mentioned vs. when-recorded. |
| 3 | Semantic memory | Durable facts and preferences, with confidence, validity, and supersession history. |
| 4 | Project memory | Projects as first-class records: status, decisions, blockers, open questions, activity log. |
| 5 | Open loops & commitments | Unfinished tasks/questions surfaced when contextually relevant — without nagging. |
| 6 | Temporal reasoning | Facts change; newest statements don't blindly erase history. |
| 7 | Entity resolution | Recognize that many references point to one thing — evidence-based, confidence-aware. |
| 8 | Memory retrieval | Ranked, explained retrieval of only the memories likely to matter. |
| 9 | Context assembly | A bounded context packet that separates fact from inference from stale info. |
| 10 | Memory extraction | Candidate memories proposed, then validated before acceptance — never free-written. |
| 11 | Uncertainty & provenance | Every memory traces back to source messages and carries confidence. |
| 12 | Corrections & forgetting | Correct, delete, supersede, merge, split, export, inspect. |
| 13 | Privacy boundaries | Strict per-user isolation, local-first, exportable, fully deletable. |

## 5. Success criteria

The project is succeeding when **all** of the following hold:

- [ ] A user can resume a months-old discussion naturally, from an oblique reference.
- [ ] The system retrieves the right project **without flooding the prompt**.
- [ ] Outdated information is not presented as current.
- [ ] Every important memory carries **evidence and confidence**.
- [ ] The user can **inspect and correct** stored memory.
- [ ] A deleted memory no longer appears — not via retrieval, summaries, or embeddings.
- [ ] Ambiguous references are handled **honestly** (rank, then ask if unsure).
- [ ] Continuity improves **without any pretense of consciousness**.
- [ ] The architecture stays **understandable to a single developer**.

## 6. Reference scenarios (acceptance-level)

These drive the evaluation suite (see `IMPLEMENTATION_PLAN.md`, Phase 6):

- **A — Returning to a project.** "I deployed to a Jetson Nano but haven't tested at
  home." → months later → "I finally tested it." Retrieve the Jetson project, find the
  open testing loop, don't confuse it with other deployments.
- **B — Changed preference.** "I'm doing low carb." → later → "I stopped low carb."
  Keep the history, mark the earlier state not-current, don't call the user low-carb now.
- **C — Ambiguous reference.** "That board finally arrived." Multiple hardware projects
  exist → rank candidates, use recency + open loops, ask a concise clarification when
  confidence is low.
- **D — Corrected memory.** Companion names the wrong project → "No, that was the buoy
  project, not the AI project." → correct, re-associate, keep an audit trail, don't repeat.
- **E — Long-term continuity.** User returns after months and alludes to an ongoing
  interest without naming it → identify the likely topic, don't recite an unrelated profile.
