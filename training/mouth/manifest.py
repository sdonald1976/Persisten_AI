"""Record everything needed to reproduce or audit a mouth training run.

Identity, not description. A manifest that says "Qwen2.5-3B" and "the frozen corpus" documents
nothing checkable; every field here is a hash, a revision, a version string or a measured number,
so a later reader can verify the claim rather than take it.
"""
import argparse
import hashlib
import io
import json
import platform
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
REPO = ROOT.parent.parent
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


def resolve_dir(name):
    """Config directories are written relative to the repo (run-2) or to this folder (run-2.1).

    Both spellings are in configs that are already frozen and referenced by hashes elsewhere, so
    the reader accommodates them rather than the configs being edited to agree.
    """
    for base in (REPO, ROOT):
        if (base / name).is_dir():
            return base / name
    raise FileNotFoundError(f"corpus directory not found under {REPO} or {ROOT}: {name}")


def hash_set(directory, spec):
    """Hash and count every file a config's corpus/supplement block names."""
    files = {}
    names = [spec.get("train"), spec.get("validation"), *spec.get("heldOut", [])]
    for name in [n for n in names if n]:
        p = directory / name
        files[Path(name).stem] = {
            "sha256": sha256_file(p),
            "rows": sum(1 for l in io.open(p, encoding="utf-8-sig") if l.strip()),
        }
    return files


def main():
    import torch
    import transformers
    import peft
    import bitsandbytes

    ap = argparse.ArgumentParser()
    ap.add_argument("--config", default="config-run2.json")
    args = ap.parse_args()
    cfg = json.loads((ROOT / args.config).read_text(encoding="utf-8"))

    # Run-2 named its splits implicitly; Run-2.1 names them in the config because the reissue
    # renamed them. Read the config where it says, and fall back to Run-2's fixed names.
    corpus_cfg = cfg["corpus"]
    dataset = resolve_dir(corpus_cfg.get("directory", "dataset"))
    if corpus_cfg.get("train"):
        corpus = hash_set(dataset, corpus_cfg)
    else:
        corpus = hash_set(dataset, {
            "train": "mouth-v2-train.jsonl", "validation": "mouth-v2-validation.jsonl",
            "heldOut": ["mouth-v2-test.jsonl", "mouth-v2-hard-eval.jsonl"]})

    # selection.json belongs to the ORIGINAL freeze. A reissue inherits its selection rather
    # than making a new one, so a run-2.1 manifest still points at run-2's selection hashes.
    selection_path = dataset / "selection.json"
    if not selection_path.exists():
        selection_path = ROOT / "dataset" / "selection.json"
    selection = json.loads(selection_path.read_text(encoding="utf-8"))

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
            "corpusApprovedAt": corpus_cfg.get("repoCommit"),
        },
        "protocolHash": cfg.get("protocolHash"),
        "continuesFrom": cfg.get("continuesFrom"),
        "corpus": {
            "directory": corpus_cfg.get("directory", "dataset"),
            "files": corpus,
            "candidatePoolHash": selection["candidatePoolHash"],
            "selectionHash": selection["selectionHash"],
            "selectionAlgorithm": selection["algorithm"],
            "selectionSeed": selection["seed"],
            "promptFormat": corpus_cfg.get("promptFormat", "MouthPromptV4"),
        },
        "supplement": (
            {"directory": cfg["supplement"]["directory"],
             "composition": cfg["supplement"].get("composition"),
             "files": hash_set(resolve_dir(cfg["supplement"]["directory"]), cfg["supplement"])}
            if "supplement" in cfg else None),
        "adapter": {
            "path": f"runs/{cfg['runId']}/adapter-final",
            "sha256": sha256_dir(ROOT / "runs" / cfg["runId"] / "adapter-final",
                                 ["adapter_model.safetensors", "adapter_config.json"]),
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
        "deviations": cfg.get("deviations") or cfg.get("deviationsFromRun2"),
        "deviationsRelativeTo": "run-1c" if "deviations" in cfg else "run-2",
    }

    out = ROOT / "runs" / cfg["runId"] / "training-manifest.json"
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(manifest, indent=2))
    print(f"\n-> {out}")


if __name__ == "__main__":
    main()
