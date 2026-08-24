"""CPU-portable renderer server: the exact validated adapter, no CUDA required.

Same Ollama-mimic wire contract as serve_tuned.py (POST /api/chat, GET /api/ps) so
the shadow/canary service and every eval script work unchanged. Differences, stated
honestly: the base loads unquantized (bf16 where the CPU supports it, fp32
otherwise) instead of the NF4-on-GPU configuration every published eval ran —
same adapter weights, slightly different numerics, and CPU-speed generation
(expect seconds-per-reply, not tokens-per-second bragging rights).

  python serve_cpu.py --adapter runs/run-1c/adapter-final --port 11435

Needs ~8 GB free RAM (bf16) or ~13 GB (fp32). serve_tuned.py remains the
frozen-manifest server for CUDA machines; this file is deployment plumbing.
"""
import argparse
import glob
import json
import time
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path

import torch
from safetensors.torch import load_file
from transformers import AutoConfig, AutoTokenizer, Qwen2ForCausalLM

ROOT = Path(__file__).parent
MODEL_DIR = ROOT / "models" / "Qwen2.5-3B-Instruct"

parser = argparse.ArgumentParser()
parser.add_argument("--adapter", default=None)
parser.add_argument("--port", type=int, default=11435)
parser.add_argument("--dtype", choices=["auto", "bf16", "fp32"], default="auto")
args = parser.parse_args()

if args.dtype == "auto":
    # bf16 halves memory and is fast on modern x86; fall back to fp32 where the
    # CPU's bf16 matmul support is absent or unconvincing.
    use_bf16 = torch.backends.cpu.is_avx512_bf16_supported() if hasattr(torch.backends.cpu, "is_avx512_bf16_supported") else False
    dtype = torch.bfloat16 if use_bf16 else torch.float32
else:
    dtype = torch.bfloat16 if args.dtype == "bf16" else torch.float32

print(f"loading {'base + ' + args.adapter if args.adapter else 'base (prompted control)'} on CPU ({dtype})")
tokenizer = AutoTokenizer.from_pretrained(MODEL_DIR)

# Same shard-loader bypass as train_run1a.load_base, minus the quantization: the
# state dict is read whole by safetensors and handed over.
sd = {}
for f in sorted(glob.glob(str(MODEL_DIR / "*.safetensors"))):
    sd.update(load_file(f))
cfg = AutoConfig.from_pretrained(MODEL_DIR)
model = Qwen2ForCausalLM.from_pretrained(None, config=cfg, state_dict=sd, dtype=dtype)
del sd
if args.adapter:
    from peft import PeftModel
    model = PeftModel.from_pretrained(model, args.adapter)
model.eval()
torch.manual_seed(20260821)


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def _json(self, obj, code=200):
        body = json.dumps(obj).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path.startswith("/api/ps"):
            self._json({"models": [{"name": "run-1c-cpu", "size_vram": 0}]})
        else:
            self._json({"error": "not found"}, 404)

    def do_POST(self):
        if not self.path.startswith("/api/chat"):
            self._json({"error": "not found"}, 404)
            return
        length = int(self.headers.get("Content-Length", 0))
        req = json.loads(self.rfile.read(length))
        options = req.get("options") or {}
        temperature = options.get("temperature", 0.6)
        num_predict = options.get("num_predict", 220)

        t_start = time.perf_counter()
        ids = tokenizer.apply_chat_template(
            req["messages"], tokenize=True, add_generation_prompt=True, return_tensors="pt")
        prompt_done = time.perf_counter()
        with torch.no_grad():
            out = model.generate(
                ids, max_new_tokens=num_predict, do_sample=temperature > 0,
                temperature=max(temperature, 1e-5), top_p=0.9,
                pad_token_id=tokenizer.pad_token_id or tokenizer.eos_token_id)
        gen_done = time.perf_counter()
        new_tokens = out[0][ids.shape[1]:]
        text = tokenizer.decode(new_tokens, skip_special_tokens=True).strip()

        ns = lambda s: int(s * 1e9)
        self._json({
            "message": {"role": "assistant", "content": text},
            "done": True,
            "load_duration": 0,
            "prompt_eval_duration": ns(prompt_done - t_start),
            "eval_count": int(new_tokens.shape[0]),
            "eval_duration": ns(gen_done - prompt_done),
            "total_duration": ns(gen_done - t_start),
        })


print(f"serving on http://localhost:{args.port} (Ctrl+C to stop)")
HTTPServer(("127.0.0.1", args.port), Handler).serve_forever()
