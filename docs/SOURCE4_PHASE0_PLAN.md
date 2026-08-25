# Source 4 — Phase 0: the EmotionalSignal retention repair (declared before execution)

Declared 2026-08-25, BEFORE any code. Its own commit, independently revertible,
and it touches no register/contributor work: Phase 0 is a privacy repair to an
existing durable store, not part of Source 4a/4b/4c.

## The gap being repaired

`EmotionalSignals` is durable and append-only. Each row holds `Evidence` — the
user's **verbatim cue phrase** — and `Topic`, extracted from their message.
Verified in the Source 4 inspection: these rows are swept by neither `/forget`
nor any retention job. Forgetting the memory a feeling attached to left the
feeling, and the user's quoted words, behind forever.

`MessageId` already exists but is documented as "a soft reference for
explainability" and nothing links through it.

## The repair

1. **Exact evidence identity.** `EvidenceEventId` (Guid, assigned at capture)
   plus `EvidenceKind`, alongside the existing `MessageId`. Both are exact
   identities; neither is text.
2. **Identity-only forgetting.** `ForgetByEvidenceAsync(userId, messageIds,
   eventIds, …)` matches on `MessageId ∈ ids` OR `EvidenceEventId ∈ ids`,
   user-scoped. **No substring, no containment, no normalization of text** —
   there is no text comparison anywhere in the path.
3. **Redaction, not deletion.** A forgotten signal keeps only privacy-permitted
   metadata: `Id`, `UserId`, `Timestamp`, `Sentiment`, `Valence`, `Label`
   (a lexicon token, never the user's words), and audit fields. It loses
   `Evidence` and `Topic` — both user-derived text — and is marked
   `EvidenceForgotten` with `ForgottenAt`.
4. **A forgotten signal contributes nothing.** `RelationshipTracker` excludes
   forgotten rows entirely, so a redacted row cannot drag the average or the
   trend. Authority dies with the evidence.
5. **Declared retention lifecycle.** `IEmotionStore.PruneAsync(olderThan)`,
   called from `SleepCycle` beside the existing diagnostics/experience sweeps,
   with `EmotionalSignalRetention = 180 days` declared as a constant. Rows older
   than that are deleted outright.

## Declared cases: 9

| # | case | expected |
|---|---|---|
| 1 | forget a memory whose evidence message produced a signal | that signal redacted; `Evidence` and `Topic` null; metadata kept |
| 2 | **ambiguous** — two signals whose cue text overlaps, different messages | only the one with the matching id is redacted; the other is untouched |
| 3 | **ambiguous, identical text** — two signals with byte-identical `Evidence`, different messages | only the id-matched one is redacted (text similarity is never consulted) |
| 4 | **missing evidence** — memory forgotten with no evidence rows | nothing redacted, no error |
| 5 | **missing evidence** — signal whose `MessageId` matches nothing forgotten | untouched |
| 6 | **already forgotten** — forgetting the same evidence twice | idempotent; count 0 the second time; `ForgottenAt` unchanged |
| 7 | **cross-user** — same evidence id present for two users | only the requesting user's row is touched |
| 8 | **simultaneous conversations** — one user, two conversations, one signal each | forgetting conversation A's signal leaves conversation B's intact |
| 9 | retention sweep | rows older than the declared window are deleted; newer rows survive |

## Pass criteria

1. No text comparison exists in the forget path (asserted by the signature: it
   takes ids only, never strings).
2. Verbatim cue phrases do not survive forgetting — `Evidence` and `Topic` are
   null on every redacted row.
3. A redacted signal contributes nothing to `RelationshipSnapshot`.
4. Forgetting is idempotent and user-scoped.
5. A declared retention lifecycle exists and runs from `SleepCycle`.
6. Migration is additive; full suite green; V2/Run-1c/routing/displayed output
   unchanged.

---

# Amendment, 2026-08-25: the tombstone is smaller than declared

Conditional acceptance corrected §3 of the plan above. `Sentiment`, `Valence`
and `Label` were declared "privacy-permitted metadata". They are not: each is a
semantic DERIVATIVE of the forgotten sentence — a reading *of* it — and a
lexicon token is still a reading. All three are now purged alongside `Evidence`
and `Topic`, together with `ProjectId` (which says what the feeling attached to).

`Sentiment` and `Valence` became nullable to make purging honest: zeroing a
valence or bucketing a sentiment to `Neutral` would have written a *claim* where
the reading used to be.

**The tombstone, in full:** `Id`, `UserId`, `MessageId`, `EvidenceEventId`,
`EvidenceKind`, `EvidenceForgotten`, `ForgottenAt`, `Timestamp`, `FollowedUp`.
Identifiers, status, and operational timestamps — nothing else. `Timestamp` is
retained because the declared 180-day lifecycle sweeps by age, and idempotency
needs the row to stay findable.

**Retention lifecycle, restated:** age decides, status does not. A tombstone is
neither kept longer for audit nor dropped sooner for privacy — it expires at the
same 180 days as an active row.

**Mood transitions are covered too.** `CompanionMoodTransition` gained
`SourceEvidenceEventId`, so `/forget` reaches the transitions a forgotten moment
produced and purges their `AppliedValence`.

## Privacy compaction (contract decision, 2026-08-25)

The amendment above purged `AppliedValence`, which removed the *stored*
derivative but not the *arithmetic*. Her spirits trajectory is a deterministic
function of the valences that moved it, so a redacted transition's neighbours
bracketed it exactly. The contract decision resolved it:

> `/forget` removes the evidence and its reconstructable derivative history, but
> does **not** retroactively un-move Ava's present mood.

**How it works.** `ICompanionMoodLog.CompactForgottenAsync` deletes the
transition chain and writes a single opaque **baseline** carrying her spirits as
they actually stand: `IsBaseline = true`, `PreviousSpirits = null`,
`AppliedValence = null`, `SourceEvidenceEventId = null`, `CompactedAt` set.
Later transitions continue from it, versions staying monotonic.

**Compaction is total, and partial compaction was tried first.** Cutting only
at-or-before the boundary looks tidier and does not work: the row immediately
after the cut carries the boundary's own result as its `PreviousSpirits`, and
its own `AppliedValence` is intact, so the forgotten value falls straight back
out. Severing that costs the successor's history anyway, so the honest move is
the complete one.

**What is deliberately lost.** Exact replay across a baseline is unavailable,
because the rows it would need are exactly the rows whose arithmetic leaked.
`MoodReplay.Replay` reports this rather than approximating it: `CoversFullHistory`
is false and `Diagnosis` names the compaction version. A number produced by
guessing at deleted history would be worse than no number.

**What is deliberately kept.** Her present mood. Being affected by a moment
happened; forgetting the record of it does not undo that.

## Amendment tests (7)

1. a forgotten row serializes with no evidence, topic, sentiment, valence or label;
2. forgetting one event leaves a semantically identical one fully intact;
3. snapshot reconstruction ignores tombstones before AND after a process restart
   against the same database file;
4. the 180-day sweep treats active and redacted rows alike;
5. forgetting purges the linked mood transition's stored reading;
6. the real `/forget` path reaches both stores in one pass;
7. the real `/forget` path compacting the chain while preserving her mood.

## Compaction tests (11, in `MoodCompactionTests`)

The characterisation test that existed to fail when the leak was fixed is gone,
replaced by its inverted form: a generous recovery oracle that tries the stored
value, each row's own endpoints, and **every pair of endpoints the log still
exposes**, and must find nothing. Applied to first / middle / latest forgetting
over a five-event chain, plus: mood preserved as an opaque baseline; later
transitions continuing contiguously; replay diagnosed rather than approximated;
survival across a process restart against a file-backed database; six nudges
racing a compaction; cross-user isolation with both users living the same
valence; a no-op for an unknown event; and the real `/forget` path end to end.
