# Run-2 deployment: shadow, then canary

Integrated at `f5e1907`, deployed from clean `master`. Production routing is unchanged for every
user except `demo-user`.

## Verification before loading

`serve_run2.py` refuses to start unless every artifact hashes correctly. All of it verified on
this machine:

| artifact | sha256 | state |
|---|---|---|
| adapter (10 files) | `a86caf4a…` | verified against `SHA256SUMS` |
| base weights | `8b2ba1b4…` | verified against the training manifest |
| tokenizer | `534b7167…` | verified against the training manifest |
| corpus (9 files) | — | verified against its own `SHA256SUMS` |

**Git LFS pointer detection tested for real**, not asserted: the adapter was replaced with a
genuine LFS pointer, the endpoint refused to load with the reason and the remedy, and the file was
restored byte-identically (re-verified). A pointer is detected by *content* — an unfetched LFS file
is present, small and text, so an existence check passes it and the model then loads with no
adapter at all and silently serves the base.

## Fresh-machine bootstrap

`start-all.ps1 -VerifyOnly` → **OK, 13 active dependencies satisfied.**

```
verified     mouth.adapter    training/mouth/runs/run-2/adapter-final/adapter_model.safetensors
present      mouth.base       Qwen/Qwen2.5-3B-Instruct
verified     mouth.served     run-2
```

`mouth.served` was initially classified as a locally-built Ollama model, so bootstrap asked Ollama
for a tag named `run-2`, got "not found", and **refused startup while the endpoint was running
perfectly**. The kind was wrong — there is no tag. `HttpServedAdapter` asks the endpoint what it
loaded and compares that to the same pin the adapter file carries, which is the point: a healthy
process serving the *wrong* weights answers every liveness check exactly as well as the right one.

## Cost

| | test (171) | hard-eval (61) |
|---|---|---|
| cold start | 11.53 s | — |
| p50 | 3,956 ms | 2,954 ms |
| p95 | 5,913 ms | 3,902 ms |
| max | 8,894 ms | 4,833 ms |
| failures | 0 | 0 |
| peak VRAM | 2.87 GiB | 2.87 GiB |

In-turn canary render latency measured live: p50 **5,183 ms**, max 6,289 ms.

**The served path is the measured path.** All 232 replies through HTTP are byte-identical to the
in-process evaluation — 171/171 and 61/61. Fidelity through the endpoint is unchanged: 93.0% test,
95.1% hard-eval.

## Shadow, then canary

Shadow first: 5 real turns as `demo-user` with `CanaryUserId` empty. Production replies displayed
throughout; the mouth rendered beside them (its peak VRAM grew from 3.08 to 4.01 GB) and recorded.

Canary then enabled for `demo-user` alone:

| | |
|---|---|
| rendered | 12 |
| **displayed** | **12** |
| **fallback** | **0** |
| failed | 0 |
| loaded adapter | `a86caf4ad829fef6a427d39066ac5a744cf563934df080c8190713b52cfa235d` — matches the pin |

`run-1c activeRenderer: production`, `run-1c canaryUser: null`. Global routing unchanged.

Existing canary suite: **64 passing**. Full suite: **1,986 passing** (18 new).

## The families you asked about

| | test | hard-eval |
|---|---|---|
| `b4` unsupported detail | 11/11 | **13/16** |
| `b6` forbidden background | **1/3** | n/a |
| question-policy failures | 5 | 0 |
| opening diversity | 48.5% | **9.8%** |
| distinct replies | 77.2% | **26.2%** |

`b4` and `b6` are unchanged from the training report and remain the known weaknesses: Run-2 says
more, which is what makes it natural and what occasionally carries an unlicensed detail with it.

**The hard-case stubs are visible in production too.** 9.8% opening diversity and 26.2% distinct
replies means run-2 answers hard cases with a handful of near-identical short replies — and the
deterministic gates cannot see it, because terseness violates nothing. The live canary turns show
the same shape: three consecutive replies ended "Hope you're feeling better!" / "Hope you're okay!"
and one answered a plumber question with an offer of tea. None of that is a gate failure and none
of it would trigger a fallback.

## Invariants, enforced in code and pinned by tests

- **One candidate, ever.** When the mouth canary owns a turn the run-1c canary stands down.
- **No dual exposure.** Streaming is suppressed on canary turns, so the production reply is never
  shown and then replaced.
- **Fallback covers every failure class** — unavailable, timeout, empty, malformed, server error,
  or any critical gate. `null` means "show production"; the reason is recorded, never acted on.
- **No plan/4 or no packet, no render.** The turn stays on production rather than being answered
  from a reconstruction.
- **Shadow cannot display.** `ObserveMouth` returns `void` by construction.
- **Rows say which model wrote them.** Both arms share a recorder, and it took the adapter hash
  from run-1c's options unconditionally — a mouth row claimed run-1c's adapter beside run-2's
  output. Fixed; a test pins it.
- **The gate knows plan/4.** The artifact check knew plan/3's vocabulary only. Run-2 is the first
  model whose prompt contains `must_express` and its siblings, so those are the words it can echo.

## Blind review pack

30 items, sealed. Strata: 14 general test, 8 hard cases, 5 `b4`, 3 `b6`. Arm labels shuffled per
item; the key is a separate file so a review can be shown to predate opening it.

- `pack.json` `821ce334…`
- `KEY.json` `a4c812c9…`
- `REVIEW.md` `e13dbf5d…`

Informative, not a blocker — the measurements above stand without it.

## Rollback

Clear `Companion:RendererShadow:Mouth:CanaryUserId` and `demo-user` sees production again. Clear
`Mouth:Enabled` and run-2 stops rendering entirely. No other state is involved.
