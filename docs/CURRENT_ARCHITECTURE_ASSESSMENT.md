# Current Architecture Assessment

_Date: 2026-08-05_

## Summary

**The repository is empty.** At the time of this assessment it contained no source
files, no branches with content, no tags, and no commits — only an initialized git
directory pointing at `https://github.com/sdonald1976/Persisten_AI`.

Verification performed:

```
git status                 → "No commits yet"
git ls-remote --heads      → (empty)
git ls-remote --tags       → (empty)
find . -not -path ./.git/* → (nothing)
GitHub code search         → total_count: 0
GitHub list_branches       → []
```

This is therefore a **greenfield build**, not a migration or refactor. The original
task framing assumed an existing (possibly over-engineered) C#/.NET solution to inspect
and prune. That solution does not exist. This document records that fact honestly rather
than inventing legacy components.

## 1. What currently exists

Nothing but an empty git repository and its remote. There is no code, no configuration,
no documentation (prior to this planning set), and no build system.

## 2. Which parts directly support the companion goal

Not applicable — there is no existing code to support the goal. Everything will be built
new against the design in `PERSISTENT_COMPANION_ARCHITECTURE.md`.

## 3. Which parts are unnecessary or overly complicated

Not applicable. The most valuable property of an empty repository is that **there is no
accumulated complexity to fight**. The primary risk here is the opposite of the usual
one: the temptation to *introduce* speculative complexity on a blank canvas. The design
deliberately guards against that (see "Risks" below).

## 4. Which parts can be reused

Nothing internal. Externally we will reuse well-understood, low-risk building blocks
rather than writing them ourselves:

- **.NET 8 (LTS)** — the stated language/runtime expectation for this project.
- **EF Core + SQLite** — authoritative relational storage, local-first, zero-ops.
- **`System.Text.Json`** — structured (JSON-schema-guided) model I/O for extraction.
- **`Microsoft.Extensions.*`** — DI, configuration binding, structured logging, hosting.
- **xUnit** — unit / integration / end-to-end tests.
- **An existing LLM** (local via Ollama, or a hosted OpenAI-compatible/Anthropic API)
  strictly for language generation and embeddings — never as the memory store.

Vector search starts as **in-process cosine similarity over embeddings stored as BLOBs
in SQLite**, hidden behind an interface so a dedicated vector store (`sqlite-vec`,
Qdrant, etc.) can be swapped in later without touching callers.

## 5. Which parts should be isolated, deprecated, or removed later

Not applicable now. Forward-looking guidance instead:

- Keep **model providers** behind interfaces so no vendor leaks into core logic.
- Keep **vector search** behind an interface so the naive implementation can be
  replaced without a rewrite.
- Treat the **relational database as authoritative**; embeddings are a derived index
  that can always be regenerated and discarded.

## 6. Smallest viable implementation

The smallest thing that demonstrates real value end-to-end (detailed in
`FIRST_VERTICAL_SLICE.md`):

1. Store conversations and messages in SQLite.
2. Seed a handful of structured memories (semantic + episodic + one project + one open loop).
3. On a new user message, retrieve the most relevant memories with **ranked, explained**
   scores.
4. Assemble a bounded context packet.
5. Generate a response via a **mockable** model interface.
6. Print retrieval diagnostics (what matched, scores, why, what was excluded).

A `MockChatModel` / `MockEmbeddingModel` makes the whole loop runnable and testable with
**no network or GPU**, which keeps the initial slice trivially reproducible in CI.

## Risks and mitigations

| Risk | Mitigation |
|------|-----------|
| Blank-canvas over-engineering (the project's explicit anti-goal). | Phase gates; every component must map to a capability in the vision doc; no "cognitive" subsystems. |
| Memory bugs are hard to diagnose. | Per-turn diagnostics (`TurnTrace`) are a Phase-2 deliverable, not an afterthought. |
| Vendor lock-in to one LLM. | Separate interfaces per model role; a Mock provider ships first. |
| Vector DB drift becoming authoritative. | Relational store is the source of truth; embeddings are regenerable. |
| Stale facts presented as current. | Temporal fields + lifecycle states from day one; retrieval is validity-aware. |
| Cross-user data leakage. | `UserId` is a required, indexed key on every record; enforced at the store boundary and tested. |

## Migration recommendation

There is no legacy code to migrate. Recommendation: proceed directly to the phased
greenfield plan in `IMPLEMENTATION_PLAN.md`, beginning with the Phase-2 vertical slice.
Because the repo is empty, "migration" reduces to **scaffolding a clean solution** and
resisting scope creep.
