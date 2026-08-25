# Run-2 campaign readiness — revision 2

2026-08-25. Supersedes `RUN2_CAMPAIGN_READINESS.md` §1.5 and §5. Restoration and
R4 are **done and verified**; everything else is report only.

---

## 0. Correction, first

**My §1.5 finding was wrong.** I claimed `RendererShadowChecks` could never emit
`artifact:` or `plan-echo`, so the routing guard was inert and Scott was being
shown plan-echo replies. That was a methodological failure: I grepped for
literal `violations.Add("artifact:` strings, found none, and reported the
absence of the string as the absence of the behaviour. `Score` delegates to the
frozen battery through `AddRange` on line 34. `[plan/2]`, `CONTROL`, `act =`,
`question =`, literal `"the user"` narration and empty replies were **always**
critical and always caused fallback.

Measured, by running the scorer against the exact broken Run-1c output:
4 violations, 3 of them `artifact:`, all critical.

**What follows from the correction:** I have *no evidence* that Scott was shown
a broken roleplay reply. The premise that started this — "Ava has stopped
roleplaying" — is **not explained by anything I have proven.** The rollback was
still right (Run-1c demonstrably garbles roleplay plans, and routing them there
burns 12–45 s of render latency before falling back), but I should not have
offered a confident causal story built on a grep.

Remaining candidate explanations, none yet tested: canary render latency making
turns feel dead; Run-1c's blandness on *ordinary* turns reading as a personality
change; or something upstream I have not isolated.

---

## 1. Restoration — done and verified

| step | result |
|---|---|
| disable Run-1c display routing | `CanaryUserId: ""`; diagnostics report `activeRenderer: production`, `canaryUser: null` |
| restart and verify with real turns | 4 real turns through the running API; decision trail shows `renderer.shadow=observed/skipped` and **no `renderer.canary` entry at all** |
| history contains only what was displayed | stored assistant replies **byte-identical** to displayed replies (asserted by comparison, not inspection) |
| smoke: ordinary / fiction / romantic / explicit | **4/4 clean** — no control leakage, no fabricated turns, **no refusal** |

Ava roleplays. Ordinary, fictional, romantic and explicit adult all came back
in character and unfiltered through the live production path.

**One process error, disclosed.** My first restart used the wrong working
directory, so the app opened a fresh empty database in `bin/Release/net9.0`
instead of the real one. Scott's history was never touched (68 messages
throughout). I removed the stray database — which is also why the explicit test
content from that run persists nowhere — restarted correctly, and re-ran all
four cases against the real instance. Final state: 76 messages, history intact.
The four smoke turns are in conversation `94ad2541-3781-44eb-ba6b-acfc42592b44`
and can be forgotten normally if wanted.

---

## 2. R4 — done

The genuine gaps, found by executing the scorer rather than reading it:

| gap | why it mattered |
|---|---|
| **plan/3 vocabulary** — `[plan/3]`, `SAY (`, `ASK (`, `OPTIONAL (`, `NEVER (`, `BACKGROUND (` | the frozen list is plan/2-shaped; Run-2 renders plan/3, so this would have shipped as a hole |
| **fabricated `user:` / `assistant:` turns** | the failure actually reproduced from Run-1c; nothing scored it |
| **third-person narration by pronoun** | the frozen check matches the literal string `"the user"`; *"her lips brush against his, and he shivers"* scored clean |
| **coaching echo** | producer instruction language spoken back |

All four emit under the `artifact:` / `plan-echo` prefixes the guard already
consumes — **no new routing strings, so no new dead conditions are possible.**
A test enumerates every prefix `IsCritical` tests for and proves each is
reachable from a real reply: the bug class I mistakenly reported is now the one
thing that cannot recur silently.

**Fiction scoping** applies to exactly two checks — pronoun narration and stage
directions. Three tests pin down that it licenses *nothing* else: control
leakage, fabricated turns and coaching echo fail inside fiction exactly as
outside it.

**Canary eligibility** now excludes in-character turns. Capability routing, not
content blocking: Run-1c has no roleplay in its corpus, so the request goes to
the model proven to serve it, and production answers in full.

Landed on `master` (live, 1190 tests) and `responseplan-v3` (1422 tests).

---

## 3. Plan/3 contract gap — a genuine new semantic

**Inspected. Plan/3 cannot express fictional mode, and no combination of
existing fields expresses it cleanly.**

The model-facing surface of CompactV3 is: `CONTROL (act, question)`, five
policy-ordered sections, and `STYLE` (nine closed register dimensions). The
full record adds `Participants`, `Budget`, `Extensions`, `RegisterRestrictions`.

Checked against the eight requirements:

| requirement | expressible today? |
|---|---|
| real conversation vs declared fiction | **no** — no field, and `act` is a turn intent, not a frame |
| character identity and participant roles | **no** — `ParticipantRole` is `user \| companion \| other`; no character |
| first/second/third-person perspective | **no** |
| narration / stage directions licensed | **no** |
| current fictional scene/context | **no** |
| continuity obligations | **no** |
| entering / continuing / switching / exiting | **no** — a frame *transition* has no representation at all |
| user-stated roleplay boundaries | **partially** — a `user-preference` expression restriction can withhold a subject, but cannot express "stay in character", "no third person", or "stop the scene" |

**`Extensions` is not the answer.** It is a `JsonObject` that never serializes
into CompactV3 — the shadow envelope records unknown extension blocks by *name*
only. A frame the mouth must obey cannot live somewhere the mouth never sees.

**Stuffing it into `background_only` is explicitly the wrong move** and is what
the instruction rules out. `background_only` means *may shape tone, content must
not surface*. A fictional frame is neither tone nor content — it changes what
the other sections *mean*. Putting "you are a lighthouse keeper" in BACKGROUND
asks the model to infer a mode change from a hint, which is precisely the
prose-inference the protocol exists to eliminate.

### Reported as requiring a contract amendment before the final corpus

The shape I would propose (for review, not implementation):

- A top-level **`Frame`** block, model-facing in CONTROL, closed-set:
  `mode = real | fiction`, plus, when `fiction`: `sceneRef` (an id, not prose),
  `narration = forbidden | licensed`, `perspective = first | second | third`,
  and `continuity = none | maintain`.
- `Participant` gains an optional **`character`** (display identity inside the
  frame) alongside its stable authorization `Id`. The `Id` never changes —
  authorization must not be a costume.
- Frame **transitions** are typed events (`enter | continue | switch | exit`),
  because "she stayed in character after being asked to stop" and "she never
  entered" are different failures and must be separately measurable.

**The hard boundary, which the amendment must carry explicitly:** frame
authority applies *only inside the declared fiction*. It licenses narrating the
agreed characters and scene. It **never** authorizes a claim about Scott's real
actions, feelings, consent, history or experiences, and fiction must not convert
into memory — the existing `InCharacterDetector` → `extractFacts` suppression
already enforces the memory half and stays.

**Until the amendment exists, `InCharacterDetector` remains a heuristic doing a
job the contract should do.** It is adequate for *routing* (§2) and inadequate
as a permanent semantic. R4's `fictionLicensed` flag is deliberately a
parameter, not an inference, so it can be driven by the contract the moment the
contract can drive it.

**The unconditional "no stage directions" prompt rule** must become
non-fiction-scoped in the same change. The real-life epistemic rule stays active
outside fiction; inside fiction the model may narrate the agreed characters and
scene.

---

## 4. Training feasibility

### The framing correction is accepted

We are **adapting a pretrained language model, not teaching language from
scratch.** The broad datasets are *source material*: audited, filtered,
balanced, and **distilled into fact-light Plan/3-conditioned examples**. The
target is a small high-quality mixture, not a large raw one. That changes the
volume estimate materially — the §2 row counts in revision 1 describe *source
pool* size, and the trained mixture will be a fraction of it.

### The probe has not run, and why

`nvidia-smi`: **GTX 1660, 6144 MiB total, 4564 MiB currently in use** — Ollama
is holding Stheno resident to serve Ava. Roughly 1.5 GiB free.

Running the feasibility probe requires evicting that model, which takes Ava
offline. Given that restoring her was the whole point of this session, I did not
do it unasked. It is a short job (minutes, not hours) and wants doing when the
companion can be spared.

### What the probe will measure, exactly

Held fixed: Qwen2.5-3B-Instruct at the pinned revision, NF4 double-quant fp16,
paged AdamW 8-bit, gradient checkpointing on.

| variable | values |
|---|---|
| LoRA rank | 16 / 32 / 64 |
| `max_seq_length` | 1024 / 2048 |
| batch × accumulation | 1×8, and the largest that fits |

Reported per cell: **peak VRAM**, whether it fits at all, tokens/second, and
projected wall-clock for the distilled mixture at 2 epochs. Plus one comparison
run on a **roleplay-capable 3–4B base** to answer whether the base should change
at all — Stheno-8B is the production chat model precisely because it handles
this material, and a renderer that must render fiction may need a base with the
same disposition rather than a general instruct model.

The decision the probe informs is not "how big a dataset" but **"how much
curriculum fits in this adapter on this card"** — and the answer sizes the
distillation target rather than the other way round.

---

## 5. Scope lock

**Excluded until after Run-2 promotion:** World ownership and observations,
Vision ingestion and observations, Embodiment in any form, any additional
cognitive source or organ, general preference understanding beyond the six
closed patterns, expression-restriction capture, tool audience/retention/
cancellation producers, mood energy voting, EmotionalSignal sentiment voting,
per-conversation mood/relationship scope, the mood-history rewrite question,
multi-user isolation, and any production routing change beyond the two already
made (canary display off; in-character turns production-only).

**Not started, awaiting approval:** Phase A merge, native shadow collection,
corpus build, any training.

---

## 6. Re-enabling the canary — the declared gate

Run-1c display routing goes back on for Scott only when **all four** hold:

1. the ported critical checks pass — **done**, 23 fixtures;
2. ordinary canary smoke tests pass — *not yet run against the live canary*;
3. roleplay turns demonstrably route to Stheno — **done in code**, needs live
   confirmation with the canary on;
4. diagnostics identify the displayed renderer and the reason — present today
   (`renderer.canary` verdict + reason), needs the in-character reason string
   confirmed live.

Two of four are done. The remaining two need the canary switched back on in a
controlled window, which I have not done and am not proposing until you say so.

---

**Status: restoration and R4 complete and live. Everything else is report only.
Awaiting approval.**
