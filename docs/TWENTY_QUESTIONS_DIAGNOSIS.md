# The 20 Questions failure — diagnosis of record (2026-08-24)

Source evidence: the game conversation's Messages export from the second machine
(19 assistant turns). The regression fixture distilled from it, anonymized per
the privacy rules, is `tests/Companion.Tests/Fixtures/twenty-questions-regression.json`.
Columns requiring the second machine's full trace bundle (exact per-turn
ResponsePlans, serialized plans, tool calls, decisions) remain OPEN — the
bundle was never produced; `tools/extract_game_traces.py` generates it if the
per-turn detail is ever needed. Every conclusion below is supported by the
messages alone and is stated with that scope.

## Routing (settled)

`ModelUsed` is production Stheno on all 19 turns — the tuned renderer displayed
nothing. Console logs from that machine show why: serve_cpu answered but each
render exceeded the 25 s canary timeout at real packet sizes (~40–60 s of CPU
prompt-eval), then the server went down entirely. Every turn: attempted →
timed out → fell back, as designed. Superseded since by the merged GGUF served
through Ollama (RENDERER_SHADOW.md §9), which renders in ~1 s on GPU / ~15 s
CPU-pinned.

## The four symptoms, traced

1. **"Moving parts" asked three times** (Q6, Q12 variant, Q13): Q6's answer
   left the 6-message packet window (`RecentMessageCount = 6` ≈ 3 exchanges)
   and existed nowhere else — memory extraction stores durable facts about the
   user, not game state, and no procedure/working-state mechanism exists.
2. **Texture Yes → "does NOT have a distinct texture"**: not window loss — a
   generation error. The Q12 message buried its actual question at the end of a
   300-token essay that also re-discussed texture; the next turn's summary
   misbound the "No" (to adjustable parts) onto the texture property.
3. **Practical → decorative drift**: Q4's practical-Yes and Q9's aesthetic-No
   were far outside the window by Q13, whose self-summary invented "perhaps
   serving decorative purposes."
4. **Handing control back after ~15 questions**: the game frame (asker role,
   question budget) lived only in long-evicted messages; with no plan-level act
   or procedure carrying it, the generator reverted to its assistant attractor.

Structural proof of the window: PromptTokens oscillate 1210–1973 across 19
turns instead of growing.

## Failure separation

| layer | verdict |
|---|---|
| routing | all replies production, by designed fallback; canary silently inert all game (timeout too short for that serving stack — since fixed) |
| retrieval/context | PRIMARY: fixed recency window; no extraction of activity state |
| procedure state | PRIMARY: no working-state ledger exists; the only carrier was the generator's own lossy prose |
| planning | absent, not corrupt: the plan has no vocabulary for activity state |
| rendering | no failure attributable — the renderer never displayed a reply |

## Smallest appropriate fix (proposed, not implemented)

A conversational working-state ledger owned by cognition: when a structured
activity is active, an append-only facts list updated deterministically from
each exchange, injected verbatim into every packet — and, in the V3 protocol,
carried as typed `must_not_contradict`/`background` items with a procedure
source. This is a designed input of ResponsePlan v3 (see
RESPONSE_PLAN_V3_SPEC.md, worked example 9). Raising `RecentMessageCount` is
not the fix; it delays the amnesia and pays tokens forever.
