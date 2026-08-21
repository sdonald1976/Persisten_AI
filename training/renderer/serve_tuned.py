"""Minimal Ollama-compatible endpoint for evaluating run-1a checkpoints.

Serves POST /api/chat and GET /api/ps exactly as far as Companion.RendererBench needs,
backed by transformers + the NF4-quantized base with (optionally) a LoRA adapter.
This lets the SAME bench binary that produced every baseline score the tuned model —
the measuring instrument does not change mid-experiment.

  python serve_tuned.py --adapter runs/run-1a/adapter-final --port 11435
  python serve_tuned.py --port 11435                # base model, prompted (control arm)

Latency caveat, recorded here on purpose: this stack is transformers-python, not
Ollama-GGUF, so absolute tok/s is not comparable to the Ollama baselines. The
latency/VRAM gate is judged RELATIVELY: tuned-vs-base through this same server.
"""
import argparse
import json
import time
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path

import torch
from transformers import AutoModelForCausalLM, AutoTokenizer, BitsAndBytesConfig

ROOT = Path(__file__).parent
MODEL_DIR = ROOT / "models" / "Qwen2.5-3B-Instruct"

parser = argparse.ArgumentParser()
parser.add_argument("--adapter", default=None)
parser.add_argument("--port", type=int, default=11435)
args = parser.parse_args()

print(f"loading {'base + ' + args.adapter if args.adapter else 'base (prompted control)'}")
tokenizer = AutoTokenizer.from_pretrained(MODEL_DIR)
bnb = BitsAndBytesConfig(load_in_4bit=True, bnb_4bit_quant_type="nf4",
                         bnb_4bit_use_double_quant=True, bnb_4bit_compute_dtype=torch.float16)
model = AutoModelForCausalLM.from_pretrained(
    MODEL_DIR, quantization_config=bnb, dtype=torch.float16, device_map={"": 0})
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
            vram = torch.cuda.memory_allocated()
            self._json({"models": [{"name": "run-1a", "size_vram": vram}]})
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
            req["messages"], tokenize=True, add_generation_prompt=True, return_tensors="pt").cuda()
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
