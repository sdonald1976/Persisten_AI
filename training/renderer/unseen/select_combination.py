"""SUPERSEDED for Run-2 by select_combination_v4.py.

This selector detects primitives by substring-matching plan/2 PROSE. CompactV4 has no
such prose — its primitives are typed — so against plan/4 rows this finds nothing and
would select a combination that means something else. Kept because it is the mechanism
run-1a/1b/1c's held-out families were actually chosen by, and that record has to stay
readable.
"""

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

# Pairs already consumed by earlier cycles are permanent regression sets and are
# excluded so new picks never collide with them:
#   run-1a: epistemic-unknown x superseded (uns-*)
#   run-1b: epistemic-unknown x mandatory-question (u1b-epimq-*),
#           correction-user x optional-question (u1b-cuoq-*)
# (The run-1b pairs would mostly self-exclude anyway — run-1c trains on both
# compositions by design — but the exclusion is stated, not assumed.)
EXCLUDED = {("epistemic-unknown", "superseded"),
            ("epistemic-unknown", "mandatory-question"),
            ("correction-user", "optional-question")}
candidates = sorted(
    (a, b) for i, a in enumerate(sorted(seen_single)) for b in sorted(seen_single)[i+1:]
    if (a, b) not in seen_pairs and (a, b) not in EXCLUDED)
print(f"primitives individually present: {sorted(seen_single)}")
print(f"pairs never co-occurring in training: {len(candidates)}")
for c in candidates:
    print(f"  {c}")

# Each cycle seeds from the PREVIOUS cycle's freeze manifest — a value committed
# before any of that cycle's evaluation results existed. Run-1b seeded from
# freeze-run1a.json (picks recorded in RUN1B_RESULTS.md); run-1c seeds from
# freeze-run1b.json.
seed = int(hashlib.sha256((ROOT / "dataset" / "freeze-run1b.json").read_bytes()).hexdigest(), 16)
first = candidates[seed % len(candidates)]
rest = [c for c in candidates if c != first]
second = rest[(seed // len(candidates)) % len(rest)]
print(f"\nfreeze-hash selections: {first} and {second}")
