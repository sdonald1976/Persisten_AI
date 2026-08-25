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
