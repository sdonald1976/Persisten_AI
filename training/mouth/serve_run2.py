"""Serve the Run-2 mouth behind an Ollama-compatible endpoint.

Descended from training/renderer/serve_tuned.py, which serves run-1c the same way. Two things
are added, and both exist because this endpoint is about to sit in a live turn path rather than
in a bench harness:

  * NOTHING LOADS UNVERIFIED. Adapter, base weights and tokenizer are hashed against
    runs/run-2/SHA256SUMS and the training manifest before a single tensor is read, and a Git LFS
    pointer is detected by content rather than inferred from a file's absence. A 130-byte text
    file where an adapter should be is exactly what a fresh clone without LFS produces, and it
    would otherwise load as "no adapter" and quietly serve the base model.

  * IT SAYS WHAT IT IS. /api/identity returns the adapter hash actually loaded, so the caller can
    assert that the weights answering a turn are the weights that were measured, rather than
    trusting that a config string and a running process refer to the same thing.

Decoding matches the evaluation harness: greedy. The comparison that matters is what the model
does, and sampling would make each turn a measurement of luck.

    python serve_run2.py --port 11436
"""
import argparse
import hashlib
import io
import json
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

import torch
from transformers import AutoTokenizer, BitsAndBytesConfig

ROOT = Path(__file__).parent
REPO = ROOT.parent.parent
RENDERER = REPO / "training" / "renderer"
MODEL_DIR = RENDERER / "models" / "Qwen2.5-3B-Instruct"
ADAPTER = ROOT / "runs" / "run-2" / "adapter-final"
MANIFEST = ROOT / "runs" / "run-2" / "training-manifest.json"
SUMS = ROOT / "runs" / "run-2" / "SHA256SUMS"

LFS_POINTER_MAGIC = b"version https://git-lfs.github.com/spec/v1"


def sha256_file(path):
    h = hashlib.sha256()
    with io.open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def sha256_dir(path, patterns):
    h = hashlib.sha256()
    for pattern in patterns:
        for p in sorted(path.glob(pattern)):
            h.update(p.name.encode("utf-8"))
            h.update(sha256_file(p).encode("ascii"))
    return h.hexdigest()


def is_lfs_pointer(path):
    """Content, not existence. An unfetched LFS file is present, small, and text."""
    try:
        with io.open(path, "rb") as f:
            return f.read(len(LFS_POINTER_MAGIC)) == LFS_POINTER_MAGIC
    except OSError:
        return False


def verify():
    """Every artifact, hashed, before anything is loaded. Refuses rather than warns."""
    problems = []

    if not ADAPTER.exists():
        problems.append(f"adapter directory missing: {ADAPTER}")
        return None, problems

    weights = ADAPTER / "adapter_model.safetensors"
    if is_lfs_pointer(weights):
        problems.append(
            f"{weights} is a Git LFS pointer, not weights. Run `git lfs pull` - loading would "
            f"silently serve the base model with no adapter at all.")
        return None, problems

    expected = {}
    for line in io.open(SUMS, encoding="utf-8"):
        line = line.strip()
        if line:
            digest, rel = line.split("  ", 1)
            expected[rel] = digest

    checked = 0
    for rel, digest in expected.items():
        if not rel.startswith("runs/run-2/adapter-final/"):
            continue
        p = ROOT / rel
        if not p.exists():
            problems.append(f"missing: {rel}")
            continue
        actual = sha256_file(p)
        checked += 1
        if actual != digest:
            problems.append(f"HASH MISMATCH {rel}\n    expected {digest}\n    actual   {actual}")

    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    base_actual = sha256_dir(MODEL_DIR, ["*.safetensors"])
    if base_actual != manifest["baseModel"]["weightsSha256"]:
        problems.append(
            f"BASE WEIGHTS MISMATCH\n    expected {manifest['baseModel']['weightsSha256']}"
            f"\n    actual   {base_actual}")

    tok_actual = sha256_dir(
        MODEL_DIR,
        ["tokenizer.json", "tokenizer_config.json", "vocab.json", "merges.txt",
         "special_tokens_map.json", "generation_config.json"])
    if tok_actual != manifest["tokenizer"]["sha256"]:
        problems.append(
            f"TOKENIZER MISMATCH\n    expected {manifest['tokenizer']['sha256']}"
            f"\n    actual   {tok_actual}")

    identity = {
        "adapter": "run-2",
        "adapterSha256": sha256_file(weights),
        "adapterFilesVerified": checked,
        "baseModel": manifest["baseModel"]["id"],
        "baseRevision": manifest["baseModel"]["revision"],
        "baseWeightsSha256": base_actual,
        "tokenizerSha256": tok_actual,
        "promptFormat": "mouth-prompt/4.0",
        "selectedCheckpointStep": 180,
        "corpusCommit": manifest["repository"]["corpusApprovedAt"],
    }
    return identity, problems


parser = argparse.ArgumentParser()
parser.add_argument("--port", type=int, default=11436)
parser.add_argument("--verify-only", action="store_true")
args = parser.parse_args()

IDENTITY, PROBLEMS = verify()
if PROBLEMS:
    print("REFUSING TO LOAD - artifact verification failed:", file=sys.stderr)
    for p in PROBLEMS:
        print("  " + p, file=sys.stderr)
    raise SystemExit(2)

print("artifact verification passed")
for k, v in IDENTITY.items():
    print(f"  {k:24}{v}")
if args.verify_only:
    raise SystemExit(0)

COLD_START_BEGAN = time.perf_counter()
tokenizer = AutoTokenizer.from_pretrained(MODEL_DIR)
sys.path.insert(0, str(RENDERER))
from train_run1a import load_base   # the same mmap-bypassing loader, single source

bnb = BitsAndBytesConfig(load_in_4bit=True, bnb_4bit_quant_type="nf4",
                         bnb_4bit_use_double_quant=True, bnb_4bit_compute_dtype=torch.float16)
model = load_base(MODEL_DIR, bnb)
from peft import PeftModel
model = PeftModel.from_pretrained(model, str(ADAPTER))
model.eval()
COLD_START_SEC = time.perf_counter() - COLD_START_BEGAN
IDENTITY["coldStartSec"] = round(COLD_START_SEC, 2)
print(f"cold start: {COLD_START_SEC:.1f}s")

# One generation call at a time. The model is not reentrant and the turn path is allowed to be
# slow, never wrong.
LOCK = threading.Lock()


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *a):
        pass

    def handle_one_request(self):
        """A client that gives up mid-reply must not take the server down with it.

        The API being stopped while a shadow render was in flight raised ConnectionAbortedError
        out of the socket write and killed the serving process - so the next turn's shadow failed
        for a reason that had nothing to do with the model."""
        try:
            super().handle_one_request()
        except (ConnectionAbortedError, ConnectionResetError, BrokenPipeError):
            self.close_connection = True

    def _json(self, obj, code=200):
        body = json.dumps(obj).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path.startswith("/api/identity"):
            self._json(IDENTITY)
        elif self.path.startswith("/api/ps"):
            self._json({"models": [{
                "name": "run-2",
                "size_vram": torch.cuda.memory_allocated(),
                "peak_vram": torch.cuda.max_memory_allocated()}]})
        elif self.path.startswith("/api/version"):
            self._json({"version": "run-2-mouth"})
        else:
            self._json({"error": "not found"}, 404)

    def do_POST(self):
        if not self.path.startswith("/api/chat"):
            self._json({"error": "not found"}, 404)
            return
        length = int(self.headers.get("Content-Length", 0))
        req = json.loads(self.rfile.read(length))
        options = req.get("options") or {}
        num_predict = options.get("num_predict", 220)

        # Decoding is a SERVING parameter, so the grid can be measured without touching prompts,
        # weights or corpus. Greedy stays the default, because that is what every measurement so
        # far was taken under and a default that drifts makes old numbers incomparable.
        temperature = float(options.get("temperature", 0.0))
        top_p = float(options.get("top_p", 1.0))
        repetition_penalty = float(options.get("repetition_penalty", 1.0))
        no_repeat_ngram = int(options.get("no_repeat_ngram_size", 0))
        sample = temperature > 0

        gen_kwargs = dict(
            max_new_tokens=num_predict,
            do_sample=sample,
            pad_token_id=tokenizer.pad_token_id or tokenizer.eos_token_id,
        )
        if sample:
            gen_kwargs["temperature"] = temperature
            gen_kwargs["top_p"] = top_p
        if repetition_penalty != 1.0:
            gen_kwargs["repetition_penalty"] = repetition_penalty
        if no_repeat_ngram > 0:
            gen_kwargs["no_repeat_ngram_size"] = no_repeat_ngram

        # A seed per request, so a sampled configuration is still reproducible: the same request
        # to the same configuration returns the same reply, and a grid can be re-run.
        seed = int(options.get("seed", 20260830))

        t0 = time.perf_counter()
        with LOCK:
            if sample:
                torch.manual_seed(seed)
            ids = tokenizer.apply_chat_template(
                req["messages"], tokenize=True, add_generation_prompt=True,
                return_tensors="pt").cuda()
            prompt_done = time.perf_counter()
            with torch.no_grad():
                out = model.generate(ids, **gen_kwargs)
            gen_done = time.perf_counter()
            new_tokens = out[0][ids.shape[1]:]
            text = tokenizer.decode(new_tokens, skip_special_tokens=True).strip()

        ns = lambda s: int(s * 1e9)
        self._json({
            "message": {"role": "assistant", "content": text},
            "done": True,
            "adapter_sha256": IDENTITY["adapterSha256"],
            "load_duration": 0,
            "prompt_eval_duration": ns(prompt_done - t0),
            "eval_count": int(new_tokens.shape[0]),
            "eval_duration": ns(gen_done - prompt_done),
            "total_duration": ns(gen_done - t0),
        })


print(f"serving run-2 on http://127.0.0.1:{args.port} (Ctrl+C to stop)", flush=True)
ThreadingHTTPServer(("127.0.0.1", args.port), Handler).serve_forever()
