"""Download the run-1a base model and pin its exact revision for the freeze."""
import json
from pathlib import Path

from huggingface_hub import HfApi, snapshot_download

ROOT = Path(__file__).parent
DEST = ROOT / "models" / "Qwen2.5-3B-Instruct"
REPO = "Qwen/Qwen2.5-3B-Instruct"

info = HfApi().model_info(REPO)
print(f"{REPO} @ {info.sha}")
path = snapshot_download(REPO, revision=info.sha, local_dir=DEST)
print(f"downloaded -> {path}")

pin = {"repo": REPO, "revision": info.sha}
(ROOT / "dataset" / "base-model-pin.json").write_text(json.dumps(pin, indent=2) + "\n", encoding="utf-8")
print(f"pinned: {pin}")
