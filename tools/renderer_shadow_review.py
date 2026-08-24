"""Builds the paired blind review from collected renderer shadow rows.

Reads the companion database directly (everything stays local, inside the same
privacy boundary as the DB itself) and writes two files beside it:

  renderer-shadow-review.md   anonymized pairs, A/B randomized per item
  renderer-shadow-key.json    SEALED mapping — do not open before judging

Sampling rule (docs/RENDERER_SHADOW.md §5): every row where EITHER reply carries a
deterministic violation is included unconditionally; clean rows are added by seeded
random sample up to --size. Failures cannot be hidden by selection.

  python tools/renderer_shadow_review.py --db path/to/companion.db [--size 30] [--seed 20260824]
"""
import argparse
import json
import random
import sqlite3
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument("--db", required=True)
parser.add_argument("--size", type=int, default=30)
parser.add_argument("--seed", type=int, default=20260824)
args = parser.parse_args()

db = sqlite3.connect(f"file:{args.db}?mode=ro", uri=True)
rows = db.execute(
    "SELECT Id, Timestamp, Legacy, Model, Agreed, Input FROM ShadowComparisons "
    "WHERE Subject = 'renderer.plan2' AND Model IS NOT NULL ORDER BY Timestamp").fetchall()
if not rows:
    raise SystemExit("no renderer shadow rows collected yet")

parsed = []
for rid, ts, legacy, model, agreed, envelope in rows:
    env = json.loads(envelope) if envelope else {}
    parsed.append({
        "id": rid, "ts": ts, "production": legacy or "", "shadow": model or "",
        "user": env.get("UserMessage", ""),
        "violations": {
            "shadow": env.get("ShadowViolations", []),
            "production": env.get("ProductionViolations", []),
        },
        "paletteBearing": env.get("PaletteBearing", False),
        "questionMode": env.get("QuestionMode", "?"),
    })

flagged = [p for p in parsed if p["violations"]["shadow"] or p["violations"]["production"]]
clean = [p for p in parsed if p not in flagged]
rng = random.Random(args.seed)
extra = rng.sample(clean, min(max(0, args.size - len(flagged)), len(clean)))
sample = flagged + extra
rng.shuffle(sample)

out_dir = Path(args.db).parent
key = {}
lines = ["# Renderer shadow — paired blind review",
         "",
         f"{len(sample)} pairs ({len(flagged)} carry a deterministic flag on either side — all of",
         "them are here; the rest are a seeded random sample of clean pairs). For each pair,",
         "judge both replies: would-use / fine / off / bad, and circle a preference or 'tie'.",
         "Which side is production and which is the shadow is in the sealed key only.", ""]
for i, p in enumerate(sample, 1):
    first_is_shadow = rng.random() < 0.5
    a = p["shadow"] if first_is_shadow else p["production"]
    b = p["production"] if first_is_shadow else p["shadow"]
    key[str(i)] = {"A": "shadow" if first_is_shadow else "production",
                   "B": "production" if first_is_shadow else "shadow",
                   "rowId": p["id"], "flagged": bool(p["violations"]["shadow"] or p["violations"]["production"])}
    lines += [f"## {i}",
              f"> Scott: {p['user']}", "",
              f"**A.** {a.strip()}", "",
              f"**B.** {b.strip()}", "",
              "Judgment A: ___  B: ___  Preferred: ___", ""]

(out_dir / "renderer-shadow-review.md").write_text("\n".join(lines), encoding="utf-8")
(out_dir / "renderer-shadow-key.json").write_text(json.dumps(
    {"sealed": "DO NOT OPEN BEFORE JUDGING", "seed": args.seed, "map": key}, indent=1),
    encoding="utf-8")
print(f"{len(sample)} pairs -> {out_dir / 'renderer-shadow-review.md'} (key sealed; "
      f"{len(flagged)} flagged rows included unconditionally)")
