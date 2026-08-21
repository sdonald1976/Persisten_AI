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

import torch
from peft import LoraConfig, get_peft_model, prepare_model_for_kbit_training
from torch.utils.data import DataLoader, Dataset
from transformers import (AutoModelForCausalLM, AutoTokenizer, BitsAndBytesConfig,
                          get_cosine_schedule_with_warmup)

ROOT = Path(__file__).parent
DATASET = ROOT / "dataset"
OUT = ROOT / "runs" / "run-1a"
OUT.mkdir(parents=True, exist_ok=True)

CFG = json.loads((DATASET / "config-run1a.json").read_text(encoding="utf-8"))
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
            prompt_ids = tokenizer.apply_chat_template(
                messages, tokenize=True, add_generation_prompt=True)
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
    model = AutoModelForCausalLM.from_pretrained(
        MODEL_DIR, quantization_config=bnb, dtype=torch.float16, device_map={"": 0})
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

    gen = torch.Generator().manual_seed(SEED)
    loader = DataLoader(train_ds, batch_size=tcfg["per_device_train_batch_size"],
                        shuffle=True, generator=gen,
                        collate_fn=lambda b: collate(b, pad_id))

    steps_per_epoch = math.ceil(len(loader) / ga)
    total_steps = steps_per_epoch * tcfg["num_train_epochs"]
    import bitsandbytes as bnb_opt
    optimizer = bnb_opt.optim.PagedAdamW8bit(
        (p for p in model.parameters() if p.requires_grad), lr=tcfg["learning_rate"])
    scheduler = get_cosine_schedule_with_warmup(
        optimizer, int(total_steps * tcfg["warmup_ratio"]), total_steps)
    print(f"optimizer steps: {total_steps} ({steps_per_epoch}/epoch)")

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
    val0 = evaluate()
    record(step=0, valLoss=round(val0, 4))

    step, accum, running = 0, 0, 0.0
    model.train()
    for epoch in range(tcfg["num_train_epochs"]):
        for b in loader:
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
                if step % tcfg["eval_steps"] == 0:
                    record(step=step, valLoss=round(evaluate(), 4))
                    model.save_pretrained(OUT / f"checkpoint-{step}")
    final_val = evaluate()
    record(step=step, valLoss=round(final_val, 4), final=True)
    model.save_pretrained(OUT / "adapter-final")
    tokenizer.save_pretrained(OUT / "adapter-final")
    print(f"done: {step} steps, final val loss {final_val:.4f} -> {OUT / 'adapter-final'}")


if __name__ == "__main__":
    main()
