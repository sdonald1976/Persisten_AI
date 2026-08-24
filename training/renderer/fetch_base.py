"""Download the run-1a base model and pin its exact revision for the freeze."""
import json
from pathlib import Path

from huggingface_hub import HfApi, snapshot_download

ROOT = Path(__file__).parent
DEST = ROOT / "models" / "Qwen2.5-3B-Instruct"
REPO = "Qwen/Qwen2.5-3B-Instruct"

# An existing pin wins: on a second machine this reproduces the EXACT base the
# adapters were trained against, rather than whatever HF is serving today.
pin_file = ROOT / "dataset" / "base-model-pin.json"
if pin_file.exists():
    revision = json.loads(pin_file.read_text(encoding="utf-8"))["revision"]
    print(f"{REPO} @ {revision} (from existing pin)")
else:
    revision = HfApi().model_info(REPO).sha
    print(f"{REPO} @ {revision} (latest; pinning it now)")
    pin_file.write_text(json.dumps({"repo": REPO, "revision": revision}, indent=2) + "\n",
                        encoding="utf-8")

path = snapshot_download(REPO, revision=revision, local_dir=DEST)
print(f"downloaded -> {path}")
