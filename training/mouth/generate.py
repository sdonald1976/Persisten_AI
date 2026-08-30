"""Generate one reply per corpus row, for a named arm.

Arms are the untouched base, a Run-1 adapter, or the Run-2 adapter. Every arm sees exactly the
same bytes - the row's own `system` and `input`, which are what MouthPromptV4 emits - so a
difference between arms is a difference in weights and nothing else.

Decoding is greedy. Sampling would make the comparison a measurement of luck, and the question
being asked is what the model does, not what it might do.

    python generate.py --split test --arm run-2 --adapter runs/run-2/adapter-final
    python generate.py --split validation --arm base
"""
import argparse
import glob
import io
import json
import time
from pathlib import Path

import torch
from safetensors.torch import load_file
from transformers import (AutoConfig, AutoTokenizer, BitsAndBytesConfig,
                          Qwen2ForCausalLM)

ROOT = Path(__file__).parent
REPO = ROOT.parent.parent
DATASET = ROOT / "dataset"
MODEL_DIR = REPO / "training" / "renderer" / "models" / "Qwen2.5-3B-Instruct"


def read_rows(name):
    return [json.loads(l) for l in io.open(DATASET / name, encoding="utf-8-sig") if l.strip()]


def load(adapter):
    bnb = BitsAndBytesConfig(
        load_in_4bit=True, bnb_4bit_quant_type="nf4",
        bnb_4bit_use_double_quant=True, bnb_4bit_compute_dtype=torch.float16)
    sd = {}
    for f in sorted(glob.glob(str(MODEL_DIR / "*.safetensors"))):
        sd.update(load_file(f))
    cfg = AutoConfig.from_pretrained(MODEL_DIR)
    model = Qwen2ForCausalLM.from_pretrained(
        None, config=cfg, state_dict=sd, quantization_config=bnb,
        dtype=torch.float16, device_map={"": 0})
    if adapter:
        from peft import PeftModel
        model = PeftModel.from_pretrained(model, adapter)
    model.eval()
    return model


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--split", required=True)
    ap.add_argument("--file", default=None,
                    help="explicit rows file; --split then names the output only")
    ap.add_argument("--arm", required=True)
    ap.add_argument("--adapter", default=None)
    ap.add_argument("--out", default=None)
    ap.add_argument("--max-new-tokens", type=int, default=160)
    args = ap.parse_args()

    rows = ([json.loads(l) for l in io.open(args.file, encoding="utf-8-sig") if l.strip()]
            if args.file else read_rows(f"mouth-v2-{args.split}.jsonl"))
    tokenizer = AutoTokenizer.from_pretrained(MODEL_DIR)
    model = load(args.adapter)

    out_path = Path(args.out or (ROOT / "evaluation" / f"gen-{args.arm}-{args.split}.jsonl"))
    out_path.parent.mkdir(parents=True, exist_ok=True)

    t0 = time.time()
    with io.open(out_path, "w", encoding="utf-8", newline="\n") as f:
        for i, r in enumerate(rows):
            messages = [
                {"role": "system", "content": r["system"]},
                {"role": "user", "content": r["input"]},
            ]
            ids = tokenizer.apply_chat_template(
                messages, tokenize=True, add_generation_prompt=True, return_tensors="pt")
            ids = ids["input_ids"] if not isinstance(ids, torch.Tensor) else ids
            ids = ids.cuda()
            with torch.no_grad():
                out = model.generate(
                    input_ids=ids,
                    max_new_tokens=args.max_new_tokens,
                    do_sample=False,
                    pad_token_id=tokenizer.pad_token_id or tokenizer.eos_token_id)
            text = tokenizer.decode(out[0][ids.shape[1]:], skip_special_tokens=True).strip()
            f.write(json.dumps({"id": r["id"], "target": text}) + "\n")
            if (i + 1) % 25 == 0:
                print(f"  {i + 1}/{len(rows)}  {time.time() - t0:.0f}s", flush=True)

    print(f"{args.arm} / {args.split}: {len(rows)} generations -> {out_path} "
          f"({time.time() - t0:.0f}s)")


if __name__ == "__main__":
    main()
