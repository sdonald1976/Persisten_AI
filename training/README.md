# Fine-tuning (experimental, optional)

Turn your companion's own data into a small **LoRA fine-tune** and load it back into Ollama.
This is entirely optional and runs **outside** the app — nothing here is automatic, and the
chat loop never trains or swaps models on its own.

## Read this first — what to fine-tune, and what not to

- **Facts about you stay in memory, never in weights.** Memory is editable, forgettable
  (`/forget`), and provenance-tracked. Weights are none of those — baking "the user likes X"
  into a model means you can't cleanly correct or forget it, which breaks the guarantees the
  rest of this project is built on. So fine-tune **behavior/format**, not facts.
- **Best first target: the extraction model.** Your pipeline already generated validated,
  evidence-backed `(message → memory JSON)` pairs during normal use. Teaching a small model to
  reproduce that JSON is high-value, low-risk, and needs no thumbs-up feedback.
- **Don't auto-train or auto-promote.** Continuous unattended fine-tuning drifts and forgets;
  auto-promoting a regressed model silently ruins the companion. Keep a human in the loop.
- **Never train on unfiltered model output** (that's how models collapse). The extraction
  dataset is built from *validated* memories, not raw generations — good. The `chat-sft`
  dataset is raw and experimental; prefer extraction until you capture reply ratings.

## The workflow

```
companion.db ──build_dataset.py──▶ data/*.jsonl ──finetune.py──▶ merged GGUF
                                                                     │
                                          ollama create -f Modelfile ▼
                                                              new model tag
                                                                     │
                                    evaluate (below) ── promote ─────▼ appsettings.json
```

### 1. Build the dataset (no GPU needed; stdlib only)
```bash
python build_dataset.py --db /path/to/companion.db --out data/extraction.jsonl
# experimental raw chat pairs:
# python build_dataset.py --db companion.db --dataset chat-sft --out data/chat_sft.jsonl
```
Deleted memories are skipped, so you never train on anything you told it to forget.

### 2. Fine-tune (GPU box)
```bash
python -m venv .venv && source .venv/bin/activate
pip install -r requirements.txt
python finetune.py --data data/extraction.jsonl \
    --base unsloth/Llama-3.2-3B-Instruct --out outputs/companion-extractor --epochs 2
```
Produces a **merged GGUF** under `outputs/companion-extractor/`.
(Apple Silicon: Unsloth isn't supported — use `mlx_lm.lora` to train, then convert to GGUF.)

### 3. Load into Ollama
```bash
cp Modelfile.example Modelfile     # FROM already points at the exported GGUF
ollama create companion-extractor -f Modelfile
```

### 4. Evaluate BEFORE promoting
Point a **copy** of your config at the new model and run the benchmark that already ships with
this repo:
```bash
dotnet test --filter "FullyQualifiedName~ScenarioTests"
```
Also spot-check extraction quality with `/why` on a few real turns. Only if it's as good or
better do you promote.

### 5. Promote (and roll back)
In `appsettings.json`, set the role to the new tag:
```jsonc
"Extraction": { "Model": "companion-extractor", "Temperature": 0.2 }
```
Rollback is just changing that line back — the old model is untouched. Keep versioned tags
(`companion-extractor-v2`, …) so you can A/B and revert instantly.

## Style fine-tuning from your feedback
The app records reply ratings (say "that was great" / "that was unhelpful"), so you can train
**style** from replies you approved:
```bash
python build_dataset.py --db companion.db --dataset feedback-sft --out data/style.jsonl
python finetune.py --data data/style.jsonl --base unsloth/Llama-3.2-3B-Instruct --out outputs/companion-style
```
Only thumbs-upped replies become targets (never unfiltered model output). Full DPO/preference
tuning needs both a rejected and a chosen reply for the same prompt — capture more ratings over
time and that becomes possible. Extraction remains the highest-signal dataset; style is the fun
one once you've rated a few hundred replies.

Reminder: this tunes *how it talks*, not *what it knows*. Facts stay in the forgettable memory
layer, never baked into weights.

## Cognitive classifiers — what the corpus actually shows

```bash
dotnet run --project tools/Companion.Eval -- --only corpus --out training/corpus
python training/cognition/crossval.py                     # every decision
python training/cognition/crossval.py memory.unfinished   # one
```

### The retraction

The previous version of this section claimed `memory.unfinished` was "the first heuristic worth
replacing", on this evidence:

```
regex (incumbent)      P=1.000 R=0.438 F1=0.609
tf-idf + logreg        P=0.937 R=0.925 F1=0.931      (+0.322)
```

That was one draw of ten template families. The same code, same seed, scored on the ten
*validation* families instead gives `regex F1 0.000, model F1 0.595` — the regex fires on nothing
at all in one draw and on 44 % of rows in another. Neither number is wrong; both are properties of
which ten families landed in the split. **Ten families is not a sample**, and +0.322 from one of
them is a coin flip reported to three decimal places.

The harness now cross-validates over families and puts a paired bootstrap interval on every
difference, which is the only thing here that can answer "is A better than B".

### What that says, over all forty-plus families

95 % interval on the difference in family-macro F1 against the shipped rule:

| decision | union (regex OR model) | model alone |
|---|---|---|
| `memory.decision` | **−0.247** [−0.492, −0.073] — loses | −0.552 — loses |
| `memory.unfinished` | **+0.317** [+0.068, +0.558] — **beats** | +0.290 [−0.016, +0.565] — indistinguishable |
| `tool.capability` | −0.143 [−0.333, +0.000] — indistinguishable | −0.596 — loses |
| `companion.commitment` | 9 families — too few to cross-validate at all | |

Three things worth reading twice.

**The swap is not supported; the composition is.** On the one decision where anything wins, the
model *alone* is indistinguishable from the regex and the *union* beats it. The previous headline
measured the swap and reported a win the swap does not have. This is the shape the codebase already
uses everywhere — two independent signals, neither trusted alone — and it is the only variant that
cannot lose a case the incumbent gets, which is checked rather than assumed.

**It is one decision out of three.** The same model class makes `memory.decision` and
`tool.capability` measurably worse. "Anything learnable should be learned" is not what the data
says; "this particular judgement is learnable and those two are not, yet" is.

**Winning the metric is not the same as being safe to ship.** Precision at a 3 % conversational
base rate, which is roughly what a real conversation looks like:

```
memory.unfinished    incumbent 0.103     union 0.025
```

Family-macro F1 counts a wrongly-fired negative family the same as a missed positive one.
Production does not: an open loop is surfaced unprompted, so a false positive is her asking how
work that does not exist is going, and a false negative is only silence. The union wins the metric
and would fabricate about four times as often. (Both numbers are worst-case — the corpus negatives
are adversarial by construction, not sampled from conversation.)

### Two things that change this, both now buildable

**A real model.** `python training/cognition/finetune_encoder.py memory.unfinished` fine-tunes a
22M MiniLM and exports ONNX straight into `models/`, which is what the C# side already loads. Run
`crossval.py` afterwards, not instead — the comparison that matters is the same grouped
cross-validation and paired bootstrap, and a model that skips it is a model adopted for being newer.

**Real data.** `python training/datasets/fetch.py --list` — DialogueNLI, CommitmentBank, CLINC150,
DailyDialog, mapped into this repo's row shape and read by `crossval.py` unchanged. See
§"The corpora that already exist" in `docs/SPECIALIST_MODELS.md` for what matches what and why two
of them are the exact problems Phase 4 measured an off-the-shelf model failing.

Neither has been run. Hugging Face was unreachable from the session that wrote them, so the mapping
is tested offline and the downloads are not.

### So it is not adopted, and here is what would change that

Every error the model makes is the same error: it cannot read tense, negation or mood.

```
said yes - closed:I thought I'd have to do {t} but I didn't
said yes - closed:we cancelled {t}
said yes - closed:would I need to do {t} first
said yes - closed:{t} is finally sorted
```

Character n-grams over 750 rows cannot see any of that, and no amount of threshold tuning will
make them. That is a statement about the model class, not about the idea — a sentence encoder is
the thing that reads tense, and swapping tf-idf for one is the next experiment rather than the next
regex.

The other half is the corpus. Forty families sounds like a lot until you notice the metric needs
them: fold-to-fold spread runs ±0.14 to ±0.31, wider than every gap being measured. Rows are cheap
(950 of them) and families are what count, so the generator should cross fewer fillers and write
more phrasings — particularly the ones above, since tense and negation are barely represented.

### Two incumbent defects the harness found on the way

Neither needs a model:

- `DecisionDetector` misses **"I've chosen X"** and fires on **"everyone assumes we're going with X"**.
- `ToolNudge` fires on **"are you able to come tomorrow"** and **"are you able to make it on
  Saturday"**, and misses **"do you have access to the internet"**.

Left unpatched on purpose. Adding four phrases to a regex so it scores better on a corpus I wrote
is the treadmill this whole effort exists to get off, and it would also quietly make the baseline
easier to beat. They are recorded here so the decision to fix them is a decision rather than an
accident.
