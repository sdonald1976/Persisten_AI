"""Freeze the experiment: hash every input that determines what run 1a IS.

Run with --provisional before approval (records the current state without claiming
it is final) and without the flag at approval time. After a real freeze, changing
any hashed file means the result belongs to a DIFFERENT experiment — failures are
results, not reasons to edit examples.
"""
import hashlib
import json
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).parent
REPO = ROOT.parents[2]

# --run run-1b freezes the run-1b inputs (default remains run-1a for the record).
RUN = sys.argv[sys.argv.index("--run") + 1] if "--run" in sys.argv else "run-1a"
CONFIG_NAME = f"config-{RUN.replace('run-', 'run')}.json"

TARGETS = {
    "dataset": ROOT / "train-200.jsonl",
    "splitManifest": ROOT / "splits.json",
    "trainingConfig": ROOT / CONFIG_NAME,
    "planSerialization": REPO / "tools" / "Companion.RendererBench" / "PlanSerialization.cs",
    "evaluationSuite": REPO / "tools" / "Companion.RendererBench" / "RendererChecks.cs",
    "curationPipeline": ROOT / "curate.py",
    "curationDecisions": ROOT / "curation-run1a.jsonl",
    "canonicalPlan2": ROOT / "plan2-current.jsonl",
    "heldOutBenchmark": REPO / "training" / "renderer" / "fixtures.jsonl",
    "trainingScript": REPO / "training" / "renderer" / "train_run1a.py",
    "evalServer": REPO / "training" / "renderer" / "serve_tuned.py",
    "baseModelPin": ROOT / "base-model-pin.json",
}
if RUN != "run-1a":
    TARGETS["curationDelta"] = ROOT / f"curation-{RUN.replace('run-', 'run')}.jsonl"
    TARGETS["pinnedRun1aSplits"] = ROOT / "splits-run1a-pinned.json"
    for f in sorted((REPO / "training" / "renderer" / "unseen").glob(f"{RUN.replace('run-', 'run')}-*.jsonl")):
        TARGETS[f"unseenFamily:{f.stem}"] = f

def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()

provisional = "--provisional" in sys.argv
entries = {}
for name, path in TARGETS.items():
    if not path.exists():
        entries[name] = {"path": str(path.relative_to(REPO)), "sha256": None, "missing": True}
        continue
    entries[name] = {
        "path": str(path.relative_to(REPO)).replace("\\", "/"),
        "sha256": sha256(path),
        "bytes": path.stat().st_size,
    }

try:
    commit = subprocess.run(["git", "rev-parse", "HEAD"], cwd=REPO,
                            capture_output=True, text=True, check=True).stdout.strip()
except Exception:
    commit = None

pin_path = ROOT / "base-model-pin.json"
pin = json.loads(pin_path.read_text(encoding="utf-8")) if pin_path.exists() else {}

manifest = {
    "runId": RUN,
    "status": "PROVISIONAL — awaiting dataset approval" if provisional else "FROZEN",
    "baseModel": pin.get("repo", "Qwen/Qwen2.5-3B-Instruct"),
    "baseModelRevision": pin.get("revision", "PIN AT DOWNLOAD"),
    "repoCommit": commit,
    "artifacts": entries,
    "rule": ("No example may change after freeze. If a checkpoint produces an "
             "embarrassing failure, the failure is the result."),
}
out = ROOT / ("freeze-provisional.json" if provisional
              else f"freeze-{RUN.replace('run-', 'run')}.json")
out.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
print(json.dumps(manifest, indent=2))
print(f"\nwritten: {out}")
