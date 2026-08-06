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

## Once you're capturing reply feedback
The `chat-sft` path becomes worthwhile (and DPO/preference tuning from corrections becomes
possible) once the app records which replies were good/bad. That's a small future addition; for
now, extraction is the sweet spot.
