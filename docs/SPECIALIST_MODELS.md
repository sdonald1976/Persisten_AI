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

> **This was wrong, and Phase 4 measured it wrong.** (It is not the only one — Phase 5 retracted a
> claim too. Both retractions are left standing where they were written.) The argument below is
> sound about the *problem*
> — similarity genuinely cannot separate these cases — and wrong about the *fix*. An off-the-shelf
> MNLI model scores 0.462 against the heuristic's 0.667, because it asks whether two sentences
> describe the same scene, not whether both can be true of one person over time. Left in place
> rather than quietly edited, because a design document that only records the predictions that came
> true is worth nothing. See §6.


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
| 2 — shadow + evaluation | **built** — `IShadowRecorder`, `/diagnostics/shadow`, `tools/Companion.Eval`; **corpus capture** added, see §Capture |
| 3 — cross-encoder | **built, measured, NOT adopted** — see below |
| 4 — NLI | **built, measured, REJECTED for now** — and it disproved §3.2. See below |
| 5 — cognitive classifier | **corpus built, cross-validated, NOT adopted** — and it retracted a claim. See below |
| 6 — emotion | not started — GoEmotions (Apache-2.0) is the obvious start; see §The corpora that already exist |
| 7 — roaming seam | **built** — `IRoamingPolicy`, structured observation, ranked deliberation. No policy trained, and §Phase 7 says what actually blocks one |
| 8 — retirement | not started |

No model files are shipped or downloaded. Every specialist model is **disabled by default**, and the
companion starts, runs and passes its full suite with none present — that is the fallback
requirement, and it is tested rather than asserted.

### What Phase 2 found on its first run

```
decision       n=16  P=0.778 R=1.000 F1=0.875  FP=2  FN=0
assertion      n=16  P=0.875 R=1.000 F1=0.933  FP=1  FN=0
supersession   n=12  P=1.000 R=0.500 F1=0.667  FP=0  FN=3
```

Three things, none of which needed a model to learn:

1. **`DecisionDetector` had two false positives, and both were questions** — "have we decided on the
   database yet?" and "if we went with PostgreSQL, would that be simpler?". The same
   question-is-not-a-claim confusion that put fabricated facts in the memory store, in a different
   detector, unnoticed. Gating it on `AssertionGuard` took it to **F1 1.000**. That is the harness
   paying for itself before a single model exists, and it is the argument for building it first.
2. **The one remaining `assertion` miss is precisely the case NLI catches and mood cannot**: "I
   wouldn't say I've bought the timber yet" is a well-formed statement that does not entail the
   fact. Independent evidence for §3.3 — *add* an entailment veto, don't replace the mood check.
3. **Supersession's wording signal has perfect precision and 50% recall.** It never claims a
   replacement wrongly and misses half the real ones ("I'm on decaf"). Note what is being measured:
   the wording signal alone. In production, cardinality catches "I live in Cambridge now" and "it's
   Scott Donald" with no wording at all. So 0.667 is the floor of the half a model would replace,
   which is the honest thing to benchmark.

### Using it

```bash
dotnet run --project tools/Companion.Eval              # score every suite against its baseline
dotnet run --project tools/Companion.Eval -- --verbose # print the mistakes, not the successes
```

Non-zero exit when a suite falls below `Baselines.Floors`, so it can gate a change. The harness
references `Companion.Core` deliberately and scores the **real** heuristics — a reimplementation
would drift and end up flattering whatever replaced it.

Datasets are JSONL under `tools/Companion.Eval/datasets/`, each row tagged with where its label came
from: `real_conversation`, `human_reviewed`, `hard_negative`, `weakly_labelled`, `synthetic`. The
hard negatives are the ones that matter — rows sharing vocabulary with the opposite label, which is
what separates a model from a keyword list.

### Phase 3: the cross-encoder, measured and not adopted

`cross-encoder/ms-marco-MiniLM-L6-v2` (22M, **apache-2.0**, ONNX from the canonical repo — no
third-party mirror needed, so the licence is unambiguous). Fetch with `tools/get-models.ps1`;
weights are gitignored.

It works. It loads in the real app, is called on real turns, and costs **~25 ms per query** on the
CPU with no VRAM contention. Verified end-to-end: `/diagnostics/cognitive` shows
`available=true, calls=2, failures=0, avg 24.7ms` after a live conversation.

And on the resolution set it does not beat the thing it would replace:

```
keyword-overlap   n=12  P@1=0.917  R@3=1.000  MRR=0.958
cross-encoder     n=12  P@1=0.917  R@3=1.000  MRR=0.944
hybrid            n=12  P@1=0.917  R@3=1.000  MRR=0.944
```

The interesting part is *which* one each got wrong. Keyword missed **"the buoy one"** → picked
"Halyard, a C# service" over "the Marsh Lane marine sensor project" — the case it structurally
cannot do, since the two share no token, Jaccard scores zero, and the tie falls to whatever was
first. The cross-encoder got that right and missed a different one, **"the thing with the little
board"** → "the soil chemistry talk" over "the Jetson Nano build". The hybrid fixes keyword's miss
and inherits the model's, landing in the same place.

**So it stays off.** The rule is that a model replaces a heuristic on evidence, and "level on twelve
rows" is not that evidence — twelve rows is far too few for one case to mean anything, and the set
was written by hand, which biases it toward candidates that share vocabulary and therefore toward
Jaccard. That is the honest reading, and shipping it anyway because it is newer would be exactly the
failure this document exists to prevent.

What it is good for now is **shadow mode on real conversations**, which is a far better dataset than
twelve rows somebody wrote down. Two flags, deliberately separate:

- `Reranker:Enabled` — load the model, so it can be measured.
- `RerankMemories` — let it actually reorder retrieval.

Collapsing those into one is how a model gets promoted for the crime of being present.

**What would change the verdict:** a resolution set an order of magnitude larger, harvested from
`/diagnostics/shadow/disagreements` rather than invented; and a retrieval test at scale, which needs
a few hundred memories in the store, since today the retriever returns everything and there is
nothing to rerank.

### Phase 4: NLI, and the part of this document that was wrong

§3.2 argued that NLI on supersession was the strongest-evidenced item in the whole brief — the one
place the existing approach was *provably* unable to work. It was measured. It is worse.

`cross-encoder/nli-MiniLM2-L6-H768` (apache-2.0, ONNX, RoBERTa BPE), ~27 ms per call on CPU:

```
supersession        (wording heuristic)  P=1.000 R=0.500 F1=0.667
supersession-nli    (entailment model)   P=0.429 R=0.500 F1=0.462
```

Before blaming the model, the plumbing was ruled out. The C# byte-level BPE was verified token-for-
token against Hugging Face's own tokenizer (`RobertaTokenizationTests`), and the whole stack was
re-run through Python/onnxruntime, which produced **identical** probabilities. On canonical NLI the
model is excellent — entailment 0.97, contradiction 1.00, neutral 0.99 on the textbook triple. It
works. It is answering a different question from the one supersession asks.

**Why, and this is the useful part.** MNLI trains on premise/hypothesis pairs describing *the same
scene*, where two descriptions compete. Memory asks whether both can be true *of one person, over
time*. Those come apart badly:

| pair | needs | NLI says |
|---|---|---|
| corgi called Kanga / cat called Mim | coexist | **contradiction 1.00** |
| dislikes coriander / dislikes olives | coexist | **contradiction 0.92** |
| plays cello / plays piano | coexist | **contradiction 1.00** |
| soil-chemistry talk / irrigation rebuild | coexist | **contradiction 0.92** |
| coffee black / oat milk lattes now | replace | **neutral 0.82** |
| low-carb / keto | replace | **neutral 0.91** |

It is wrong in both directions, confidently, and precisely on the cases that matter. A person with
two pets is not a contradiction; someone who changed their coffee order is not a neutral remark.

**The assertion veto fails too, differently.** §3.3 proposed NLI as a *second* veto beside the mood
check. Measured, it cannot be the *first*:

```
"Did I ever tell you what timber I bought for the beds?"  ⊨ "The user bought timber."   entailment 0.97
"If I bought cedar for the beds, would it last longer?"   ⊨ "The user bought cedar."    entailment 0.68
"I wouldn't say I've bought the timber yet."              ⊨ "The user bought timber."   contradiction (0.23)
```

Given a question, it happily entails the presupposition at 97% — the exact fabrication that started
all this. It gets right only the one case mood cannot reach. So the composition §3.3 argued for is
confirmed and its *order* is now evidenced: mood first, structurally, and NLI only afterwards on
sentences already judged declarative. Not implemented, because on current data it would gain one row
in sixteen, and one row is not evidence.

**So NLI stays off, and it is not a failed experiment.** What it cost was an afternoon; what it
bought was knowing that the most confident recommendation in this document was wrong, before it was
wired into the path that retires memories. That is the entire purpose of building the harness first.

**What would change the verdict:** a fine-tune on supersession-framed pairs rather than MNLI —
"can both of these be true of this person?" instead of "do these describe the same scene?". The
brief anticipated this. It needs labelled data, which needs shadow mode running on real
conversations, which is the next thing to do rather than the next model to add.

### Phase 5: the cognitive classifier, and the second claim this document has had to withdraw

A corpus generator (`CognitiveCorpus`) produces labelled rows for four of the classifier-shaped
decisions — `memory.decision`, `memory.unfinished`, `companion.commitment`, `tool.capability` —
as templates crossed with fillers, with hard negatives written so the tempting answer is the wrong
one. Splits are drawn on the template **family**, never the row, because rows are the same sentence
several times and a row split scores memorisation.

The first run reported that `memory.unfinished` was **the first heuristic worth replacing**:

```
regex (incumbent)      P=1.000 R=0.438 F1=0.609
tf-idf + logreg        P=0.937 R=0.925 F1=0.931     (+0.322)
```

It is not, and the number is not reproducible in the sense that matters. The same code and seed,
scored on the ten *validation* families instead of the ten *test* families:

```
regex (incumbent)      F1 0.000
tf-idf + logreg        F1 0.595
```

The incumbent fires on nothing at all in one draw of ten families and on 44 % of rows in another.
Both numbers are correct; neither is about the method. **Ten families is not a sample**, and the
whole result was a property of which families the shuffle happened to put on which side.

This is the same failure the split rule was written to prevent, one level up. Splitting by family
instead of by row fixed leakage and left the sample-size problem completely untouched, and the
harness reported three decimal places either way. So the harness changed:

- **Grouped cross-validation** over every development family, each predicted exactly once by a
  model that never saw it — forty families of evidence rather than ten.
- **A paired bootstrap over families** on every difference, because "A scored higher than B" is not
  a finding and an interval that straddles zero is.
- **Family-macro as the primary metric**, in the Python trainer *and* in the shipped C# harness. A
  template carrying a `{when}` filler renders sixty rows where a bare one renders ten, so a
  row-weighted average was silently weighting phrasings by how many fillers somebody wrote.
- **The incumbent's answer is written into the corpus** by the C# generator, so the trainer scores
  the shipped rule rather than a Python transcription of it that can drift.

#### Read the table with the model in it

Before the numbers: **the "model" here is 1,113 parameters.** `TfidfVectorizer(analyzer="char_wb",
ngram_range=(3,5))` plus `LogisticRegression`, fitted on 550 synthetic rows. No neural network, no
pretraining, no knowledge of English beyond those rows — a bag of character substrings and a linear
boundary, which is roughly 1970s technology.

It is also structurally blind to the thing these judgements turn on. Measured, not asserted:

```
cosine("I have to do the roof", "the roof I have to do") = 1.000
```

Word order is not weighted lightly, it is **not represented at all** — both sentences are the same
point in feature space. Negation is a fact about structure, so "but I didn't" cannot reach the
classifier as anything except three more character trigrams.

The reason it is this and not a MiniLM is mechanical: the session that ran it had no route to
Hugging Face, so no encoder could be fetched to fine-tune. Phases 3 and 4 used real ones —
`ms-marco-MiniLM-L6-v2` (22M) and `nli-MiniLM2-L6-H768` — and those verdicts stand on real models.
This one does not.

So the table below says what 1,113 linear weights did. Read the losses accordingly.

| decision | union (regex OR model) | model alone |
|---|---|---|
| `memory.decision` | **−0.247** [−0.492, −0.073] — loses | −0.552 — loses |
| `memory.unfinished` | **+0.317** [+0.068, +0.558] — **beats** | +0.290 [−0.016, +0.565] — indistinguishable |
| `tool.capability` | −0.143 [−0.333, +0.000] — indistinguishable | −0.596 — loses |
| `companion.commitment` | 9 families — too few to cross-validate at all | |

**The swap is not supported. The composition is.** On the only decision where anything wins, the
model alone is indistinguishable from the regex and the union beats it. The retracted headline
measured a swap and claimed a win the swap does not have. §3.3 and §3.4 both argued for composition
over replacement on other grounds; this is the first time it has been measured, and it is the same
answer. That a linear model over character n-grams beats a hand-tuned regex at all is a statement
about how weak the incumbent is, and it survives whatever replaces the classifier.

**The two losses say much less.** They are evidence that *this* model cannot do those judgements,
and close to no evidence about learned models generally. Every error is the same error — word order
and negation:

```
said yes - closed:I thought I'd have to do {t} but I didn't
said yes - closed:we cancelled {t}
said yes - closed:would I need to do {t} first
```

which is precisely what a bag of character n-grams cannot represent. An earlier draft of this
section put those losses in a table and the caveat in prose underneath, which reads as a verdict on
the idea rather than on the model. It is not one.

**Winning the metric is still not permission to ship.** At a 3 % conversational base rate, which is
roughly what real traffic looks like, precision on `memory.unfinished` is `incumbent 0.103` against
`union 0.025`. Family-macro F1 treats a wrongly-fired negative family and a missed positive one as
equal; production does not. Open loops are surfaced unprompted, so a false positive is her asking
how work that does not exist is going, and a false negative is only silence. The union wins the
metric and would fabricate roughly four times as often.

**What would change the verdict.** Every error the model makes is one error — it cannot read tense,
negation or mood:

```
said yes - closed:I thought I'd have to do {t} but I didn't
said yes - closed:we cancelled {t}
said yes - closed:would I need to do {t} first
said yes - closed:{t} is finally sorted
```

Character n-grams over 750 rows cannot see any of that, and no threshold fixes it. That is a fact
about the model class, not about the idea: a sentence encoder is the thing that reads tense, which
makes MiniLM-or-similar the next experiment rather than the next regex. Alongside it, more
*families* — fold spread runs ±0.14 to ±0.31, wider than every gap being measured, and rows are not
the currency.

**Two incumbent defects found on the way**, neither of which needs a model: `DecisionDetector`
misses "I've chosen X" and fires on "everyone assumes we're going with X"; `ToolNudge` fires on
"are you able to come tomorrow" and misses "do you have access to the internet". Deliberately not
patched — adding four phrases to a regex so it scores better on a corpus written in this repo is
the treadmill the whole effort exists to leave, and it would quietly lower the bar a model has to
clear. Recorded so that fixing them stays a decision.

### Phase 7: the roaming seam, and the thing that actually blocks a learned policy

The brief asked for the seam and not the model: make `RoamingPolicy` replaceable, create structured
observations and actions, do not start on RL. That is what is built.

- **`RoamingObservation`** — everything a policy may see, in one value: the places the world just
  advertised, where she is, where she was, her spirits and energy, what is on her mind, how long she
  has been sitting, and the time. Seven positional parameters is not something a second
  implementation can be written against.
- **`RoamingDeliberation`** — every place scored and ranked, the move or `null` for stay, the reason
  either way, the threshold a move had to clear, and the margin it cleared it by. **The losers are
  kept deliberately**: two policies with the same top pick from completely different rankings have
  not agreed, and only the ranking tells those apart. Same reason retrieval reports what it excluded.
- **`IRoamingPolicy`** with `HeuristicRoamingPolicy` as the only implementation and the registered
  default. `RoamingPolicy.Choose` still exists and still runs the identical scoring, so the
  twenty-one existing roaming tests were not touched — a refactor whose own tests had to be
  rewritten has not been shown to preserve anything.
- Staying now carries a reason. "Why is she still in the study?" is asked at least as often as "why
  did she move", and it was the one outcome that left no record.

**Two things are deliberately outside the seam, and the second is the interesting one.**

*Concerns never reach a policy.* If something in the world needs doing, the worker acts on it before
asking where she would rather be. That is not an oversight: feeding concerns in as ordinary
preoccupations made a stove going cold score 0.5 against the study's 0.4 — a gap under the move
threshold — so she sat and read while the fire went out. A need is not a preference. Models judge
where she would like to be; code decides that something needing doing outranks it.

*The observation contains only what the caller can actually supply.* The brief listed user presence,
recent experiences, novelty, environment state, social state. None is gathered today, and a field
that is always null is worse than a missing one — it reads as available, gets consumed, and quietly
means nothing. Adding any of them is a change to what the world worker gathers, which is different
work from making the policy replaceable.

**And the part worth saying plainly: the seam was never what blocked a learned policy.** It is that
**nothing in this system says a roam was good.** There is no reward — no signal that being in the
greenhouse at four o'clock was better than being in the study, and no way to derive one from what is
recorded. Reinforcement learning without a reward is not a hard problem, it is not a problem. A
policy trained today could only imitate the rule it replaced, at greater cost and with less
explanation, and it would pass any test that compared it to the rule.

So the honest next step for Phase 7 is not a model and not more architecture. It is **a reward or a
preference signal**: a way for a person to say "that was a good place to be" or "you've been in
there all day", or an observable consequence the companion can be scored against. Until one exists,
the heuristic is not a placeholder — it is the correct implementation, because it is the only one
that can explain itself.

### The corpora that already exist, and the fact that nobody looked

Every verdict above is qualified by the same sentence: *the corpus is synthetic and one person wrote
it.* That was treated for several sessions as a fact about the world. It is a fact about nobody
having checked whether these judgements have names in the literature. Most of them do, and several
have annotated corpora that match far more precisely than a template generator ever will.

| decision here | public corpus | size | what it is | licence |
|---|---|---|---|---|
| `FactSupersession` | **DialogueNLI** (Welleck et al. 2019) | ~310k pairs | persona sentences labelled entailment / neutral / **contradiction** | unconfirmed |
| `AssertionGuard` | **CommitmentBank** (de Marneffe et al.) | 1,200 discourses | speaker commitment to an embedded clause under question / modal / negation / conditional | unconfirmed (CB itself CC-BY) |
| `tool.capability` | **CLINC150** | 22.5k utterances | 150 intents over 10 domains **plus a real out-of-scope class** | unconfirmed (CC BY-SA 3.0 per UCI) |
| `CommitmentDetector` | **DailyDialog** | 13,118 dialogues | per-utterance acts incl. **commissive** | **CC BY-NC-SA 4.0 — non-commercial, ShareAlike** |
| `MoodDetector` | **GoEmotions** | 58k comments | 27 multi-label emotions | Apache-2.0 |
| long-term persona change | **Multi-Session Chat** (Xu et al. 2022) | 5-session dialogues | persona carried and revised across sessions | unconfirmed |

Two of those are not "roughly relevant". They are the exact problem.

**DialogueNLI is much closer to the question Phase 4 measured MNLI failing — and one step of that
is still unverified.** §Phase 4 concluded that MNLI asks whether two sentences describe the same
scene, while memory asks whether both can be true *of one person*, and recorded these failures:

```
corgi called Kanga / cat called Mim     needs coexist    MNLI said contradiction 1.00
plays cello / plays piano               needs coexist    MNLI said contradiction 1.00
```

DialogueNLI is built from PersonaChat personas and labelled from human-annotated **relation
triples** — `(i, have_pet, dog)` — where contradiction is assigned via an explicitly *negating*
triple such as `(i, not_have, dog)`, and pairs across *different* relations (`have_pet` against
`have_vehicle`) are neutral by rule. That is person-level coherence rather than scene identity,
which is the right axis and the one MNLI was measured getting wrong. 310,000 pairs of it, public
since 2019.

**What is not confirmed is the case the argument rests on: same relation, different value.** "I have
a corgi" against "I have a cat" is one `have_pet` triple against another, and the published rules do
not say whether `have_pet` is treated as many-valued. If those pairs are neutral, this corpus
answers our question. If they are contradiction, it shares MNLI's problem exactly and only its
negation-derived rows are usable. An earlier draft of this section asserted the first outcome; it
was a recollection of the annotation scheme, not a reading of the data.

Five minutes of arithmetic over the real file settles it, so that is now a command rather than a
belief:

```bash
python training/datasets/fetch.py dialogue-nli --audit
```

It cross-tabulates the corpus's own relation pairs against its own labels and says which of the two
worlds we are in. **Run it before the fine-tune, not after.**

The reordering still holds either way, because it does not depend on that answer: "we would need a
fine-tune on supersession-framed pairs, which needs labelled data, which needs capture running on
real conversations" was wrong about the last clause. Person-level entailment data exists in public
at scale, and none of it had to be waited for.

**CommitmentBank is AssertionGuard, itemised by someone else.** 1,200 naturally occurring discourses
whose final sentence puts a clause-embedding predicate under an *entailment-cancelling operator* — a
question, a modal, a negation, or the antecedent of a conditional — with human ratings of how
committed the speaker is to the embedded clause. The three failures recorded in §Phase 4 are three
of those four environments:

```
"Did I ever tell you what timber I bought?"   question      NLI wrongly entailed at 0.97
"If I bought cedar, would it last longer?"    conditional   wrongly entailed at 0.68
"I wouldn't say I've bought the timber yet"   negation      the case sentence mood cannot reach
```

This does not retire capture — real sentences from *this* companion are still the only thing that
measures its base rate, and no public corpus contains her user. But it reorders the queue: **the NLI
fine-tune can start now**, and does not need to wait behind months of conversation.

#### How they are wired in

`training/datasets/adapters.py` maps each corpus to the row shape everything else already reads, so
a borrowed row, a generated row and a harvested row are the same row and go through the same grouped
cross-validation and the same paired bootstrap.

```bash
python training/datasets/fetch.py --list          # the register, with licences
python training/datasets/fetch.py --probe         # which repository ids actually resolve
python training/datasets/fetch.py dialogue-nli    # -> corpus/memory.supersession.borrowed.jsonl
python training/cognition/crossval.py             # same metric, now on real data
```

**What actually resolves**, from a real `--probe` run rather than a guess:

| corpus | id that loads | columns |
|---|---|---|
| `dialogue-nli` | `pietrolesci/dialogue_nli` | `dtype, id, label, original_label, sentence1, sentence2, triple1, triple2` |
| `commitment-bank` | `aps/super_glue/cb` | `premise, hypothesis, label, idx` |
| `clinc150` | `clinc/clinc_oos/plus` | `text, intent` |
| `daily-dialog` | **none** | both known ids are script-based and fail on `datasets` ≥ 4.5 |

DailyDialog is the one to go without if it stays broken — it is also the one with the awkward
licence, and it feeds only the *detection* half of a judgement whose gate stays code regardless.

The DialogueNLI mirror carries `original_label` beside an int64 `label` that has **no ClassLabel
metadata**, so nothing can be read off the schema and the string column is the only thing that
states what an id means. The adapter prefers it, and still refuses to decode a bare integer without
names — a mirror that ordered its classes differently would silently swap entailment and
contradiction.

#### The audit, and two ways it was wrong before it worked

`fetch.py dialogue-nli --audit` was built to settle whether the corpus treats *same relation,
different value* — "I have a corgi" against "I have a cat" — as neutral or as contradiction. Its
first run on real data produced a confident verdict that was worth nothing, twice over:

1. It compared labels against the string `"neutral"` while the mirror stores integers, so the count
   was always zero and it printed "mostly NOT neutral" **whatever the data said**. A verdict that
   cannot come out the other way is not a measurement.
2. It bucketed on the **relation alone**, which conflates two unrelated cases. A pair sharing the
   *same triple* is an entailment by construction — that is how the corpus makes its positives —
   and it landed in the same bucket as the case in question. Of the 192,337 same-relation pairs,
   52 % are entailment and 46 % contradiction, and that split is almost certainly same-value against
   everything else rather than the answer to anything.

It now buckets on same-value / different-value / different-relation, decodes through
`original_label`, withholds the verdict entirely when labels cannot be decoded, and carries the
different-relation bucket as a **control**: the paper labels relation swaps neutral by construction,
so if that bucket is not overwhelmingly neutral the triples are being misread and nothing else in
the output should be believed. All four branches are tested against fixtures.

One thing the first run did establish, because it can be derived rather than assumed: different-
relation pairs came out `1` at 81 %, and the paper labels those neutral by rule, so **`1` = neutral**
— which agrees with HF's conventional NLI ordering `[entailment, neutral, contradiction]`.

**So the question is still open**, and re-running `--audit` after a pull answers it. That is a
better position than the previous one, which was an answer nobody should have trusted.

Three things were worth being careful about, and two of them are lessons this project already paid
for:

- **The group key.** Real corpora bring an invisible version of the leakage trap: the same persona
  sentence appears in hundreds of DialogueNLI pairs, and one CLINC intent has a hundred paraphrases.
  Splitting either by row measures memorisation, exactly as the template-filler split did. Every
  adapter declares a `family` explicitly, and it means "what must not appear on both sides".
- **The incumbent has no verdict on borrowed rows**, and counting an absent verdict as "said no"
  would credit the regex with perfect precision on rows it was never run over — flattering precisely
  the thing under test. `crossval.py` now says how many rows are in that state.
- **Schema drift fails loudly.** These adapters were written from published descriptions, not
  against the files. A renamed column would otherwise produce an all-negative corpus, which trains a
  model that says no to everything, scores 97 % accuracy, and is discovered weeks later.

`training/datasets/test_adapters.py` runs offline with no network and no `datasets` install, and it
earned that separation immediately: it caught an inverted label in the CommitmentBank adapter, where
inferring the encoding from the value read SuperGLUE's `0` (**entailment** — fully committed) as a
Likert `0.0` (**undecided**). A bare number cannot be disambiguated, so it is no longer guessed —
the caller is asked.

**The downloads themselves are unverified**, because the session that wrote this had no route to
Hugging Face. The mapping is tested; the fetching is not. Expect the first run to need a fix, and to
tell you which one.

### Capture: the way out of the deadlock

Every verdict above ends in the same place. The reranker needs "a resolution set an order of
magnitude larger, harvested from real conversations rather than invented". NLI needs "a fine-tune on
supersession-framed pairs, which needs labelled data, which needs shadow mode running on real
conversations". The classifier needs families that were not all written by one person. Three
different models, one blocker.

And shadow mode cannot collect it, because shadow mode needs a model to compare against. That is
the deadlock: no data, so no model; no model, so nothing to shadow; nothing to shadow, so no data.

**Capture breaks it by recording the half that already exists.** `CognitiveModels:Capture` writes
down what each heuristic said about each real sentence — every message, including the ones where
the answer is no. No model runs. Nothing about the turn changes.

```jsonc
"CognitiveModels": { "Capture": true }     // off by default; separate flag from ShadowMode
```

- `GET /diagnostics/shadow/captures?subject=&count=` — the rows.
- `python training/cognition/harvest.py --url http://localhost:5000` — writes a review queue per
  decision under `training/corpus/<decision>.captured.jsonl`, with `label: null`.
- Label them, save as `<decision>.reviewed.jsonl`, and `crossval.py` folds them into the
  development set and reports what fraction of the corpus is finally real.

Four decisions are captured, and the subjects are deliberately **the same strings the generated
corpus uses** — `memory.decision`, `memory.unfinished`, `tool.capability`, `companion.commitment`
— so a captured row and a generated row are the same row about the same judgement and can be
trained on together. A near-miss like `unfinished` against `memory.unfinished` would look correct
in both files and silently produce two datasets, so it is asserted in a test rather than left to
care.

**The heuristic's verdict is a weak label, not a label.** Training on it directly teaches a model
to imitate the regex including its misses, which for `memory.unfinished` means learning to miss
five cases in six. Its value is that it sorts the queue. `label` comes out null and a human fills
it in; there is no way round that, because a corpus labelled by the rule it is meant to judge can
only ever conclude that the rule was right.

**What it is allowed to write.** Capture stores user text, which the rest of the telemetry
deliberately avoids, so it is bounded three ways:

- It runs **inside the same gate as memory extraction** — not a private conversation, not an
  in-character one, not one marked "don't remember", extraction enabled. A sentence she was asked
  to forget is not training data either, and "we won't remember this, except in the telemetry
  table" is not a promise anyone would accept written down that way.
- `SecretDetector` runs on every captured sentence, and a hit **drops the text and keeps the
  verdict** — the verdict rather than nothing, because skipping the row would bias the one number
  this is best placed to produce. Running it live showed where that check actually matters, which
  is not where it was written for: `RuleBasedPrivacyClassifier` already calls the same
  `SecretDetector`, so a *user message* containing a key makes the whole turn non-rememberable and
  never reaches capture. On *her reply* nothing else looks, and a key quoted back out of a tool
  result is caught here or not at all.
- It is **off unless switched on**, and switching on `ShadowMode` does not switch it on. Different
  costs, different decisions.

**The number worth having first** is not a model at all. It is the rate each heuristic fires at on
real traffic. Every precision figure in this document assumes a 3 % conversational base rate
because nothing measured one, and precision is the metric that moves when the base rate does — at
3 %, `memory.unfinished` scores 0.103 for the incumbent and 0.025 for the union that beats it on
F1. `harvest.py` prints that column. If it says something other than 3 %, several conclusions above
are wrong by a factor nobody has calculated yet.

**Read that rate with its denominator, which is not "all turns".** Capture runs inside the
extraction gate, so the population is *turns allowed to produce durable memory*. For
`memory.unfinished`, `memory.decision` and `companion.commitment` that is exactly right — those
detectors only run on such turns anyway, so the captured rate is the rate that matters. For
`tool.capability` it is not: `ToolNudge` runs in the tool loop on **every** turn, private ones
included, so its captured rate is measured over a narrower population than it actually sees. The
alternative is capturing verdicts about private messages, which is a bigger change to what this
promises than a more accurate denominator is worth. Recorded rather than fixed.

**End-to-end, on a live instance** (`Models:Provider=Mock`, `CognitiveModels:Capture=true`), four
ordinary turns produced exactly the verdicts they should:

```
memory.unfinished   true   "I still need to finish the shed roof."
memory.decision     true   "We have decided to use SQLite in the end."
tool.capability     true   "What can you actually do?"
                    false  "The weather is lovely today."   (all three)
```

and a fifth turn containing an `sk-…` key produced **no capture rows at all** — the privacy gate,
not the redaction, which is how the scoping above came to be written down.

### Shadow mode

Off by default. `CognitiveModels:ShadowMode: true` runs a model beside the heuristic it might
replace and records both, **changing nothing** — the production answer stays the heuristic's, and
each row records `Applied: "legacy"` explicitly rather than leaving it to be inferred from a config
flag whose value at the time isn't in the row.

- `GET /diagnostics/shadow` — agreement rate, average confidence and added latency per subject.
- `GET /diagnostics/shadow/disagreements?subject=&count=` — the cases worth a human deciding.

**Wired call sites:** `supersession.replaces` only, in `MemoryPipeline` — the wording signal against
NLI's contradiction verdict, on every real turn. Worth saying plainly that the first version of this
shipped with *none*: the recorder, the endpoints and the helper all existed and nothing called them,
so switching shadow mode on recorded an empty table. A measurement feature that is never invoked
looks exactly like one that finds no disagreements.

When it is off, the model is not merely ignored, it is **not run**: shadowing costs a real inference
per turn, and paying for an answer nobody reads is how a measurement feature becomes a latency
regression. Input text is only stored when a caller passes it, because the rest of the telemetry
deliberately holds sizes and outcomes rather than content so it can be kept for weeks without
becoming a second, unguarded conversation store.

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
