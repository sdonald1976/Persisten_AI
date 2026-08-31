"""Run 2.2: a bounded register correction on top of Run-2.1, with replay.

Identical machinery to train_run21; only the config and the adapter it continues from differ.
Continues from Run-2.1 (not Run-2), replays the same reissued corpus, and mixes in the
consensual-adult register supplement. See config-run22.json.

ORIGINAL run-2.1 header follows:
Run 2.1: a bounded correction on top of Run-2, with replay.

Continues from Run-2's selected checkpoint rather than starting over. The correction is narrow -
one composition, 94 rows - and a narrow correction trained alone overwrites the general behaviour
it was meant to sit beside. Replay from the full reissued Run-2 training corpus is what keeps the
rest of the model where it was.

Three things make this a correction rather than a second run:

  * THE MIXTURE IS BOUNDED. The supplement is oversampled so it is learnable at all, and capped so
    it cannot dominate. At the default it is ~15% of the effective mixture; the other ~85% is
    replay of what Run-2 already learned.

  * THE LEARNING RATE IS LOWER. Run-2 trained at 1e-4 from a zero-initialised adapter. Continuing
    at that rate would move weights further than the correction warrants and undo the run it is
    correcting.

  * SELECTION READS BOTH VALIDATIONS. The original validation may not materially regress, and the
    targeted validation must improve. A checkpoint that fixes the composition by damaging
    everything else is not a correction, and one validation set alone cannot tell the difference.

    python train_run21.py
"""
import glob
import io
import json
import math
import os
import time
from pathlib import Path

import torch
from peft import LoraConfig, PeftModel, get_peft_model, prepare_model_for_kbit_training
from safetensors.torch import load_file
from torch.utils.data import DataLoader, Dataset
from transformers import (AutoConfig, AutoTokenizer, BitsAndBytesConfig,
                          Qwen2ForCausalLM, get_cosine_schedule_with_warmup)

ROOT = Path(__file__).parent
REPO = ROOT.parent.parent
RUN_ID = os.environ.get("RUN_ID", "run-2.2")
OUT = ROOT / "runs" / RUN_ID
OUT.mkdir(parents=True, exist_ok=True)

CFG = json.loads((ROOT / "config-run22.json").read_text(encoding="utf-8"))
TCFG = CFG["training"]
SEED = TCFG["seed"]
torch.manual_seed(SEED)

MODEL_DIR = REPO / "training" / "renderer" / "models" / "Qwen2.5-3B-Instruct"
RUN2_ADAPTER = ROOT / "runs" / "run-2.1" / "adapter-final"  # continue from Run-2.1


def read_rows(path):
    """utf-8-sig: the exports carry a BOM. Recorded in config; the corpora are not rewritten."""
    return [json.loads(l) for l in io.open(path, encoding="utf-8-sig") if l.strip()]


def load_base(model_dir, bnb):
    sd = {}
    for f in sorted(glob.glob(str(model_dir / "*.safetensors"))):
        sd.update(load_file(f))
    cfg = AutoConfig.from_pretrained(model_dir)
    return Qwen2ForCausalLM.from_pretrained(
        None, config=cfg, state_dict=sd, quantization_config=bnb,
        dtype=torch.float16, device_map={"": 0})


class MouthDataset(Dataset):
    def __init__(self, rows, tokenizer, max_len, tag):
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
            self.items.append({"id": r["id"], "tag": tag,
                               "input_ids": input_ids, "labels": labels})

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
    corpus = ROOT / CFG["corpus"]["directory"]
    supplement = ROOT / CFG["supplement"]["directory"]

    replay_rows = read_rows(corpus / CFG["corpus"]["train"])
    sup_train = read_rows(supplement / CFG["supplement"]["train"])
    val_main = read_rows(corpus / CFG["corpus"]["validation"])
    val_targeted = read_rows(supplement / CFG["supplement"]["validation"])

    tokenizer = AutoTokenizer.from_pretrained(MODEL_DIR)
    bnb = BitsAndBytesConfig(
        load_in_4bit=True, bnb_4bit_quant_type="nf4",
        bnb_4bit_use_double_quant=True, bnb_4bit_compute_dtype=torch.float16)
    model = load_base(MODEL_DIR, bnb)
    model = prepare_model_for_kbit_training(
        model, use_gradient_checkpointing=TCFG["gradient_checkpointing"],
        gradient_checkpointing_kwargs={
            "use_reentrant": TCFG["gradient_checkpointing_use_reentrant"]})

    # CONTINUE from Run-2, do not start over. The adapter is loaded trainable so the correction
    # moves the weights Run-2 learned rather than a fresh set beside them.
    model = PeftModel.from_pretrained(model, str(RUN2_ADAPTER), is_trainable=True)
    model.print_trainable_parameters()

    max_len = TCFG["max_seq_length"]
    replay_ds = MouthDataset(replay_rows, tokenizer, max_len, "replay")
    sup_ds = MouthDataset(sup_train, tokenizer, max_len, "supplement")
    val_main_ds = MouthDataset(val_main, tokenizer, max_len, "val-main")
    val_targeted_ds = MouthDataset(val_targeted, tokenizer, max_len, "val-targeted")
    total_trunc = replay_ds.truncated + sup_ds.truncated + val_main_ds.truncated + val_targeted_ds.truncated
    if total_trunc:
        raise SystemExit(f"TRUNCATION: {total_trunc} row(s) exceed max_seq_length={max_len}")

    # The mixture: replay once, supplement repeated up to the configured share. Oversampling is
    # what makes 94 rows learnable beside 1,616; the cap is what stops the correction becoming the
    # run. Both numbers are recorded rather than tuned in place.
    share = TCFG["supplement_share"]
    repeats = max(1, round(share * len(replay_ds) / ((1 - share) * len(sup_ds))))
    mixture = [("replay", i) for i in range(len(replay_ds))]
    for _ in range(repeats):
        mixture += [("supplement", i) for i in range(len(sup_ds))]
    actual_share = repeats * len(sup_ds) / len(mixture)
    if actual_share > TCFG["supplement_share_ceiling"]:
        raise SystemExit(
            f"mixture share {actual_share:.3f} exceeds the ceiling "
            f"{TCFG['supplement_share_ceiling']}; the correction would dominate")

    print(f"replay {len(replay_ds)} / supplement {len(sup_ds)} x{repeats} "
          f"= {len(mixture)} examples, supplement {actual_share:.1%}")
    print(f"validation: main {len(val_main_ds)}, targeted {len(val_targeted_ds)}")

    pad_id = tokenizer.pad_token_id or tokenizer.eos_token_id
    ga = TCFG["gradient_accumulation_steps"]

    def epoch_order(epoch):
        g = torch.Generator().manual_seed(SEED + epoch)
        return torch.randperm(len(mixture), generator=g).tolist()

    steps_per_epoch = math.ceil(len(mixture) / ga)
    total_steps = steps_per_epoch * TCFG["num_train_epochs"]
    import bitsandbytes as bnb_opt
    optimizers = {"paged_adamw_8bit": bnb_opt.optim.PagedAdamW8bit,
                  "adamw_8bit": bnb_opt.optim.AdamW8bit}
    optimizer = optimizers[TCFG["optim"]](
        (p for p in model.parameters() if p.requires_grad), lr=TCFG["learning_rate"])
    scheduler = get_cosine_schedule_with_warmup(
        optimizer, int(total_steps * TCFG["warmup_ratio"]), total_steps)
    print(f"optimizer steps: {total_steps} ({steps_per_epoch}/epoch) at lr {TCFG['learning_rate']}")

    def evaluate(ds):
        model.eval()
        losses = []
        with torch.no_grad():
            for b in DataLoader(ds, batch_size=1, collate_fn=lambda b: collate(b, pad_id)):
                ids, labels, mask = (t.cuda() for t in b)
                losses.append(model(input_ids=ids, attention_mask=mask, labels=labels).loss.item())
        model.train()
        return sum(losses) / len(losses)

    log = open(OUT / "training-log.jsonl", "a", encoding="utf-8")
    t0 = time.time()

    def record(**kw):
        kw["elapsedSec"] = round(time.time() - t0, 1)
        log.write(json.dumps(kw) + "\n")
        log.flush()
        print("  " + json.dumps(kw), flush=True)

    def save_state(tag, step, epoch, best, since):
        path = OUT / f"checkpoint-{tag}"
        model.save_pretrained(path)
        torch.save({"optimizer": optimizer.state_dict(), "scheduler": scheduler.state_dict(),
                    "step": step, "epoch": epoch, "best": best, "sinceImprove": since,
                    "torch_rng": torch.get_rng_state(),
                    "cuda_rng": torch.cuda.get_rng_state_all()
                    if torch.cuda.is_available() else None},
                   path / "trainer-state.pt")

    # Resume, on the same terms as Run-2: RNG travels with the checkpoint so a restart continues
    # the run rather than sampling a new one.
    resume_step, resume_epoch, since_improve = 0, 0, 0
    best = None
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
        best, since_improve = state.get("best"), state.get("sinceImprove", 0)
        if state.get("torch_rng") is not None:
            torch.set_rng_state(state["torch_rng"].cpu().to(torch.uint8))
            cuda = state.get("cuda_rng")
            if cuda is not None and torch.cuda.is_available() and len(cuda) == torch.cuda.device_count():
                torch.cuda.set_rng_state_all([t.cpu().to(torch.uint8) for t in cuda])
            rng_note = "exact (RNG restored)"
        else:
            rng_note = "APPROXIMATE - checkpoint predates RNG capture"
        print(f"RESUMING from {latest.name}: step {resume_step}   fidelity: {rng_note}")

    record(event="start", resumeStep=resume_step, resumeFidelity=rng_note,
           replay=len(replay_ds), supplement=len(sup_ds), repeats=repeats,
           supplementShare=round(actual_share, 4), totalSteps=total_steps,
           lr=TCFG["learning_rate"], seed=SEED)

    if resume_step == 0:
        base_main, base_targeted = evaluate(val_main_ds), evaluate(val_targeted_ds)
        best = {"main": base_main, "targeted": base_targeted, "step": 0}
        record(step=0, valMain=round(base_main, 4), valTargeted=round(base_targeted, 4),
               note="Run-2 checkpoint before any correction")
        save_state("best", 0, 0, best, 0)

    baseline_main = best["main"] if best else None
    n = len(mixture)
    step, accum, running = resume_step, 0, 0.0
    consumed = resume_step * ga
    stopped = False
    model.train()

    for epoch in range(consumed // n, TCFG["num_train_epochs"]):
        if stopped:
            break
        order = epoch_order(epoch)
        start = consumed % n if epoch == consumed // n else 0
        for idx in order[start:]:
            tag, i = mixture[idx]
            ds = replay_ds if tag == "replay" else sup_ds
            b = collate([ds[i]], pad_id)
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

            if step % TCFG["save_steps"] == 0:
                save_state(str(step), step, epoch, best, since_improve)

            if step % TCFG["eval_steps"] == 0:
                main_loss, targeted_loss = evaluate(val_main_ds), evaluate(val_targeted_ds)

                # BOTH conditions, and the first is a veto. A checkpoint that fixes the targeted
                # composition by damaging the general one is not a correction.
                regression = main_loss - baseline_main
                may_regress = TCFG["main_validation_regression_allowance"]
                improved_targeted = targeted_loss < best["targeted"] - TCFG["min_delta"]
                acceptable_main = regression <= may_regress

                if improved_targeted and acceptable_main:
                    best = {"main": main_loss, "targeted": targeted_loss, "step": step}
                    since_improve = 0
                    save_state("best", step, epoch, best, since_improve)
                else:
                    since_improve += 1

                record(step=step, epoch=epoch,
                       valMain=round(main_loss, 4), valTargeted=round(targeted_loss, 4),
                       mainRegression=round(regression, 4),
                       acceptableMain=acceptable_main, improvedTargeted=improved_targeted,
                       bestStep=best["step"], sinceImprove=since_improve)

                if since_improve >= TCFG["early_stopping_patience_evals"]:
                    record(event="early-stop", step=step, bestStep=best["step"])
                    stopped = True
                    break

    record(event="done", step=step, bestStep=best["step"],
           bestMain=round(best["main"], 4), bestTargeted=round(best["targeted"], 4),
           earlyStopped=stopped)

    import shutil
    final = OUT / "adapter-final"
    if final.exists():
        shutil.rmtree(final)
    shutil.copytree(OUT / "checkpoint-best", final)
    if (final / "trainer-state.pt").exists():
        (final / "trainer-state.pt").unlink()
    tokenizer.save_pretrained(final)
    print(f"done: step {step}, selected {best['step']} "
          f"(main {best['main']:.4f}, targeted {best['targeted']:.4f}) -> {final}")


if __name__ == "__main__":
    main()
