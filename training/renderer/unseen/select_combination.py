"""Gate 7: pick the unseen family's primitive combination mechanically.

The design requires the post-training family to be authored without reference to the
model's failures. The curator has seen the failures (they ran the evaluation), so
discretion is removed instead: enumerate every pair of cognitive primitives that the
training corpus contains individually but NEVER in combination, sort them, and pick
by the sha256 of the freeze manifest — a value committed before any evaluation
existed. The choice was therefore fixed, if unknown, before the first result.
"""
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).parents[1]
rows = [json.loads(l) for l in (ROOT / "dataset" / "train-200.jsonl").read_text(encoding="utf-8").splitlines() if l.strip()]

def primitives(r):
    p2 = r["plan2"]
    have = set()
    if "Ava made an error; Scott corrected her" in p2: have.add("correction-companion")
    if "Scott corrected his own earlier words" in p2: have.add("correction-user")
    if "emphatically agreeing" in p2: have.add("agreement")
    if "has NOT learned" in p2: have.add("epistemic-unknown")
    if "superseded, never assert" in p2: have.add("superseded")
    if "PALETTE" in p2: have.add("palette")
    if ":mandatory" in p2: have.add("mandatory-question")
    if ":optional" in p2: have.add("optional-question")
    return have

seen_single, seen_pairs = set(), set()
for r in rows:
    ps = primitives(r)
    seen_single |= ps
    for a in ps:
        for b in ps:
            if a < b:
                seen_pairs.add((a, b))

candidates = sorted(
    (a, b) for i, a in enumerate(sorted(seen_single)) for b in sorted(seen_single)[i+1:]
    if (a, b) not in seen_pairs)
print(f"primitives individually present: {sorted(seen_single)}")
print(f"pairs never co-occurring in training: {len(candidates)}")
for c in candidates:
    print(f"  {c}")

seed = int(hashlib.sha256((ROOT / "dataset" / "freeze-run1a.json").read_bytes()).hexdigest(), 16)
pick = candidates[seed % len(candidates)]
print(f"\nfreeze-hash selection: {pick}")
