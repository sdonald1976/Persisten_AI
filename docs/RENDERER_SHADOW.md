# Renderer shadow mode — protocol, thresholds, rollback

**Status: frozen before data collection (2026-08-24).** The thresholds in §4 were
written before the first shadow row existed and do not move afterward; a threshold
that fails is a result. Under §§1–7 run-1c is not user-facing; §8 (added later the
same day, on Scott's explicit approval) defines the one exception — a reversible
canary scoped to his user alone. Global promotion still comes only from Scott
after the shadow report, not from this file.

## 1. What runs

When `Companion:RendererShadow:Enabled` is true, every **eligible** turn does the
following after the production reply is final:

- The turn's `ResponsePlan` (already built in shadow every turn) is serialized with
  the frozen `PlanSerialization.CompactV2` — the same file, linked verbatim, that
  produced every training pair and evaluation prompt.
- The run-1c adapter renders it via `serve_tuned.py` on the configured endpoint
  (default `http://localhost:11435`), with the training-time system prompt and the
  evaluation-time sampling options (temperature 0.6, num_predict 220).
- Both replies — production's and the shadow's — are scored by the same
  deterministic battery (`RendererChecks.Check` frozen classes + the real-turn
  proxies in `RendererShadowChecks`).
- One `ShadowComparison` row is recorded: subject `renderer.plan2`,
  `Legacy` = production reply, `Model` = shadow reply, `Agreed` = shadow passed
  every deterministic check, `DurationMs` = shadow latency, `Input` = a JSON
  envelope with plan hash, adapter sha256, model version, VRAM, question mode,
  palette/must-state presence, both violation lists, and both sludge lists.

Isolation contract: `IRendererShadow.Observe` is fire-and-forget over an immutable
snapshot. The shadow path holds no reference to conversation state, the memory
pipeline, goals, tools, or the response stream. Any failure inside it is a debug
log line. The shadow reply exists only in the shadow table.

**Eligibility** (each skip is a `renderer.shadow` decision on the turn trace):
ordinary answered chat turns only; turns that used tools are skipped (the corpus
never covered tool results); privacy-sensitive turns are skipped entirely — the
strictest existing boundary, stricter than the reply-gate's own recording.

## 2. Privacy and retention

Shadow rows live in the same `ShadowComparisons` table, database, and backup
boundary as all other operational telemetry. Nothing is exported anywhere by the
collection path. `ForgetCapturesAsync` — the `/forget` promise — now also matches
`renderer.*` rows against their envelope, production reply, and shadow reply, so a
forgotten memory sweeps its sentences out of renderer rows too. Real conversations
are **never** exported into the training corpus automatically; any future corpus
use of shadow rows is a separate, explicit, human-reviewed step.

## 3. Collection target

At least **100 eligible real turns**, and at least **20 palette-bearing turns**
(envelope `PaletteBearing`) if ordinary conversation produces them — no
conversation is fabricated to reach either number. If palette-bearing turns are
scarce, the report says so and the palette verdict carries the smaller n.

## 4. Promotion thresholds (pre-declared)

Run-1c may be proposed for promotion only if, over the collected window:

| class (from envelopes) | threshold |
|---|---|
| plan-echo + control vocabulary (shadow) | 0 occurrences |
| invented experience/preference (shadow) | 0 confirmed on human review of flagged rows |
| palette leakage (shadow) | flag-rate ≤ production's on the same turns, AND ≤ 2 confirmed leaks in the palette-bearing set |
| MustState omission (proxy, shadow) | 0 confirmed omissions on human review of flagged rows |
| closed-plan questions (shadow) | rate ≤ max(production's rate, 3%) |
| mandatory-question missing (shadow) | 0 confirmed silent drops |
| malformed clarification (shadow) | ≤ 1 confirmed per 10 mandatory-question turns |
| epistemic admission (shadow) | ≤ 1 confirmed leak per 10 epistemic-bearing turns |
| sludge | shadow's disqualifying-class sludge rate ≤ production's |
| latency (shadow rig health, not a promotion bar) | median shadow render ≤ 15 s and VRAM stable; the promotion-grade latency bar is set AFTER merge/GGUF conversion, measured on the serving stack that would actually ship |
| blind paired review (§5) | Scott's would-use rate for shadow ≥ production's, judged blind |

"Confirmed" means a human looked at the flagged row and agreed — the proxies are
deliberately over-sensitive, and a proxy flag alone neither passes nor fails
anything. All flagged rows are reviewed; none may be discarded unreviewed.

## 5. Blind paired review

`tools/renderer_shadow_review.py` builds the review file from the local database:

- **Every** row where either reply has a deterministic violation is included —
  failures cannot be hidden by sampling.
- Clean rows are added by seeded random sample to reach the review size.
- Each item shows the user message and the two replies in random A/B order;
  the mapping is written to a sealed key file, opened only after judging.
- Both files stay local, inside the same privacy boundary as the database.

## 6. Configuration

```json
"Companion": {
  "RendererShadow": {
    "Enabled": true,
    "Endpoint": "http://localhost:11435",
    "AdapterSha256": "4732591a39e6aa078b87445e42c2e049cf1082009975345839e9604c7b36af2f",
    "ModelVersion": "run-1c adapter on Qwen2.5-3B-Instruct aa8e7253 (freeze-run1c, commit 13e51c6)",
    "TimeoutSeconds": 60
  }
}
```

The serving process is started separately (it is a measurement instrument, not a
production dependency):

```bash
cd /c/Source/Persisten_AI/training/renderer && ../.venv-train/Scripts/python.exe serve_tuned.py --adapter runs/run-1c/adapter-final --port 11435
```

If the server is down, shadow observations are dropped silently (debug log only);
the turn never notices.

## 7. Rollback

Set `Companion:RendererShadow:Enabled` to `false` (or remove the section) and
restart. That is the entire rollback:

- The DI container registers `NullRendererShadow`; `Companion` holds only the
  interface, and its `IsObserving` short-circuits before any snapshot work.
- No schema was added — rows already collected simply stop growing and remain
  governed by the ordinary telemetry retention and forget rules.
- `PromoteResponsePlan` / `PromoteKnowledgeBoundary` are untouched by this
  feature in either direction; the renderer gained no authority anywhere.
- Stopping `serve_tuned.py` is an equally complete rollback at the process level
  (observations drop; turns unaffected), useful when the GPU is needed.

Verified by test: with the flag off, the shadow service is the null object and a
turn makes no renderer HTTP call and records no renderer row; with the flag on and
the endpoint dead, the turn completes identically and only a debug line notes the
dropped observation.

## 8. The user-scoped canary

Approved 2026-08-24 as a reversible, user-scoped step — NOT global promotion.
When `Companion:RendererShadow:CanaryUserId` names a user, that user's eligible
non-tool turns DISPLAY the run-1c reply instead of production's:

- Production still generates first (its reply is the fallback and the
  comparison row's other half). The canary render then runs synchronously with
  its own timeout (`CanaryTimeoutSeconds`, default 25 s).
- Fallback to production is automatic when the renderer is unavailable, times
  out, returns empty output, or fails a **critical** fidelity check: spoken
  control vocabulary, recited plan text, a dropped mandatory question, or
  third-person narration. Softer proxies (palette, sludge, omission
  heuristics) flag rows for review but never override the displayed reply.
- Only the displayed reply enters conversation history, memory extraction,
  reflection, and every downstream state — the swap happens before the reply
  gate and before storage. On streaming turns, production tokens are withheld
  and the chosen reply is delivered to the stream once, whole.
- The comparison row is still recorded (non-sensitive turns), with `Applied`
  naming the renderer whose reply the user actually saw ("model" = run-1c,
  "legacy" = production). Canary outcomes are counted
  (CanaryDisplayed/CanaryFallback) and exposed with the active renderer and
  adapter sha at `/diagnostics/renderer-shadow`.
- No other user's routing changes; the adapter is not retrained, merged,
  quantized, or otherwise altered.

Canary rollback: clear `CanaryUserId` (one setting) and restart — the user is
back on the production renderer; shadow collection continues unchanged.

## 9. The merged GGUF deployment (approved 2026-08-24)

On Scott's approval, the run-1c adapter was merged into the pinned base and
imported into Ollama as `renderer-shadow` (q8_0, layer 54e76dd5; merge record
with per-shard sha256 in `training/renderer/merged/run-1c/merge-record.json`;
build reproducible anywhere via `tools/build_renderer_model.py`). The adapter
in git remains the canonical artifact; the GGUF is a derived build product.

**Revalidation against the frozen battery, GGUF vs the PyTorch adapter:**

| instrument | adapter (NF4/GPU) | GGUF q8_0 (Ollama) |
|---|---|---|
| validation CLR (149) | 2.7% (4) | **1.3% (2)** |
| closed-plan questions (113) | 2 | **0** |
| opening-trigram diversity | 0.95 | 0.95 |
| unseen compositions (32) | 3 fail | 5 fail |
| fixtures (multi-sample) | axe-known 7/7, precious 2/7, clarify 1/7 | axe-known 3/3, precious 2/3 |
| speed | 2.0 tok/s, ~8 s/reply | **42.9 tok/s, ~1 s/reply (GPU)** |

The two extra unseen failures are name/token paraphrases (the mandatory
question and the epistemic admission still fired in both u1b-epimq misses);
u1b-cuoq-02 is the one genuinely wrong reply. Judged not-going-backwards:
the primary instrument (validation, n=149) improved, and the deltas at n=32
are within single-draw noise of paraphrase-class misses. All raw outputs in
`training/renderer/runs/gguf-q8/` (machine-local).

**Deployment note for small GPUs:** the app's full pipeline juggles several
models; `Companion:RendererShadow:NumGpu: 0` pins the renderer to CPU inside
Ollama (~15-17 s/render) so the chat model is never evicted. Measured on the
GTX 1660: per-turn cost is dominated by the pre-existing pipeline
(extraction ~27 s + chat ~35 s + reranker/safety/planner ~25 s), not the
renderer. serve_tuned.py / serve_cpu.py remain lab instruments for
frozen-eval reproduction against the unmerged adapter.
