"""Build a small sealed blind-review pack.

Blind means the reader cannot tell which arm wrote which reply. The arm labels live in a key file
that is written separately and hashed, so a review can be shown to have happened before the key
was opened rather than asserted to have.

Small on purpose. A pack nobody finishes is worth less than a pack of thirty rows somebody
actually reads, and the sampling is stratified so the thirty cover what the run needs judged:
ordinary test rows, the hard cases where both adapters collapse into stubs, and the two families
that regressed - b4's unsupported detail and b6's forbidden background.

Selection is deterministic (sorted ids, fixed stride), so the pack can be rebuilt and shown to be
the same pack.

    python review_pack.py
"""
import hashlib
import io
import json
import random
from pathlib import Path

ROOT = Path(__file__).parent
DATASET = ROOT / "dataset"
EV = ROOT / "evaluation"
OUT = EV / "review-pack"

ARMS = ("base", "run-1c", "run-2")
SEED = 20260830


def rd(path, enc="utf-8"):
    return [json.loads(l) for l in io.open(path, encoding=enc) if l.strip()]


def sha256_file(path):
    return hashlib.sha256(io.open(path, "rb").read()).hexdigest()


def main():
    meta = {m["id"]: m for m in rd(DATASET / "accepted.metadata.jsonl")}
    scen = {s["id"]: s for s in rd(DATASET / "scenarios.jsonl")}

    rows, gens = {}, {}
    for split in ("test", "hard-eval"):
        for r in rd(DATASET / f"mouth-v2-{split}.jsonl", "utf-8-sig"):
            rows[r["id"]] = (split, r)
        for arm in ARMS:
            p = EV / f"gen-{arm}-{split}.jsonl"
            if p.exists():
                for g in rd(p):
                    gens.setdefault(g["id"], {})[arm] = g["target"]

    def pick(predicate, want):
        """Deterministic stratified draw: sorted ids, even stride across the matches."""
        matches = sorted(rid for rid in rows if predicate(rid))
        if not matches:
            return []
        if len(matches) <= want:
            return matches
        stride = len(matches) / want
        return [matches[int(i * stride)] for i in range(want)]

    def family(rid):
        return meta[rid]["familyId"] if rid in meta else "?"

    strata = {
        "test-general": pick(
            lambda r: rows[r][0] == "test" and family(r) not in ("b4", "b6"), 14),
        "hard-case": pick(lambda r: rows[r][0] == "hard-eval", 8),
        "b4-unsupported-detail": pick(lambda r: family(r) == "b4", 5),
        "b6-forbidden-background": pick(lambda r: family(r) == "b6", 3),
    }

    rng = random.Random(SEED)
    pack, key = [], []
    item = 0
    for stratum, ids in strata.items():
        for rid in ids:
            available = [a for a in ARMS if a in gens.get(rid, {})]
            if len(available) < 2:
                continue
            item += 1
            # Arm order is shuffled per item, so position carries no information.
            order = available[:]
            rng.shuffle(order)
            sc = scen.get(meta[rid]["scenarioId"], {}) if rid in meta else {}
            pack.append({
                "item": item,
                "stratum": stratum,
                "userMessage": sc.get("userMessage"),
                "plan": {
                    "mustExpress": [f["text"] for f in sc.get("approvedFacts", [])
                                    if f["policy"] == 0],
                    "mayExpress": [f["text"] for f in sc.get("approvedFacts", [])
                                   if f["policy"] == 1],
                    "background": [f["text"] for f in sc.get("approvedFacts", [])
                                   if f["policy"] == 2],
                    "questionPolicy": (sc.get("question") or {}).get("policy"),
                },
                "replies": {chr(ord("A") + i): gens[rid][a] for i, a in enumerate(order)},
            })
            key.append({
                "item": item, "rowId": rid, "stratum": stratum, "family": family(rid),
                "arms": {chr(ord("A") + i): a for i, a in enumerate(order)},
            })

    OUT.mkdir(parents=True, exist_ok=True)
    pack_path = OUT / "pack.json"
    key_path = OUT / "KEY.json"
    pack_path.write_text(json.dumps(pack, indent=2) + "\n", encoding="utf-8")
    key_path.write_text(json.dumps(key, indent=2) + "\n", encoding="utf-8")

    sheet = OUT / "REVIEW.md"
    lines = [
        "# Blind review pack - Run-2",
        "",
        f"{len(pack)} items. Each shows the turn, the plan it was rendered from, and two or three",
        "replies labelled A/B/C. The labels are shuffled per item; which arm wrote which is in",
        "KEY.json, whose hash is recorded below so a review can be shown to predate opening it.",
        "",
        "For each reply, two questions:",
        "",
        "1. **Faithful** - does it obey the plan? Required points said, forbidden points absent,",
        "   background not surfaced, question policy respected.",
        "2. **Natural** - would you accept it as something a person said?",
        "",
        "Answer per item, then open the key.",
        "",
        f"- pack.json sha256: `{sha256_file(pack_path)}`",
        f"- KEY.json sha256: `{sha256_file(key_path)}`",
        "",
        "## Items",
        "",
    ]
    for p in pack:
        lines.append(f"### {p['item']} ({p['stratum']})")
        lines.append("")
        lines.append(f"**Them:** {p['userMessage']}")
        lines.append("")
        plan = p["plan"]
        if plan["mustExpress"]:
            lines.append(f"- must say: {'; '.join(plan['mustExpress'])}")
        if plan["mayExpress"]:
            lines.append(f"- may say: {'; '.join(plan['mayExpress'])}")
        if plan["background"]:
            lines.append(f"- background, must NOT surface: {'; '.join(plan['background'])}")
        lines.append(f"- question policy: {plan['questionPolicy']}")
        lines.append("")
        for label, text in p["replies"].items():
            lines.append(f"**{label}.** {text}")
            lines.append("")
    sheet.write_text("\n".join(lines) + "\n", encoding="utf-8")

    manifest = {
        "items": len(pack),
        "strata": {k: len(v) for k, v in strata.items()},
        "seed": SEED,
        "packSha256": sha256_file(pack_path),
        "keySha256": sha256_file(key_path),
        "reviewSha256": sha256_file(sheet),
    }
    (OUT / "MANIFEST.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(manifest, indent=2))


if __name__ == "__main__":
    main()
