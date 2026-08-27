"""Run 1a: the first gradient step of the Language Organ project.

QLoRA fine-tune of Qwen2.5-3B-Instruct on the frozen run-1a corpus
(dataset/train-200.jsonl, family splits in dataset/splits.json), per the frozen
config in dataset/config-run1a.json. The prompt format is byte-compatible with
inference: PlanSerialization.SystemPromptV2 as the system turn and BuildUserPrompt's
exact layout (CRLF inside plan/2, the trailing "\nAva's reply:" with a bare LF) as
the user turn. Loss on the assistant turn only.

Deliberately boring: no sweeps, no early stopping, no dataset touching. The config
is the experiment; failures become results.
"""
import json
import math
import time
from pathlib import Path

import glob

import torch
from peft import LoraConfig, get_peft_model, prepare_model_for_kbit_training
from safetensors.torch import load_file
from torch.utils.data import DataLoader, Dataset
from transformers import (AutoConfig, AutoTokenizer, BitsAndBytesConfig,
                          Qwen2ForCausalLM, get_cosine_schedule_with_warmup)


def load_base(model_dir, bnb):
    """Windows workaround: transformers' shard loader slices mmap'd storages, which
    access-violates on this machine (reproduced on torch 2.7.1 and 2.13, transformers
    4.57 and 5.15). safetensors' own reader copies cleanly, so the state dict is
    preloaded and handed over whole. Same weights, same quantization — plumbing only."""
    sd = {}
    for f in sorted(glob.glob(str(model_dir / "*.safetensors"))):
        sd.update(load_file(f))
    cfg = AutoConfig.from_pretrained(model_dir)
    return Qwen2ForCausalLM.from_pretrained(
        None, config=cfg, state_dict=sd, quantization_config=bnb,
        dtype=torch.float16, device_map={"": 0})

import os

ROOT = Path(__file__).parent
DATASET = ROOT / "dataset"
# RUN_ID parameterizes config and output dir so run-1b reuses this script verbatim;
# defaults keep run-1a's behavior byte-identical.
RUN_ID = os.environ.get("RUN_ID", "run-1a")
OUT = ROOT / "runs" / RUN_ID
OUT.mkdir(parents=True, exist_ok=True)

# RUN_ID "run-1a" -> config file "config-run1a.json"
CFG = json.loads((DATASET / f"config-{RUN_ID.replace('run-', 'run')}.json").read_text(encoding="utf-8"))
SEED = CFG["training"]["seed"]
torch.manual_seed(SEED)

MODEL_DIR = ROOT / "models" / "Qwen2.5-3B-Instruct"

SYSTEM_PROMPT = (
    "You are Ava's voice. Ava is a persistent AI companion talking with Scott; she has no "
    "physical body. Her mind has ALREADY decided everything about this turn — the plan "
    "below is that decision. Your only job is to say it naturally, as Ava, speaking to "
    "Scott.\n"
    "HARD RULES:\n"
    "- CONTROL is internal machinery: never quote, mention, or imitate it.\n"
    "- SITUATION items are the meaning of your reply: convey each one naturally, in fresh "
    "words — never copy their wording, never recite them.\n"
    "- CONSTRAINTS are absolute. Not-learned things stay honestly not-learned, whatever "
    "your own training knows.\n"
    "- PALETTE is optional color; ignore it unless it truly fits.\n"
    "- Ask a question only if the plan says so.\n"
    "- Never invent shared memories, physical experiences, or facts. Speak as \"I\" (Ava) "
    "to \"you\" (Scott).\n"
    "STYLE is yours to interpret: wording, rhythm, warmth, humor. Short and ordinary beats "
    "long and ornate. Output Ava's reply text only.")


def build_user_prompt(plan2: str, transcript, user_message: str) -> str:
    """Mirror of PlanSerialization.BuildUserPrompt on Windows: AppendLine emits CRLF;
    the final 'Ava's reply:' is appended with a bare LF."""
    nl = "\r\n"
    parts = ["RESPONSE PLAN:" + nl, plan2 + nl, "RECENT CONVERSATION:" + nl]
    for t in transcript:
        who = "Scott" if t["role"] == "user" else "Ava"
        parts.append(f"[{who}] {t['text']}" + nl)
    parts.append(f"[Scott] {user_message}" + nl)
    parts.append("\nAva's reply:")
    return "".join(parts)


class PlanDataset(Dataset):
    def __init__(self, rows, tokenizer, max_len):
        self.items = []
        for r in rows:
            messages = [
                {"role": "system", "content": SYSTEM_PROMPT},
                {"role": "user", "content": build_user_prompt(r["plan2"], r["transcript"], r["userMessage"])},
            ]
            templated = tokenizer.apply_chat_template(
                messages, tokenize=True, add_generation_prompt=True)
            # transformers 5.x returns a BatchEncoding; 4.x returned a bare id list.
            prompt_ids = templated["input_ids"] if not isinstance(templated, list) else templated
            if prompt_ids and isinstance(prompt_ids[0], list):
                prompt_ids = prompt_ids[0]
            target_ids = tokenizer(r["target"] + "<|im_end|>", add_special_tokens=False)["input_ids"]
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
    rows = [json.loads(l) for l in (DATASET / "train-200.jsonl").read_text(encoding="utf-8").splitlines() if l.strip()]
    splits = json.loads((DATASET / "splits.json").read_text(encoding="utf-8"))
    val_families = set(splits["validationFamilies"])
    train_rows = [r for r in rows if r["family"] not in val_families]
    val_rows = [r for r in rows if r["family"] in val_families]
    print(f"train {len(train_rows)} / val {len(val_rows)}")

    tokenizer = AutoTokenizer.from_pretrained(MODEL_DIR)
    bnb = BitsAndBytesConfig(
        load_in_4bit=True, bnb_4bit_quant_type="nf4",
        bnb_4bit_use_double_quant=True, bnb_4bit_compute_dtype=torch.float16)
    model = load_base(MODEL_DIR, bnb)
    model = prepare_model_for_kbit_training(model, use_gradient_checkpointing=True)
    lora = LoraConfig(**{k: v for k, v in CFG["lora"].items() if k != "task_type"},
                      task_type="CAUSAL_LM")
    model = get_peft_model(model, lora)
    model.print_trainable_parameters()

    tcfg = CFG["training"]
    max_len = tcfg["max_seq_length"]
    train_ds = PlanDataset(train_rows, tokenizer, max_len)
    val_ds = PlanDataset(val_rows, tokenizer, max_len)
    pad_id = tokenizer.pad_token_id or tokenizer.eos_token_id
    ga = tcfg["gradient_accumulation_steps"]

    # Deterministic per-epoch permutations (seed + epoch), so a resumed run walks the
    # exact same example order the crashed one did. Batch size is 1 per the config;
    # this loop indexes the dataset directly instead of trusting a DataLoader's
    # generator state to survive process death (this machine crashes under sustained
    # GPU load — twice now — so exact resume is a requirement, not a nicety).
    assert tcfg["per_device_train_batch_size"] == 1
    def epoch_order(epoch):
        g = torch.Generator().manual_seed(SEED + epoch)
        return torch.randperm(len(train_ds), generator=g).tolist()

    steps_per_epoch = math.ceil(len(train_ds) / ga)
    total_steps = steps_per_epoch * tcfg["num_train_epochs"]
    import bitsandbytes as bnb_opt
    optimizer = bnb_opt.optim.PagedAdamW8bit(
        (p for p in model.parameters() if p.requires_grad), lr=tcfg["learning_rate"])
    scheduler = get_cosine_schedule_with_warmup(
        optimizer, int(total_steps * tcfg["warmup_ratio"]), total_steps)
    print(f"optimizer steps: {total_steps} ({steps_per_epoch}/epoch)")

    checkpoint_every = int(os.environ.get("CHECKPOINT_EVERY", tcfg["save_steps"]))

    def save_state(step, epoch, tag):
        path = OUT / f"checkpoint-{tag}"
        model.save_pretrained(path)
        # RNG state travels with the checkpoint. Example ORDER is already exact (the
        # per-epoch permutation is reseeded from SEED + epoch), but LoRA dropout draws
        # from the global generators, so without these a resumed run takes different
        # dropout masks from the step it resumes at onward. That makes it a different
        # valid sample rather than the same run continued -- which is fine as training
        # and fatal as a reproducibility claim, since the freeze manifest says the run
        # is reproducible from its config and seed.
        torch.save({"optimizer": optimizer.state_dict(),
                    "scheduler": scheduler.state_dict(),
                    "step": step, "epoch": epoch,
                    "torch_rng": torch.get_rng_state(),
                    "cuda_rng": torch.cuda.get_rng_state_all()
                    if torch.cuda.is_available() else None},
                   path / "trainer-state.pt")

    # Resume: newest checkpoint with trainer-state wins. Examples consumed inside a
    # partially accumulated batch are re-run (their gradients were zeroed at save).
    resume_step, resume_epoch = 0, 0
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

        # Restore the generators, and say plainly when a checkpoint predates them: a
        # resume without RNG state is still correct training, but it is no longer the
        # same run, and that belongs in the run log rather than in nobody's memory.
        if state.get("torch_rng") is not None:
            torch.set_rng_state(state["torch_rng"].cpu().to(torch.uint8))
            cuda_rng = state.get("cuda_rng")
            if cuda_rng is not None and torch.cuda.is_available():
                if len(cuda_rng) == torch.cuda.device_count():
                    torch.cuda.set_rng_state_all([t.cpu().to(torch.uint8) for t in cuda_rng])
                else:
                    print(f"  RNG WARNING: checkpoint has {len(cuda_rng)} CUDA generator(s), "
                          f"this host has {torch.cuda.device_count()}; CUDA RNG not restored. "
                          f"Dropout masks will diverge from the interrupted run.")
            rng_note = "exact (RNG restored)"
        else:
            rng_note = ("APPROXIMATE - checkpoint predates RNG capture; dropout masks "
                        "diverge from here. Record this on the run.")

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
    if resume_step == 0:
        record(step=0, valLoss=round(evaluate(), 4))

    # Accumulation carries across epoch boundaries (run-1a's exact semantics), so one
    # optimizer step always consumes exactly `ga` examples and the global example
    # cursor is simply step*ga. Resume converts that cursor back to (epoch, offset).
    n = len(train_ds)
    step, accum, running = resume_step, 0, 0.0
    consumed = resume_step * ga
    model.train()
    for epoch in range(consumed // n, tcfg["num_train_epochs"]):
        order = epoch_order(epoch)
        start = consumed % n if epoch == consumed // n else 0
        for idx in order[start:]:
            b = collate([train_ds[idx]], pad_id)
            ids, labels, mask = (t.cuda() for t in b)
            out = model(input_ids=ids, attention_mask=mask, labels=labels)
            (out.loss / ga).backward()
            running += out.loss.item()
            accum += 1
            if accum == ga:
                torch.nn.utils.clip_grad_norm_(
                    (p for p in model.parameters() if p.requires_grad), 1.0)
                optimizer.step()
                scheduler.step()
                optimizer.zero_grad()
                accum = 0
                step += 1
                if step % tcfg["logging_steps"] == 0:
                    record(step=step, epoch=epoch, trainLoss=round(running / (ga * tcfg["logging_steps"]), 4),
                           lr=scheduler.get_last_lr()[0],
                           vramGb=round(torch.cuda.max_memory_allocated() / 2**30, 2))
                    running = 0.0
                if step % checkpoint_every == 0:
                    save_state(step, epoch, str(step))
                if step % tcfg["eval_steps"] == 0:
                    record(step=step, valLoss=round(evaluate(), 4))
    final_val = evaluate()
    record(step=step, valLoss=round(final_val, 4), final=True)
    model.save_pretrained(OUT / "adapter-final")
    tokenizer.save_pretrained(OUT / "adapter-final")
    print(f"done: {step} steps, final val loss {final_val:.4f} -> {OUT / 'adapter-final'}")


if __name__ == "__main__":
    main()
