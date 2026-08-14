# Choosing models for each role

Persistent_AI runs **eight** model roles. They have genuinely different jobs, and the right model
for "be Ava" is close to the worst model for "return strict JSON". This is the reasoning behind the
recommended roster, plus what to change if your hardware is tighter or roomier.

## Before any of that: the context window is not what you think it is

The chat model's context window is the setting most likely to break her, and the only one whose
failure is completely silent.

**A model's advertised length is not what it is served with.** Stheno is an 8192-token model.
Ollama loads it at **4096** unless told otherwise:

```bash
ollama ps
```

Read the `CONTEXT` column. That, not the model card, is the number that matters.

**You cannot ask for more from the client.** `num_ctx` sent to Ollama's OpenAI-compatible
`/v1/chat/completions` is accepted and ignored — verified both nested under `options` and at the top
level; the loaded context stayed at 4096 either way. Raising it means restarting the server:

```bash
OLLAMA_CONTEXT_LENGTH=8192 ollama serve
```

**Overflow is not a soft failure.** A prompt larger than the window is not rejected and does not
error. The server discards the excess *from the top* — where identity, the standing rules, and the
oldest turns live — and answers from what is left. Measured against this project's own config: a
~7,800-token prompt came back reporting `prompt_tokens: 2050`, with the entire system prompt gone,
and the model then denied fluently that it had ever been told the thing that was cut.

From the outside that is indistinguishable from the companion losing her memory and starting a new
conversation, because functionally that is what happened.

So tell the config the truth:

```jsonc
"Chat": {
  "ContextTokens": 4096,      // what `ollama ps` actually reports
  "ReplyReserveTokens": 1024  // room for her answer + tokenizer-estimate slack
}
```

The prompt budget is derived from these (`ContextTokens - ReplyReserveTokens`), and
`ContextPacketRenderer` drops sections lowest-value first to stay inside it — naming what it
dropped in the log. Identity, the standing rules, and the newest exchange are never among them.
Override the derived value with `Companion:PromptTokenBudget` only if you have a reason to.

If those warnings are routine, the answer is a bigger window, not a smaller companion.

## The one constraint that beats all others: VRAM and swap cost

On a single GPU, Ollama keeps recently-used models resident and evicts the rest. Every *distinct*
model in your config is a potential load/unload. A 5-second model swap in the middle of a turn is
far worse than a slightly-mismatched model that's already resident.

So the roster is designed around **three resident models**, not eight:

| Tier | Models resident | Roughly |
|---|---|---|
| **Conversation** | your RP 8B (Stheno) | ~6 GB at Q4 |
| **Utility** | one 7–8B instruct | ~5–6 GB at Q4 |
| **Fast** | one 3B instruct | ~2–3 GB at Q4 |
| Embeddings | nomic-embed-text | ~0.3 GB |

Roles are then assigned to whichever tier fits their job. That's the real design decision — not
"which model is best at task X" but "which of my three resident models should own task X".

## Recommended roster (12–16 GB VRAM — your current class)

```jsonc
"Chat":         { "Model": "hf.co/bartowski/L3-8B-Stheno-v3.2-GGUF:Q4_K_M", "Temperature": 0.8 },
"ToolPlanner":  { "Model": "qwen2.5:3b-instruct", "Temperature": 0.1, "TimeoutSeconds": 30 },
"Extraction":   { "Model": "qwen2.5:7b-instruct", "Temperature": 0.2 },
"Safety":       { "Model": "qwen2.5:7b-instruct", "Temperature": 0.0 },
"Reranker":     { "Model": "qwen2.5:3b-instruct", "Temperature": 0.0 },
"TaskAuditor":  { "Model": "qwen2.5:3b-instruct", "Temperature": 0.0 },
"Summarizer":   { "Model": "qwen2.5:7b-instruct", "Temperature": 0.3 },
"Embeddings":   { "Model": "nomic-embed-text", "Dimensions": 768 }
```

Three chat models resident (Stheno + Qwen 7B + Qwen 3B) plus embeddings. Compared to the previous
llama3.1:8b / llama3.2:3b split this is a like-for-like swap, not an extra load.

### Pull them before you start

Configuring a model does not install it. A name that Ollama doesn't have builds, starts, and passes
every test, then fails on your first sentence — so the API checks the provider's catalog at startup
and says exactly what's missing:

```
Model 'qwen2.5:7b-instruct' is configured for extraction, summarizer, safety but the server at
http://localhost:11434/v1 does not have it — those calls will fail until you pull it or correct
the configured name. Try: ollama pull qwen2.5:7b-instruct
```

The same verdict is on `GET /health` (`status` is `degraded` while anything is missing, with a
`modelCheck.missing` list), and missing models mark their capabilities `Unavailable` rather than
letting `/capabilities` claim something it has never checked. A provider it can't reach is reported
as *unverified*, never as missing.

To pull the whole recommended roster:

```bash
ollama pull hf.co/bartowski/L3-8B-Stheno-v3.2-GGUF:Q4_K_M && ollama pull qwen2.5:7b-instruct && ollama pull qwen2.5:3b-instruct && ollama pull nomic-embed-text
```

## Why each role gets what it gets

**Chat — the RP fine-tune, unchanged.** This is the one role where "instruction-following
benchmark score" is the *wrong* metric. Stheno's job is voice, warmth, and staying in character;
everything it was previously bad at (tool orchestration) has been moved off it. Temperature stays
high (0.8) because personality lives in the sampling.

**ToolPlanner — smallest, strictest, most JSON-reliable model you have.** It runs 1–2× per turn on
the critical path, and its entire output is one JSON object. Latency matters more than depth here,
and a 3B instruct at temp 0.1 is both faster and *more* reliable at this than an 8B RP model — as
we measured live (Stheno returned clean `{"tool": null}` and then confabulated). Qwen2.5-3B has the
best JSON discipline per gigabyte in this class; Llama-3.2-3B-Instruct is the fallback.

**Extraction — the most demanding structured job, give it the 7B.** It reads a whole exchange and
emits typed memory candidates with evidence. Errors here become *permanent wrong memories*, which is
the most expensive failure mode in the system. Worth the extra seconds — it runs after the reply, so
it costs no user-visible latency.

**Safety/privacy — accuracy over speed, share the 7B.** Output is one token (`SKIP`/`REMEMBER`), but
a false "REMEMBER" writes a secret to disk forever. Rules run first and catch credentials
deterministically; the model catches phrasing. Temperature 0.

**Reranker — the 3B is plenty.** It reorders ≤10 short candidate strings by relevance and returns
ids. Cheap, frequent, on the critical path, and there's a rule-based fallback if it fails, so a
smaller model's occasional miss is harmless.

**TaskAuditor — the 3B, and only when it runs.** Judges "did this reply finish?" on long outputs.
Small, binary, forgiving.

**Summarizer — 7B, background only.** Consolidation summaries become durable memory text you'll read
months later, so quality matters, but it runs during the sleep cycle where nobody's waiting.

**Embeddings — `nomic-embed-text` is the right default.** 768-dim, fast, strong at short-text
retrieval, and the whole memory system depends on it. **Changing this model invalidates every stored
vector** — all existing embeddings were produced by the old model and cosine between two different
models' vectors is meaningless. If you ever switch, re-embed everything. Upgrades worth the
migration: `mxbai-embed-large` (1024-dim, better recall) or `bge-m3` (multilingual, long context).
Set `Dimensions` to match.

## If you have 8 GB VRAM

Drop to **two** resident models: keep Stheno for Chat, and use `qwen2.5:3b-instruct` for
*everything else* (ToolPlanner, Extraction, Safety, Reranker, TaskAuditor, Summarizer). Extraction
quality drops, but the system stays responsive and nothing swaps. Prefer that trade — a swapping
config feels broken in a way a slightly-dumber extractor does not.

## If you have 24 GB+

Give Extraction and Safety a `qwen2.5:14b-instruct`, and consider a 12–13B RP model for Chat. Keep
the planner small regardless — it's a dispatcher, not a thinker, and a big planner just adds latency
to every turn.

## Fallback chains (what happens if you omit a block)

```
ToolPlanner → Extraction → Chat
Extraction  → Chat
Summarizer  → Chat
Reranker    → Summarizer → Chat
Safety      → Extraction → Chat
TaskAuditor → Summarizer → Chat
```

Omitting everything is valid: every role then runs on Chat. That works, but it's the configuration
that produced the original "she never uses her tools" behavior — the RP model doing executive work.

## Verifying your choices with real data

This is what the telemetry is for. After a day of normal use:

```
GET /diagnostics/models?hours=24
```

or the dashboard's **🔬 Diagnostics** tab. Per role+model you get calls, failures, average latency,
and token totals. Concrete things to look for:

- **`tool-planner` average latency > ~1.5s** → your planner model is too big, or it's being swapped
  out between turns. Try a smaller one, or share a model with another frequent role so it stays warm.
- **Any role with a non-zero failure count** → that model is failing to produce parseable output for
  its job. Check the fallback is doing its job, then downgrade the ambition or change the model.
- **`extraction` latency** is free (post-reply), so don't optimize it — optimize its *quality* by
  watching how many memories get accepted vs. rejected.
- **`conversation` rounds > 1 frequently** → auto-continuation is firing a lot; that's a
  `MaxTokens` problem, not a model problem.

Change one role at a time and compare windows. The whole point of the telemetry layer is that these
are measurements, not opinions.
