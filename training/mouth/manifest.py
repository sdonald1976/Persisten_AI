"""Record everything needed to reproduce or audit the Run-2 training run.

Identity, not description. A manifest that says "Qwen2.5-3B" and "the frozen corpus" documents
nothing checkable; every field here is a hash, a revision, a version string or a measured number,
so a later reader can verify the claim rather than take it.
"""
import hashlib
import io
import json
import platform
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).parent
REPO = ROOT.parent.parent
DATASET = ROOT / "dataset"
MODEL_DIR = REPO / "training" / "renderer" / "models" / "Qwen2.5-3B-Instruct"


def sha256_file(path):
    h = hashlib.sha256()
    with io.open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def sha256_dir(path, patterns):
    """One hash over several files, ordered by name so it is stable."""
    h = hashlib.sha256()
    for pattern in patterns:
        for p in sorted(path.glob(pattern)):
            h.update(p.name.encode("utf-8"))
            h.update(sha256_file(p).encode("ascii"))
    return h.hexdigest()


def git(*args):
    return subprocess.run(["git", "-C", str(REPO), *args],
                          capture_output=True, text=True).stdout.strip()


def main():
    import torch
    import transformers
    import peft
    import bitsandbytes

    cfg = json.loads((ROOT / "config-run2.json").read_text(encoding="utf-8"))

    corpus = {}
    for name in ("mouth-v2-train", "mouth-v2-validation", "mouth-v2-test", "mouth-v2-hard-eval"):
        p = DATASET / f"{name}.jsonl"
        rows = sum(1 for l in io.open(p, encoding="utf-8-sig") if l.strip())
        corpus[name] = {"sha256": sha256_file(p), "rows": rows}

    selection = json.loads((DATASET / "selection.json").read_text(encoding="utf-8"))

    gpu = {}
    if torch.cuda.is_available():
        props = torch.cuda.get_device_properties(0)
        gpu = {
            "name": props.name,
            "totalMiB": props.total_memory // (1024 * 1024),
            "computeCapability": f"{props.major}.{props.minor}",
            "driver": subprocess.run(
                ["nvidia-smi", "--query-gpu=driver_version", "--format=csv,noheader"],
                capture_output=True, text=True).stdout.strip(),
        }

    manifest = {
        "runId": cfg["runId"],
        "repository": {
            "commit": git("rev-parse", "HEAD"),
            "branch": git("rev-parse", "--abbrev-ref", "HEAD"),
            "treeClean": git("status", "--porcelain") == "",
            "corpusApprovedAt": cfg["corpus"]["repoCommit"],
        },
        "corpus": {
            "files": corpus,
            "candidatePoolHash": selection["candidatePoolHash"],
            "selectionHash": selection["selectionHash"],
            "selectionAlgorithm": selection["algorithm"],
            "selectionSeed": selection["seed"],
            "promptFormat": cfg["corpus"]["promptFormat"],
        },
        "baseModel": {
            "id": cfg["baseModel"]["id"],
            "revision": cfg["baseModel"]["revision"],
            "localPath": str(MODEL_DIR),
            "weightsSha256": sha256_dir(MODEL_DIR, ["*.safetensors"]),
            "configSha256": sha256_file(MODEL_DIR / "config.json"),
        },
        "tokenizer": {
            "sha256": sha256_dir(
                MODEL_DIR,
                ["tokenizer.json", "tokenizer_config.json", "vocab.json", "merges.txt",
                 "special_tokens_map.json", "generation_config.json"]),
            "files": sorted(
                p.name for p in MODEL_DIR.iterdir()
                if p.name.startswith(("tokenizer", "vocab", "merges", "special_tokens",
                                      "generation_config"))),
        },
        "environment": {
            "python": sys.version.split()[0],
            "platform": platform.platform(),
            "torch": torch.__version__,
            "transformers": transformers.__version__,
            "peft": peft.__version__,
            "bitsandbytes": bitsandbytes.__version__,
            "cuda": torch.version.cuda,
            "cudnn": torch.backends.cudnn.version(),
        },
        "gpu": gpu,
        "seed": cfg["training"]["seed"],
        "hyperparameters": {
            "quantization": cfg["quantization"],
            "lora": cfg["lora"],
            "training": cfg["training"],
        },
        "deviationsFromRun1c": cfg["deviations"],
    }

    out = ROOT / "runs" / cfg["runId"] / "training-manifest.json"
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(manifest, indent=2))
    print(f"\n-> {out}")


if __name__ == "__main__":
    main()
