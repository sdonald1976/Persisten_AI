# Specialist models

**Models make judgments. Code enforces guarantees.**

This document is the audit, the plan, and the record of what has actually been built. It is written
against the real code, not against an idea of it, and it says where I disagree with the brief.

---

## 1. Why

The companion contains a lot of hand-written approximations of understanding: regex over phrasing,
keyword lists, Jaccard token overlap, hand-tuned score weights. Every one of them was written after
a real conversation went wrong, and each is a reasonable guess about English that will be wrong
again on the next sentence nobody predicted.

Two things follow, and they pull in opposite directions:

- Small encoder models make these judgments better than phrase lists, because they generalize.
- A model that can be *wrong* must never be the thing that *destroys* data.

So the split is not "models good, rules bad". It is: a model may propose a judgment; deterministic
code decides what is allowed to happen because of it. That is already how memory extraction works
(the model proposes, `MemoryPipeline` disposes), and it is the pattern to extend rather than replace.

---

## 2. Audit

Everything below is in the codebase today. The classification column is the recommendation, not a
statement of what has been done — see §6 for status.

### 2a. The single biggest item: `ScoreMath.KeywordOverlap`

`KeywordOverlap` is Jaccard over `Tokenizer` — lowercase, split on non-alphanumerics, drop a
50-word stopword list, apply "a tiny suffix stemmer". It is used in **nine** places:

| Caller | What it decides |
|---|---|
| `Retriever` | one of seven weighted retrieval signals |
| `EntityResolver` | which project a reference means |
| `Agent.RankMemoriesAsync` | which memory `forget`/`that's wrong` targets |
| `Agent.RecallAsync` | which memories a topic question is about |
| `ProcedureStore` | which procedure matches a query |
| `AssociativeRecallService` | which memory a thought associates to |
| `AttentionService` (×2) | what is currently on her mind |
| `ProjectContextService` | which open loops are relevant |
| `Reflector` (×2) | which memories support a reflection |

This is the highest-leverage target in the solution, and not for the reason the brief gives. It is
not that reranking memories is the most valuable single win — it is that **one cross-encoder behind
one interface improves nine subsystems at once**, and every one of them is a "how related are these
two pieces of text" question, which is exactly what a cross-encoder is for.

Jaccard cannot see that "the buoy one" refers to "Marsh Lane marine sensor project". No amount of
weight tuning fixes that. **Classification: 4 (cross-encoder), with a deterministic floor retained.**

### 2b. Detectors and classifiers

| System | What it does now | Class | Note |
|---|---|---|---|
| `RuleBasedIntentParser` | ~15 anchored regexes → `IntentKind` | **2 classifier** | Keep regex for `/slash` commands — those are protocol, not language. |
| `MoodDetector` | word lists → valence/arousal | **2 classifier** (emotion) | Best-suited item in the brief. Low risk: nothing destructive depends on it. |
| `DecisionDetector` | "we decided/settled on/going with" + filler list | **2 classifier** | |
| `CommitmentDetector` | "I'll/I'm going to" + **capability allow-list** | **8 hybrid** | The *detection* is semantic; the allow-list is a policy question ("can she actually do this?") and must stay code. See §4. |
| `AnticipationDetector` | future-event phrasing | **2 classifier** | |
| `UnfinishedWorkDetector` | obligation phrasing | **2 classifier** | Written as a backstop; a classifier is strictly better at it. |
| `InCharacterDetector` | roleplay markers | **2 classifier** | |
| `RuleBasedPrivacyClassifier` | 22 lines of keywords | **8 hybrid** | Must sit *above* `SecretDetector`, never replace it. |
| `AssertionGuard` | sentence mood + clause splitting | **8 hybrid** | See §4 — I disagree with a straight NLI swap. |
| `FactSupersession` | cardinality + replacement phrases | **3 NLI** + code | Highest-value NLI target. See §3. |
| `CompletionSignals` | deliverable verbs, sign-off shapes | **1 keep** | About the *shape* of a reply, not its meaning. Cheap, runs every turn. |
| `SecretDetector` | credential formats | **1 keep — never replace** | §5. |
| `PromptEchoFilter`, `ReasoningFilter`, `TextRepetition` | strip model artifacts | **1 keep** | Structural string surgery. |
| `MemoryNormalizer`, `PredicateVocabulary` | keys and vocabulary | **1 keep** | Identity, not judgment. |
| `RoamingPolicy` | weighted movement scoring | **7 learned policy, later** | Create the seam; do not train. §4. |
| `ToolNudge` | phrases that suggest a lookup | **2 classifier** | Folds into the cognitive classifier's `tool_request`. |
| `ClarificationResolver` | "the first one" / "never mind" | **8 hybrid** | Ordinals and "never mind" are deterministic; *which candidate* is semantic. |
| `EntityResolver` | keyword + embedding + alias | **4 cross-encoder** | Feeds directly off §2a. |
| `LlmMemoryReranker` | a 3B generative model scores relevance | **4 cross-encoder** | A generative model doing a scoring job — the clearest case of the brief's "don't use a 7B for what a 70M does better". |

### 2c. Seams that already exist

Worth knowing before building anything: `IMemoryReranker` already has **two** implementations
(`RuleBasedMemoryReranker`, `LlmMemoryReranker`) selected by configuration. A cross-encoder is a
third implementation behind an interface that is already there, already injected, already tested.
The brief's suggested `IMemoryReranker → CrossEncoderMemoryReranker` is not a refactor; it is an
addition. Same for `IPrivacyClassifier` and `IIntentParser`.

---

## 3. Where I disagree with the brief

The brief invited challenge. Four points, each with evidence from this codebase.

### 3.1 The reranker should not be first for the reason given — but it should still be first

The brief's Phase 2 rationale is that reranking is isolated and validates the architecture. True.
But on today's evidence a reranker will show **no measurable retrieval benefit yet**: with 7
memories the retriever returns all of them, every score lands between 0.94 and 2.15, and the packet
sits at ~1,650 tokens against a 3,072 budget. There is nothing to rerank. Measured on a real
conversation, the relevance floor of 0.15 filters nothing at all.

It should still be first — because of §2a. Nine subsystems share `KeywordOverlap`, and the ones that
will show a difference immediately are **entity resolution** and **dispute targeting**, not memory
ranking. Those are places where the wrong answer is currently visible and damaging: a keyword tie of
0.200 vs 0.167 is what decides which memory "that's wrong" flags.

So: build it first, but evaluate it on entity resolution and reference targeting, and expect memory
reranking to prove itself only once the store is large. Seed a few hundred memories to test that.

### 3.2 NLI for supersession is the strongest-evidenced item in the brief and should move up

I measured this today. On nomic-embed-text:

```
0.763  coffee black → oat milk lattes   (MUST replace)
0.753  dislikes coriander vs olives     (MUST coexist)
0.729  irrigation vs raised beds        (MUST coexist)
```

There is no threshold in that ordering. Embedding similarity is structurally incapable of this
distinction, which is why the current rule falls back to reading the user's phrasing for "actually"
and "no longer" — a phrase list that will miss every unanticipated way of saying it.

NLI answers exactly the right question. "The user drinks their coffee black" vs "The user drinks oat
milk lattes now" is a *contradiction*; "dislikes coriander" vs "dislikes olives" is *neutral*. This
is the one place in the brief where the current approach is not merely crude but provably unable to
work, and where a specialist model does something the alternative cannot.

**Recommendation: NLI moves to Phase 3, ahead of the cognitive classifier.**

The hard constraint stays: NLI may propose `contradiction`; only `MemoryCurator` supersedes, only
with user evidence, only with a revision record, and never destructively.

### 3.3 `AssertionGuard` should keep its deterministic core

The brief proposes replacing its "question/conditional/token-overlap logic" with entailment. I half
agree, and the half matters.

`AssertionGuard` is a **veto**, not a decision: it can only refuse a candidate the extractor already
proposed. Its test is sentence mood — is this clause a statement? — which is a structural property
of English, not a semantic judgment, and it is right for the same reason a parser is right.

NLI answers a genuinely different and better question: *does this message entail this fact?* That
catches cases mood cannot, e.g. "I wouldn't say I bought cedar" — a declarative sentence that does
not entail the fact. So NLI should be **added as a second veto**, not swapped in. Two independent
refusals, both cheap, either sufficient. Removing the mood check would trade a guarantee for a
probability on the path where a false positive writes a fabricated fact into permanent memory.

### 3.4 One cognitive classifier is right; folding `CommitmentDetector` into it is not

Multi-label is the correct shape and `"I'm actually going with the smaller Jetson"` is a good example
of why. But `CommitmentDetector` no longer answers "is this a commitment?" It answers **"is this a
promise she is capable of keeping?"** — the allow-list exists because the model fabricated "I'll have
some space set aside for experimental varieties" about a garden she does not have, and that became a
durable open loop. A classifier will happily label that `commitment = 0.97`, correctly, and it would
still be wrong to store.

So: classify the commitment with the model; gate it on capability with code. Hybrid, and the gate is
the part that matters.

### 3.5 A hardware note

The GPU is a 6 GB GTX 1660 already thrashing between five Ollama models; turns take 50–135s. ONNX
inference for 70M-class encoders belongs on **CPU** — single-digit milliseconds, no VRAM contention.
The runtime must not silently grab a GPU execution provider.

---

## 4. What stays deterministic, permanently

Not "not yet" — these should never become probabilistic:

- **`SecretDetector`.** API-key shapes, `-----BEGIN PRIVATE KEY-----`, `ghp_`, `AKIA`. A semantic
  classifier may run *alongside* it for "the password I use for my router", never instead.
- **Auth, CORS, loopback binding, user-scoped queries, FK constraints, migrations/backup.**
- **Schema validation and structured-output parsing** — including `ExtractionSchema` and the
  balanced-bracket parser.
- **`CommitmentDetector`'s capability gate**, per §3.4.
- **`MemoryCurator`'s persistence rules.** No model deletes, supersedes or disputes anything.
- **Slash-command parsing, ordinals, "never mind"** — protocol, not language.
- **`MemoryNormalizer` / `PredicateVocabulary`** — identity keys.
- **The prompt-token budget and trimming order.**

---

## 5. Revised phase order

| Phase | What | Why here |
|---|---|---|
| **1** | ONNX runtime seam: `ICognitiveModel`, options, DI, singleton lifetime, CPU provider, timeout, cancellation, diagnostics, **fallback when no model is present** | Everything needs it, and it is uncontroversial |
| **2** | Shadow-mode recording + evaluation harness | Must exist *before* the first model, or the first model gets adopted on vibes |
| **3** | Cross-encoder behind `IMemoryReranker`; then `EntityResolver` and reference targeting | §2a — one model, nine callers |
| **4** | NLI: `FactSupersession` first, `AssertionGuard` as a second veto | §3.2 — provably impossible for the current approach |
| **5** | Multi-label cognitive classifier, shadowing the detectors | Broadest, but needs §2 to prove itself |
| **6** | Emotion classifier → `MoodDetector` | Low risk, nothing destructive depends on it |
| **7** | `RoamingPolicy` observation/action seam | Seam only. No RL. |
| **8** | Retire heuristics that have been measured as superseded | Evidence, not vibes |

Phases 1 and 2 are swapped relative to the brief, deliberately: the brief puts shadow mode at §10
and evaluation at §16, after several models are in. Building the measurement first is the only way
"it replaces it when we have evidence it performs better" can actually be honoured.

---

## 6. Status

| Phase | Status |
|---|---|
| 1 — runtime seam | **built** — see `src/Companion.Core/Abstractions/ICognitiveModel.cs`, `Companion.Infrastructure/Cognition/` |
| 2 — shadow + evaluation | not started |
| 3 — cross-encoder | not started |
| 4 — NLI | not started |
| 5 — cognitive classifier | not started |
| 6 — emotion | not started |
| 7 — roaming seam | not started |
| 8 — retirement | not started |

No model files are shipped or downloaded. Every specialist model is **disabled by default**, and the
companion starts, runs and passes its full suite with none present — that is the fallback
requirement, and it is tested rather than asserted.

---

## 7. Models under consideration

Nothing here has been benchmarked yet. Licences recorded now so we do not discover a problem later.

| Role | Candidate | Licence | Notes |
|---|---|---|---|
| Cross-encoder | `cross-encoder/ms-marco-MiniLM-L-6-v2` | Apache-2.0 | 22M, the standard baseline |
| Cross-encoder | `BAAI/bge-reranker-v2-m3` | Apache-2.0 | Much stronger, much bigger (568M) — benchmark before assuming |
| NLI | `MoritzLaurer/DeBERTa-v3-base-mnli-fever-anli` | MIT | 184M |
| NLI | `cross-encoder/nli-deberta-v3-small` | Apache-2.0 | 142M, faster |
| Emotion | `SamLowe/roberta-base-go_emotions` | MIT | 28 multi-label emotions |
| Classifier base | `sentence-transformers/all-MiniLM-L6-v2` + SetFit head | Apache-2.0 | SetFit needs far fewer labels |

ONNX-exported mirrors under the `Xenova/*` namespace (Apache-2.0) avoid needing PyTorch locally;
`optimum` export is the alternative and is documented in `training/` when we get there.

**Rule: no model is adopted on reputation. It is adopted on a measured win against the regression
set, and the regression set comes from real conversations.**

---

## 8. Setup (when models are enabled)

Not required to run the companion. Only needed to turn a specialist model on.

```jsonc
"CognitiveModels": {
  "Directory": "models",              // relative to the database, or absolute
  "Reranker":   { "Enabled": false, "Path": "reranker.onnx",   "Threshold": 0.0 },
  "Nli":        { "Enabled": false, "Path": "nli.onnx",        "Threshold": 0.6 },
  "Classifier": { "Enabled": false, "Path": "classifier.onnx", "Threshold": 0.5 },
  "Emotion":    { "Enabled": false, "Path": "emotion.onnx",    "Threshold": 0.3 }
}
```

A model configured `Enabled: true` whose file is missing is a **startup warning and a fallback to
the legacy implementation**, not a crash — unless `Required: true`, which is how you say "I would
rather know immediately".
