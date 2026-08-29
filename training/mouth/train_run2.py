"""Run 2: the mouth.

QLoRA fine-tune of Qwen2.5-3B-Instruct on the frozen Run-2 corpus, per config-run2.json.

Descended from training/renderer/train_run1a.py and deliberately close to it: same base and
revision, same quantization, same LoRA shape, same optimizer and schedule, same exact-resume
machinery. Holding all of that fixed is what makes "Run-2 vs Run-1c" a measurement of the corpus
and the prompt format rather than of everything at once.

What is genuinely different, and why:

  * The row IS the prompt. Run-1 reconstructed plan/2 from parts and had to mirror C# string
    building in Python to stay byte-compatible with inference. A Run-2 row carries `system` and
    `input` as the exact bytes MouthPromptV4 emits, so this trainer concatenates and never
    rebuilds. There is nothing here that can drift from the shipping renderer.

  * Checkpoint selection and early stopping on validation loss. Run-1 trained a fixed number of
    steps on purpose. Run-2 keeps the best checkpoint by validation loss and stops when it stops
    improving; test and hard-eval are never opened while that is happening.

  * max_seq_length 1536, measured rather than inherited. See config-run2.json deviations.
"""
import glob
import io
import json
import math
import os
import time
from pathlib import Path

import torch
from peft import LoraConfig, get_peft_model, prepare_model_for_kbit_training
from safetensors.torch import load_file
from torch.utils.data import DataLoader, Dataset
from transformers import (AutoConfig, AutoTokenizer, BitsAndBytesConfig,
                          Qwen2ForCausalLM, get_cosine_schedule_with_warmup)

ROOT = Path(__file__).parent
REPO = ROOT.parent.parent
DATASET = ROOT / "dataset"
RUN_ID = os.environ.get("RUN_ID", "run-2")
OUT = ROOT / "runs" / RUN_ID
OUT.mkdir(parents=True, exist_ok=True)

CFG = json.loads((ROOT / "config-run2.json").read_text(encoding="utf-8"))
TCFG = CFG["training"]
SEED = TCFG["seed"]
torch.manual_seed(SEED)

MODEL_DIR = REPO / "training" / "renderer" / "models" / "Qwen2.5-3B-Instruct"


def read_rows(name):
    """utf-8-sig: the frozen corpus carries a UTF-8 BOM. It is approved and must not be
    rewritten, so the reader absorbs it. Recorded in config-run2.json for the next freeze."""
    path = DATASET / name
    return [json.loads(l) for l in io.open(path, encoding="utf-8-sig") if l.strip()]


def load_base(model_dir, bnb):
    """Windows workaround inherited from run-1a: transformers' shard loader slices mmap'd
    storages and access-violates on this machine. safetensors' own reader copies cleanly, so the
    state dict is preloaded and handed over whole. Same weights, same quantization."""
    sd = {}
    for f in sorted(glob.glob(str(model_dir / "*.safetensors"))):
        sd.update(load_file(f))
    cfg = AutoConfig.from_pretrained(model_dir)
    return Qwen2ForCausalLM.from_pretrained(
        None, config=cfg, state_dict=sd, quantization_config=bnb,
        dtype=torch.float16, device_map={"": 0})


class MouthDataset(Dataset):
    """The row, tokenized. `system` and `input` are used verbatim - this is the one place the
    corpus and the trainer meet, and reconstructing either would reintroduce exactly the drift
    the row format exists to prevent."""

    def __init__(self, rows, tokenizer, max_len):
        self.items = []
        self.truncated = 0
        for r in rows:
            messages = [
                {"role": "system", "content": r["system"]},
                {"role": "user", "content": r["input"]},
            ]
            templated = tokenizer.apply_chat_template(
                messages, tokenize=True, add_generation_prompt=True)
            prompt_ids = templated["input_ids"] if not isinstance(templated, list) else templated
            if prompt_ids and isinstance(prompt_ids[0], list):
                prompt_ids = prompt_ids[0]
            target_ids = tokenizer(r["target"] + "<|im_end|>", add_special_tokens=False)["input_ids"]

            if len(prompt_ids) + len(target_ids) > max_len:
                self.truncated += 1
            input_ids = (prompt_ids + target_ids)[:max_len]
            labels = ([-100] * len(prompt_ids) + target_ids)[:max_len]
            if all(l == -100 for l in labels):
                raise SystemExit(f"{r['id']}: prompt alone exceeds max_len={max_len}")
            self.items.append({"id": r["id"], "input_ids": input_ids, "labels": labels})

    def __len__(self):
        return len(self.items)

    def __getitem__(self, i):
        return self.items[i]


def collate(batch, pad_id):
    width = max(len(b["input_ids"]) for b in batch)
    input_ids, labels, mask = [], [], []
    for b in batch:
        pad = width - len(b["input_ids"])
        input_ids.append(b["input_ids"] + [pad_id] * pad)
        labels.append(b["labels"] + [-100] * pad)
        mask.append([1] * len(b["input_ids"]) + [0] * pad)
    return (torch.tensor(input_ids), torch.tensor(labels), torch.tensor(mask))


def main():
    train_rows = read_rows(CFG["corpus"]["train"])
    val_rows = read_rows(CFG["corpus"]["validation"])
    print(f"train {len(train_rows)} / val {len(val_rows)}")

    tokenizer = AutoTokenizer.from_pretrained(MODEL_DIR)
    bnb = BitsAndBytesConfig(
        load_in_4bit=True, bnb_4bit_quant_type="nf4",
        bnb_4bit_use_double_quant=True, bnb_4bit_compute_dtype=torch.float16)
    model = load_base(MODEL_DIR, bnb)
    model = prepare_model_for_kbit_training(
        model,
        use_gradient_checkpointing=TCFG["gradient_checkpointing"],
        # Explicit, not inherited. The reentrant implementation is the legacy one and PyTorch
        # warns on every run that the default is changing; pinning it here means the run depends
        # on this config rather than on which torch happens to be installed.
        gradient_checkpointing_kwargs={
            "use_reentrant": TCFG["gradient_checkpointing_use_reentrant"]},
    )
    lora = LoraConfig(**{k: v for k, v in CFG["lora"].items() if k != "task_type"},
                      task_type="CAUSAL_LM")
    model = get_peft_model(model, lora)
    model.print_trainable_parameters()

    max_len = TCFG["max_seq_length"]
    train_ds = MouthDataset(train_rows, tokenizer, max_len)
    val_ds = MouthDataset(val_rows, tokenizer, max_len)
    if train_ds.truncated or val_ds.truncated:
        raise SystemExit(
            f"TRUNCATION: {train_ds.truncated} train and {val_ds.truncated} val rows exceed "
            f"max_seq_length={max_len}. Truncation removes the target, not the prompt.")
    print(f"no truncation at max_seq_length={max_len}")

    pad_id = tokenizer.pad_token_id or tokenizer.eos_token_id
    ga = TCFG["gradient_accumulation_steps"]
    assert TCFG["per_device_train_batch_size"] == 1

    def epoch_order(epoch):
        g = torch.Generator().manual_seed(SEED + epoch)
        return torch.randperm(len(train_ds), generator=g).tolist()

    steps_per_epoch = math.ceil(len(train_ds) / ga)
    total_steps = steps_per_epoch * TCFG["num_train_epochs"]
    import bitsandbytes as bnb_opt
    optimizers = {"paged_adamw_8bit": bnb_opt.optim.PagedAdamW8bit,
                  "adamw_8bit": bnb_opt.optim.AdamW8bit}
    optimizer = optimizers[TCFG["optim"]](
        (p for p in model.parameters() if p.requires_grad), lr=TCFG["learning_rate"])
    scheduler = get_cosine_schedule_with_warmup(
        optimizer, int(total_steps * TCFG["warmup_ratio"]), total_steps)
    print(f"optimizer steps: {total_steps} ({steps_per_epoch}/epoch)")

    def save_state(step, epoch, tag, best_val, best_step, since_improve):
        path = OUT / f"checkpoint-{tag}"
        model.save_pretrained(path)
        # RNG travels with the checkpoint. Example order is already exact (per-epoch permutation
        # reseeded from SEED + epoch), but LoRA dropout draws from the global generators: without
        # these, a resumed run takes different masks from the resume point onward and is a
        # different valid sample rather than the same run continued.
        torch.save({"optimizer": optimizer.state_dict(),
                    "scheduler": scheduler.state_dict(),
                    "step": step, "epoch": epoch,
                    "bestVal": best_val, "bestStep": best_step,
                    "sinceImprove": since_improve,
                    "torch_rng": torch.get_rng_state(),
                    "cuda_rng": torch.cuda.get_rng_state_all()
                    if torch.cuda.is_available() else None},
                   path / "trainer-state.pt")

    resume_step, resume_epoch = 0, 0
    best_val, best_step, since_improve = float("inf"), 0, 0
    rng_note = "fresh start"
    ckpts = sorted((p for p in OUT.glob("checkpoint-*") if (p / "trainer-state.pt").exists()),
                   key=lambda p: (p / "trainer-state.pt").stat().st_mtime)
    if ckpts:
        latest = ckpts[-1]
        state = torch.load(latest / "trainer-state.pt", weights_only=False)
        from peft import set_peft_model_state_dict
        from safetensors.torch import load_file as load_sft
        set_peft_model_state_dict(model, load_sft(str(latest / "adapter_model.safetensors")))
        optimizer.load_state_dict(state["optimizer"])
        scheduler.load_state_dict(state["scheduler"])
        resume_step, resume_epoch = state["step"], state["epoch"]
        best_val = state.get("bestVal", float("inf"))
        best_step = state.get("bestStep", 0)
        since_improve = state.get("sinceImprove", 0)

        if state.get("torch_rng") is not None:
            torch.set_rng_state(state["torch_rng"].cpu().to(torch.uint8))
            cuda_rng = state.get("cuda_rng")
            if cuda_rng is not None and torch.cuda.is_available():
                if len(cuda_rng) == torch.cuda.device_count():
                    torch.cuda.set_rng_state_all([t.cpu().to(torch.uint8) for t in cuda_rng])
                else:
                    print(f"  RNG WARNING: checkpoint has {len(cuda_rng)} CUDA generator(s), "
                          f"host has {torch.cuda.device_count()}; CUDA RNG not restored.")
            rng_note = "exact (RNG restored)"
        else:
            rng_note = "APPROXIMATE - checkpoint predates RNG capture"
        print(f"RESUMING from {latest.name}: step {resume_step}, epoch {resume_epoch}")
        print(f"  resume fidelity: {rng_note}")

    def evaluate():
        model.eval()
        losses = []
        with torch.no_grad():
            for b in DataLoader(val_ds, batch_size=1, collate_fn=lambda b: collate(b, pad_id)):
                ids, labels, mask = (t.cuda() for t in b)
                out = model(input_ids=ids, attention_mask=mask, labels=labels)
                losses.append(out.loss.item())
        model.train()
        return sum(losses) / len(losses)

    log = open(OUT / "training-log.jsonl", "a", encoding="utf-8")

    def record(**kw):
        kw["elapsedSec"] = round(time.time() - t0, 1)
        log.write(json.dumps(kw) + "\n")
        log.flush()
        print("  " + json.dumps(kw))

    t0 = time.time()
    record(event="start", resumeStep=resume_step, resumeFidelity=rng_note,
           maxSeqLen=max_len, totalSteps=total_steps, seed=SEED)
    if resume_step == 0:
        best_val = evaluate()
        best_step = 0
        record(step=0, valLoss=round(best_val, 4), note="pre-training adapter (zero-init)")
        save_state(0, 0, "best", best_val, 0, 0)

    n = len(train_ds)
    step, accum, running = resume_step, 0, 0.0
    consumed = resume_step * ga
    stopped_early = False
    model.train()
    for epoch in range(consumed // n, TCFG["num_train_epochs"]):
        if stopped_early:
            break
        order = epoch_order(epoch)
        start = consumed % n if epoch == consumed // n else 0
        for idx in order[start:]:
            b = collate([train_ds[idx]], pad_id)
            ids, labels, mask = (t.cuda() for t in b)
            out = model(input_ids=ids, attention_mask=mask, labels=labels)
            (out.loss / ga).backward()
            running += out.loss.item()
            accum += 1
            if accum != ga:
                continue

            torch.nn.utils.clip_grad_norm_(
                (p for p in model.parameters() if p.requires_grad), 1.0)
            optimizer.step()
            scheduler.step()
            optimizer.zero_grad()
            accum = 0
            step += 1

            if step % TCFG["logging_steps"] == 0:
                record(step=step, epoch=epoch,
                       trainLoss=round(running / (ga * TCFG["logging_steps"]), 4),
                       lr=scheduler.get_last_lr()[0],
                       vramGb=round(torch.cuda.max_memory_allocated() / 2**30, 2))
                running = 0.0

            # Checkpoint BEFORE evaluating. The driver on this machine resets under sustained
            # load - it did so mid-validation on the first attempt at this run, killing the
            # process with exit code 0 and losing twenty steps that were already computed.
            # Evaluation is the longest uninterrupted GPU burst in the loop, so saving after it
            # means the most crash-prone moment is also the one holding the most unsaved work.
            if step % TCFG["save_steps"] == 0:
                save_state(step, epoch, str(step), best_val, best_step, since_improve)

            if step % TCFG["eval_steps"] == 0:
                val = evaluate()
                improved = val < best_val - TCFG["early_stopping_min_delta"]
                if improved:
                    best_val, best_step, since_improve = val, step, 0
                    save_state(step, epoch, "best", best_val, best_step, since_improve)
                else:
                    since_improve += 1
                record(step=step, epoch=epoch, valLoss=round(val, 4),
                       bestVal=round(best_val, 4), bestStep=best_step,
                       sinceImprove=since_improve, improved=improved)
                if since_improve >= TCFG["early_stopping_patience_evals"]:
                    record(event="early-stop", step=step, bestStep=best_step,
                           bestVal=round(best_val, 4))
                    stopped_early = True
                    break

    record(event="done", step=step, bestStep=best_step, bestVal=round(best_val, 4),
           earlyStopped=stopped_early)

    # The selected checkpoint IS the adapter. Copying the best rather than saving the last is the
    # whole point of selection; saving the final weights here would quietly discard it.
    import shutil
    best_dir = OUT / "checkpoint-best"
    final = OUT / "adapter-final"
    if final.exists():
        shutil.rmtree(final)
    shutil.copytree(best_dir, final)
    for stray in ("trainer-state.pt",):
        if (final / stray).exists():
            (final / stray).unlink()
    tokenizer.save_pretrained(final)
    print(f"done: {step} steps, best val {best_val:.4f} at step {best_step} -> {final}")


if __name__ == "__main__":
    main()
