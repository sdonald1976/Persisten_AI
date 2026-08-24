"""Merges a LoRA adapter into the pinned base model, producing a standalone
safetensors model for GGUF conversion / Ollama import.

The adapter in git remains the canonical artifact; this output is a DERIVED
build product (docs/RENDERER_SHADOW.md discipline). Runs on CPU — merging is
weight addition, no inference — so it works on any machine with ~14 GB RAM.

  python merge_adapter.py --adapter runs/run-1c/adapter-final --out merged/run-1c

Prints sha256 of every written shard for the build record.
"""
import argparse
import glob
import hashlib
import json
from pathlib import Path

import torch
from peft import PeftModel
from safetensors.torch import load_file
from transformers import AutoConfig, AutoTokenizer, Qwen2ForCausalLM

ROOT = Path(__file__).parent
MODEL_DIR = ROOT / "models" / "Qwen2.5-3B-Instruct"

parser = argparse.ArgumentParser()
parser.add_argument("--adapter", required=True)
parser.add_argument("--out", required=True)
args = parser.parse_args()

pin = json.loads((ROOT / "dataset" / "base-model-pin.json").read_text(encoding="utf-8"))
print(f"base: {pin['repo']} @ {pin['revision']}")

adapter_sha = hashlib.sha256(
    (Path(args.adapter) / "adapter_model.safetensors").read_bytes()).hexdigest()
print(f"adapter: {args.adapter} sha256={adapter_sha}")

sd = {}
for f in sorted(glob.glob(str(MODEL_DIR / "*.safetensors"))):
    sd.update(load_file(f))
cfg = AutoConfig.from_pretrained(MODEL_DIR)
model = Qwen2ForCausalLM.from_pretrained(None, config=cfg, state_dict=sd, dtype=torch.float16)
del sd

model = PeftModel.from_pretrained(model, args.adapter)
model = model.merge_and_unload()

out = Path(args.out)
out.mkdir(parents=True, exist_ok=True)
model.save_pretrained(out, safe_serialization=True)
AutoTokenizer.from_pretrained(MODEL_DIR).save_pretrained(out)

record = {
    "base": pin,
    "adapterPath": str(args.adapter),
    "adapterSha256": adapter_sha,
    "mergedShards": {},
}
for f in sorted(out.glob("*.safetensors")):
    h = hashlib.sha256(f.read_bytes()).hexdigest()
    record["mergedShards"][f.name] = h
    print(f"merged shard {f.name} sha256={h}")
(out / "merge-record.json").write_text(json.dumps(record, indent=1), encoding="utf-8")
print(f"merged model -> {out}")
