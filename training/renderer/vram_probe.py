"""VRAM / base feasibility probe — measurement only. Trains nothing that is kept.

Answers one question: how much curriculum fits in a LoRA adapter on this card, so the
distillation target is sized by the hardware rather than guessed at and discovered later.

Runs a handful of real optimizer steps per cell and reports peak VRAM, throughput, and
projected wall time for the provisional 44–57k corpus. Every adapter is discarded.

Crucially it distinguishes the two failure modes that look alike in a plan and are nothing
alike in practice:

  OOM          — the cell does not fit at all. No schedule rescues it.
  IMPRACTICAL  — it fits and is too slow to finish in the available off-hours window.

Usage (after Ava is stopped and the GPU is free):
    .venv-train/Scripts/python.exe training/renderer/vram_probe.py
"""
import argparse
import gc
import json
import time
from pathlib import Path

import torch
from transformers import AutoModelForCausalLM, AutoTokenizer, BitsAndBytesConfig
from peft import LoraConfig, get_peft_model, prepare_model_for_kbit_training

# The provisional corpus from RUN2_CURRICULUM_R5, and the off-hours budget it must fit in.
CORPUS_ROWS = (44_000, 57_000)
EPOCHS = 2
OFFHOURS_HOURS_PER_NIGHT = 8
IMPRACTICAL_NIGHTS = 14          # beyond this, a "feasible" cell is not actually usable.

# A loaded 3B model costs gigabytes; the Windows compositor on a display GPU costs a few
# hundred MiB. Anything above this is a model that was not stopped.
OCCUPIED_MIB_MEANS_MODEL_RESIDENT = 1024

# Layer A7b's declared context buckets, in transcript turns — what truncation is measured
# against, since fiction turns carry both the longest windows and the largest frames.
FAMILY_TOKENS = {
    "A1 everyday": 320,
    "A6 intimacy": 420,
    "A7a single-turn fiction": 520,
    "A7b sustained (medium)": 900,
    "A7b sustained (long)": 1500,
    "A7b sustained (very long)": 2600,
    "B protocol control": 480,
    "B11 frame control": 700,
}

BASES = {
    "qwen2.5-3b-instruct": "Qwen/Qwen2.5-3B-Instruct",
    # The declared roleplay-capable comparison: a renderer that must render fiction may need
    # a base with that disposition rather than a general instruct model. Sourced from the
    # ungated mirror — identical weights, but meta-llama/* requires an access grant this
    # machine does not hold, and a gated 401 would read as a capability finding it is not.
    "llama-3.2-3b-instruct": "NousResearch/Llama-3.2-3B-Instruct",
}


def free_gpu() -> None:
    gc.collect()
    torch.cuda.empty_cache()
    torch.cuda.reset_peak_memory_stats()


def probe_cell(base_id: str, rank: int, seq_len: int, steps: int, batch: int, accum: int):
    """One (base, rank, seq_len) cell. Returns a result dict; never raises for OOM."""
    free_gpu()
    result = {"rank": rank, "seqLen": seq_len, "batch": batch, "accum": accum}
    try:
        quant = BitsAndBytesConfig(
            load_in_4bit=True, bnb_4bit_quant_type="nf4",
            bnb_4bit_use_double_quant=True, bnb_4bit_compute_dtype=torch.float16)

        tok = AutoTokenizer.from_pretrained(base_id)
        model = AutoModelForCausalLM.from_pretrained(
            base_id, quantization_config=quant, device_map={"": 0}, torch_dtype=torch.float16)
        model = prepare_model_for_kbit_training(model, use_gradient_checkpointing=True)
        model.gradient_checkpointing_enable()
        model = get_peft_model(model, LoraConfig(
            r=rank, lora_alpha=rank * 2, lora_dropout=0.05, bias="none",
            task_type="CAUSAL_LM",
            target_modules=["q_proj", "k_proj", "v_proj", "o_proj",
                            "gate_proj", "up_proj", "down_proj"]))
        model.train()

        opt = torch.optim.AdamW((p for p in model.parameters() if p.requires_grad), lr=1e-4)
        ids = torch.randint(0, tok.vocab_size, (batch, seq_len), device="cuda")

        # One warm-up step so allocator behaviour, not first-touch cost, is what is timed.
        loss = model(input_ids=ids, labels=ids).loss
        loss.backward()
        opt.step()
        opt.zero_grad(set_to_none=True)
        torch.cuda.synchronize()

        start = time.perf_counter()
        for i in range(steps):
            loss = model(input_ids=ids, labels=ids).loss
            (loss / accum).backward()
            if (i + 1) % accum == 0:
                opt.step()
                opt.zero_grad(set_to_none=True)
        torch.cuda.synchronize()
        elapsed = time.perf_counter() - start

        tokens = steps * batch * seq_len
        total_mib = torch.cuda.get_device_properties(0).total_memory / 2**20
        peak_mib = torch.cuda.max_memory_allocated() / 2**20
        reserved_mib = torch.cuda.max_memory_reserved() / 2**20
        # On Windows, WDDM lets CUDA oversubscribe into system RAM rather than raising
        # OutOfMemoryError. Such a cell reports success and is not a fit: it is paging over
        # PCIe every step. Calling that "ok" would mean this probe can never emit OOM at all.
        spilled = max(peak_mib, reserved_mib) > total_mib
        result.update({
            "status": "spilled" if spilled else "ok",
            "spilledToHostRam": spilled,
            "peakVramMiB": round(peak_mib),
            "reservedMiB": round(reserved_mib),
            "tokensPerSec": round(tokens / elapsed, 1),
            "secPerStep": round(elapsed / steps, 3),
        })
        del model, opt, ids
    except torch.cuda.OutOfMemoryError:
        result.update({"status": "OOM"})
    except Exception as exc:                                   # noqa: BLE001
        result.update({"status": "error", "error": f"{type(exc).__name__}: {exc}"})
    free_gpu()
    return result


def project(cell: dict) -> dict:
    """Wall time for the provisional corpus, and whether it is practically reachable."""
    if cell.get("status") not in ("ok", "spilled"):
        return cell

    tps = cell["tokensPerSec"]
    out = {}
    for label, rows in (("low", CORPUS_ROWS[0]), ("high", CORPUS_ROWS[1])):
        # Rows are padded to the cell's sequence length in this estimate, which is the
        # pessimistic end; real packing does better.
        hours = rows * EPOCHS * cell["seqLen"] / tps / 3600
        out[f"hours{label.capitalize()}"] = round(hours, 1)
        out[f"nights{label.capitalize()}"] = round(hours / OFFHOURS_HOURS_PER_NIGHT, 1)

    cell.update(out)
    # The distinction that matters: fits-but-unusable is not the same as does-not-fit.
    if cell["nightsHigh"] > IMPRACTICAL_NIGHTS:
        cell["verdict"] = "IMPRACTICAL"
    elif cell.get("spilledToHostRam"):
        # It completed, but only by paging to host RAM. Not a fit on this card.
        cell["verdict"] = "DOES-NOT-FIT (host-RAM spill)"
    else:
        cell["verdict"] = "FEASIBLE"
    return cell


def truncation(seq_len: int) -> dict:
    """Which curriculum families lose content at this sequence length."""
    return {
        family: ("fits" if need <= seq_len else f"TRUNCATES (~{need} tok)")
        for family, need in FAMILY_TOKENS.items()
    }


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--steps", type=int, default=12)
    ap.add_argument("--out", default="training/renderer/vram-probe-results.json")
    args = ap.parse_args()

    if not torch.cuda.is_available():
        raise SystemExit("no CUDA device visible — is Ollama still holding the GPU?")

    free = torch.cuda.mem_get_info()[0] / 2**20
    total = torch.cuda.get_device_properties(0).total_memory / 2**20
    print(f"GPU: {torch.cuda.get_device_name(0)}  {free:.0f} / {total:.0f} MiB free")
    # The hazard is a resident LLM, which costs gigabytes — not the few hundred MiB the
    # Windows desktop compositor holds on a card that also drives a monitor. Gate on the
    # absolute occupancy that only a loaded model can explain.
    if total - free > OCCUPIED_MIB_MEANS_MODEL_RESIDENT:
        raise SystemExit(
            f"{total - free:.0f} MiB already occupied — a model is probably still resident. "
            "Stop it before probing, or the results measure contention rather than capacity")

    # Resolve every base before measuring anything. Otherwise an unreachable or misnamed
    # repo yields one error row per cell, which reads like a result and is not one.
    from transformers import AutoConfig
    unresolved = []
    for base_name, base_id in BASES.items():
        try:
            AutoConfig.from_pretrained(base_id)
        except Exception as exc:                               # noqa: BLE001
            unresolved.append(f"  {base_name} -> {base_id}: {type(exc).__name__}")
    if unresolved:
        raise SystemExit(
            "these bases do not resolve; fix or remove them before probing:" + chr(10)
            + chr(10).join(unresolved))

    results = {"gpu": torch.cuda.get_device_name(0), "totalMiB": round(total), "cells": []}

    for base_name, base_id in BASES.items():
        for seq_len in (1024, 2048):
            for rank in (16, 32, 64):
                print(f"  {base_name} r{rank} seq{seq_len} ...", flush=True)
                cell = project(probe_cell(base_id, rank, seq_len, args.steps, 1, 8))
                cell["base"] = base_name
                cell["truncation"] = truncation(seq_len)
                results["cells"].append(cell)
                print(f"    {cell.get('status')} "
                      f"peak={cell.get('peakVramMiB', '-')}MiB "
                      f"tps={cell.get('tokensPerSec', '-')} "
                      f"verdict={cell.get('verdict', '-')}", flush=True)

    Path(args.out).write_text(json.dumps(results, indent=2), encoding="utf-8")
    print(f"\nwritten: {args.out}")


if __name__ == "__main__":
    main()
