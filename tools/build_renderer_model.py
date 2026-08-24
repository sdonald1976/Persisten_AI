"""From-scratch renderer build for any machine: base model -> merge -> Ollama model.

Run from the repo root, with the training venv's python (see
training/renderer/requirements-serve.txt for creating it; CPU torch is fine —
merging is weight addition, no inference):

  python tools/build_renderer_model.py

Steps, each skipped if already done:
  1. Download the PINNED base model (training/renderer/fetch_base.py honors the pin).
  2. Merge the run-1c adapter into it (training/renderer/merge_adapter.py, hashes recorded).
  3. `ollama create renderer-shadow -q q8_0` from the merged weights.

After this, the app's renderer canary/shadow works against plain Ollama
(Endpoint http://localhost:11434) — no separate server window, any GPU vendor
or none. On a GPU too small for the chat model plus the renderer, set
Companion:RendererShadow:NumGpu to 0 to pin the renderer to CPU.
"""
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
RENDERER = ROOT / "training" / "renderer"
MERGED = RENDERER / "merged" / "run-1c"

def run(cmd, **kw):
    print("+", " ".join(str(c) for c in cmd))
    subprocess.run(cmd, check=True, **kw)

if not (RENDERER / "models" / "Qwen2.5-3B-Instruct" / "config.json").exists():
    run([sys.executable, str(RENDERER / "fetch_base.py")], cwd=RENDERER)
else:
    print("base model present; skipping download")

if not (MERGED / "merge-record.json").exists():
    run([sys.executable, str(RENDERER / "merge_adapter.py"),
         "--adapter", "runs/run-1c/adapter-final", "--out", "merged/run-1c"], cwd=RENDERER)
else:
    print("merged model present; skipping merge")

ollama = shutil.which("ollama") or r"C:\Users\%USERNAME%\AppData\Local\Programs\Ollama\ollama.exe"
modelfile = RENDERER / "merged" / "Modelfile.run1c"
modelfile.write_text(f"FROM {MERGED.as_posix()}\n", encoding="utf-8")
run([ollama, "create", "renderer-shadow", "-q", "q8_0", "-f", str(modelfile)])
print("\ndone: Ollama model 'renderer-shadow' is ready; point Companion:RendererShadow:Endpoint at http://localhost:11434")
