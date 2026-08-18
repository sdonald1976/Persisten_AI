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

## unfinished-work classifier — the first heuristic with evidence against it

```bash
dotnet run --project tools/Companion.Eval -- --only corpus --out training/corpus
python training/unfinished/train.py
```

Measured on ten template families held out of training entirely — unseen phrasings, not
unseen rows:

```
regex (incumbent)      P=1.000 R=0.438 F1=0.609
tf-idf + logreg        P=0.937 R=0.925 F1=0.931
```

The incumbent never claims work that is not there and misses more than half of what is,
because it is a list of obligation phrasings and there is no end to those. The learned model
picks up "I haven't got round to", "is still hanging over me", "I keep putting off",
"I'm behind on" — none of which appear in its training families.

**Not adopted.** Three reasons, all of which have to be answered first:

1. Every row is synthetic and I wrote the templates. The model may be learning my writing
   habits rather than English, and the only cure is examples from real conversations.
2. Precision drops 1.000 → 0.937. The regex has never once invented an open loop; this
   would, on roughly one negative in sixteen. Open loops are surfaced unprompted, so a false
   positive is her asking about work that does not exist.
3. It still misses "I'm in the middle of X" — which the regex gets right, and which is in the
   training data. A replacement that loses cases the incumbent handles is not a replacement,
   it is a trade, and the regression corpus exists precisely to stop that being invisible.

The obvious next step is a hybrid: the regex keeps its perfect precision, the model catches
what it misses, and the union is measured against both.
