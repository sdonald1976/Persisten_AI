# Improvement Backlog

Worthwhile changes identified by the full-solution audit that were **deliberately not implemented**
in that pass — each is either too large, too risky, or needs a design decision first. Ordered by
value. Each entry states the alternative designs considered, because "why this way" ages better
than "what".

---

> **Status update:** items **1, 2 and 4 are now done** (see `FULL_SOLUTION_AUDIT.md` §15 and the
> backlog-pass commit). They are kept below with their original reasoning, marked ✅, because the
> alternatives considered are still the useful record. Items 3, 5, 6, 7 remain open.

---

## 1. ✅ DONE — Stop materializing embeddings the retriever never reads — P2, Medium effort, Medium risk

**Implemented as Option A:** `GetRetrievalCandidatesAsync` projects every scalar except the
embedding, untracked, and the read-only consumers (retriever, associative recall, greeter, agent
recall, `/memories`) use it. The pipeline and consolidator keep the full load because they genuinely
need vectors. A reflection-based test asserts the projection stays exhaustive as the entities grow,
and another asserts nothing from it enters the change tracker (so it can never save a null embedding
over a real one).

**Now:** `GetRetrievableMemoriesAsync` loads every non-deleted memory for the user, *including the
embedding blob*, on every turn. The retriever then gets similarity from the in-memory vector index
and never touches `memory.Embedding`. At 10k memories × 768 dims × 4 bytes that is ~30MB of float
arrays deserialized per turn to be discarded.

**Why it matters:** this is the first wall the system hits under years of real use. It is invisible
today (hundreds of memories) and severe later, which is exactly the kind of thing that should be
fixed before it's urgent — but it changes a shared store contract, so not blind.

- **Option A — projection without embeddings for the retrieval path.** Add
  `GetRetrievalCandidatesAsync` returning a lightweight view (id, content, kind, owner, status,
  timestamps, importance, confidence, project). Retriever, associative recall, and the greeter
  switch to it; the pipeline (which needs vectors for dedupe) keeps the full load.
  *Risk:* two shapes to keep in step; EF projections must not be tracked as partial entities.
- **Option B — split embeddings into their own table.** Cleanest long-term; embeddings load only
  when explicitly joined. *Risk:* migration over existing data, touches every memory write.
- **Option C — page/prefilter candidates** (e.g. top-N by recency before scoring). *Risk:* silently
  changes recall semantics — a genuinely old but highly relevant memory could stop surfacing.

**Recommendation: A**, then measure. B is the better destination if the memory table ever gets its
own maintenance story; C should be avoided because it trades correctness for speed, which is the
wrong trade for a memory system whose whole promise is *not* forgetting.

---

## 2. ✅ DONE — SQLite busy/lock handling — P2, Small effort, Low risk

**Implemented:** WAL mode (+ `synchronous=NORMAL`) is enabled once after migration on file-backed
databases — readers no longer block behind the background writer, which was the actual shape of the
problem. Microsoft.Data.Sqlite already retries `SQLITE_BUSY` within its command timeout, so Option A
below turned out to be redundant. A test runs a turn and a sleep cycle concurrently.

Original analysis:

**Now:** concurrent writes (a turn finishing while the reflection worker consolidates) can raise
`SQLITE_BUSY`. Nothing retries; the caller sees a raw DB error.

- **Option A — `busy_timeout` on the connection string** (one line, lets SQLite block briefly).
- **Option B — EF execution strategy with retry-on-busy.**
- **Option C — serialize writes through a queue.**

**Recommendation: A + B.** C is real infrastructure for a problem this system doesn't have at
single-user scale. Add a test that runs a turn and a sleep cycle concurrently.

---

## 3. Per-section token budgets for the context packet — P2, Medium effort, Medium risk

**Now:** sections are capped by *count* (max memories, max loops, max attention items). Content
length is unbounded, so a few long memories plus a long recent transcript can crowd an 8B model's
window, and the sections that lose are the ones rendered last.

- **Option A — token budget per section**, with a documented priority order for who gets trimmed
  first (identity > recent conversation > retrieval > tools > everything else).
- **Option B — global budget with proportional trimming.**
- **Option C — leave it; rely on count caps.**

**Recommendation: A**, but only once there's a real tokenizer estimate in the loop (char/4 is a
poor proxy for a Llama tokenizer). Needs a decision on trim order, which is a product question:
what should Ava lose first when the window is tight?

---

## 4. ✅ MOSTLY DONE — Restart and concurrency test coverage — P2, Medium effort, Low risk

**Implemented:** `TestHost` now accepts a real file-backed connection string, enabling (a) a restart
test — a second host over the same database file rebuilds the cold vector index from disk and
retrieves the same memory — and (b) a concurrency test running a turn and the sleep cycle
simultaneously.

**Still open:** (c) two simultaneous *turns* for the same user (profile/relationship/attention
races), and (d) mid-turn client disconnect leaving consistent state. Both need a deliberate
interleaving harness rather than `Task.WhenAll`, which is why they weren't bundled in.

---

## 5. Split `Program.cs` (875 lines) and `Companion.cs` (764 lines) — P3, Medium effort, Low risk

Both are navigable but at the limit. `Program.cs` naturally splits by area (conversation, memory,
projects, reflection, diagnostics, prompts, media) into endpoint-group extension methods.
`Companion.cs` could move its context-gathering helpers into a collaborator, though care is needed:
the turn's *readability as one narrative* is currently a feature, and splitting it into five files
would cost more than it gains. Do `Program.cs` first; treat `Companion.cs` as optional.

---

## 6. Shared turn-analysis object — evaluated, NOT recommended as posed

The audit asked whether privacy, extraction, reflection, planning, reranking, and auditing could
share one structured pass over the turn instead of each interpreting it independently.

**Finding:** they interpret genuinely different things at genuinely different times — privacy runs
*before* generation and gates writes; extraction runs *after* and needs the assistant reply;
reranking is per-candidate scoring; planning is a gap analysis. The only real overlap is that
several read the same raw text, which is not expensive.

- **Option A — one "turn analysis" model call feeding all consumers.** Fewer calls, but couples six
  independent failure domains into one: a malformed analysis degrades everything at once, and each
  consumer's fallback (which today is specific and well-tested) becomes generic.
- **Option B — share only *deterministic* derived facts** (tokenized text, detected entities,
  in-character flag, secret flag) via a small immutable per-turn record.
- **Option C — status quo.**

**Recommendation: B if anything**, and only when a measured hot spot justifies it. A is the
God-object the audit brief warned about, wearing a performance costume: it trades reliability and
testability for a model call that isn't currently the bottleneck.

---

## 7. Prompt-size regression guard — P3, Small effort, Low risk

There is no test asserting the assembled Chat prompt stays within a sane size for an 8B model. A
cheap guard (assemble a maximal packet from fixtures, assert an estimated-token ceiling) would catch
context bloat at the moment it's introduced rather than when replies start degrading.
