"""Collect the scenarios that curation rejected into a rescue set for re-sampling.

Rejection is not a verdict on the scenario, only on the four candidates drawn so far.
Temperature 0.6 means a fresh draw is a genuinely different sample, so the hard strata
get more attempts before anyone concludes the teachers cannot render them. Nothing is
rewritten here: the same plan, the same prompt, more draws.
"""
import json
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).parent
SCENARIOS = ROOT / "scenarios"
RESCUE = ROOT / "rescue"

accepted = {json.loads(l)["id"]
            for l in (ROOT / "train-200.jsonl").read_text(encoding="utf-8").splitlines() if l.strip()}
all_scenarios = {}
for f in sorted(SCENARIOS.glob("*.jsonl")):
    for line in f.read_text(encoding="utf-8").splitlines():
        if line.strip():
            r = json.loads(line)
            all_scenarios[r["id"]] = line

missing = [sid for sid in all_scenarios if sid not in accepted]
RESCUE.mkdir(exist_ok=True)
(RESCUE / "rejected.jsonl").write_text(
    "\n".join(all_scenarios[sid] for sid in missing) + "\n", encoding="utf-8")
print(f"{len(missing)} rejected scenarios -> {RESCUE / 'rejected.jsonl'}")

if "--run" in sys.argv:
    attempts = "4"
    cmd = ["dotnet", "run", "--project", "tools/Companion.DatasetGen/Companion.DatasetGen.csproj",
           "--no-build", "--", "--scenarios", str(RESCUE),
           "--out", str(ROOT / "candidates-rescue.jsonl"),
           "--teachers", "qwen3:8b,llama3.2:3b", "--attempts", attempts]
    print(" ".join(cmd))
    subprocess.run(cmd, cwd=ROOT.parents[2], check=True)
