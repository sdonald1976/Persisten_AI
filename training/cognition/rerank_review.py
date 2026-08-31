"""Local review of reranker shadow comparisons — the human-label pass over provenance.

Reads the shadow jsonl the running companion appends (ids and orderings only), joins memory
text from the LOCAL companion.db at review time (never duplicated into the shadow file), and lets
Scott judge relevance. Nothing here presents any model as ground truth: it shows the rule, the
3B, and the cross-encoder orderings side by side and asks the human which candidates are actually
relevant.

    python training/cognition/rerank_review.py            # review, newest-informative first
    python training/cognition/rerank_review.py --export   # write a versioned, grouped dataset

Actions per item: [c]onfirm (rule/3B/CE agree with your read), [f]lip (mark the correct top),
[a]mbiguous, [s]kip, [g]raded (score each candidate 0-3), [q]uit. Resumable: completed turn ids
are recorded in reviewed.jsonl and skipped on the next run.

Private turns never reach here: the companion excludes them at the source (no shadow record), and
the query join is null for them regardless.
"""
import argparse
import datetime
import hashlib
import io
import json
import os
import sqlite3
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent
SHADOW = ROOT / "src" / "Companion.Api" / "models" / "rerank-shadow" / "shadow.jsonl"
DB = ROOT / "src" / "Companion.Api" / "companion.db"
REVIEWED = ROOT / "training" / "corpus" / "rerank.reviewed.jsonl"
EXPORT_DIR = ROOT / "training" / "corpus"
SCHEMA_VERSION = 1


def load_shadow(path):
    if not path.exists():
        return []
    return [json.loads(l) for l in io.open(path, encoding="utf-8") if l.strip()]


def reviewed_ids(path):
    if not path.exists():
        return set()
    return {json.loads(l)["turnId"] for l in io.open(path, encoding="utf-8") if l.strip()}


def memory_text(con, mem_id):
    """Best-effort local join: a memory id -> its text, from either memory table."""
    hexid = mem_id.replace("-", "").upper()
    for sql, cols in (
        ("SELECT Subject, Predicate, Value FROM SemanticMemories WHERE replace(upper(Id),'-','')=?", 3),
        ("SELECT Description FROM EpisodicMemories WHERE replace(upper(Id),'-','')=?", 1),
    ):
        row = con.execute(sql, (hexid,)).fetchone()
        if row:
            return " ".join(str(x) for x in row if x)
    return None


def query_text(con, turn_id):
    hexid = turn_id.replace("-", "").upper()
    row = con.execute(
        "SELECT RetrievalQuery FROM TurnRecords WHERE replace(upper(Id),'-','')=?", (hexid,)
    ).fetchone()
    return row[0] if row else None


def priority(rec):
    """Disagreement, then low margin, then ambiguity — the items worth a human's time first."""
    auth = rec["authoritative"]["ranking"]
    ce = (rec.get("crossEncoder") or {}).get("ranking") or []
    rule = (rec.get("rule") or {}).get("ranking") or []
    disagree = 0
    if ce and auth and ce[0] != auth[0]:
        disagree += 2
    if rule and auth and rule[0] != auth[0]:
        disagree += 1
    if ce and rule and ce[0] != rule[0]:
        disagree += 1
    return -disagree  # most disagreement first


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--shadow", default=str(SHADOW))
    ap.add_argument("--export", action="store_true", help="write a versioned grouped dataset and exit")
    ap.add_argument("--reviewer", default=os.environ.get("USERNAME") or "scott")
    args = ap.parse_args()

    if args.export:
        return export()

    shadow = load_shadow(Path(args.shadow))
    done = reviewed_ids(REVIEWED)
    todo = [r for r in shadow if r["turnId"] not in done]
    todo.sort(key=priority)

    if not shadow:
        print(f"No shadow records at {args.shadow}.")
        print("Start the companion with CognitiveModels:RerankShadow=true and have a few turns, then re-run.")
        return 0
    print(f"{len(shadow)} shadow records, {len(done)} already reviewed, {len(todo)} to go.\n")
    if not todo:
        print("Nothing left to review. Run with --export to build the dataset.")
        return 0

    if not DB.exists():
        print(f"companion.db not found at {DB}; cannot show memory text. Review needs the local DB.")
        return 1
    con = sqlite3.connect(f"file:{DB}?mode=ro", uri=True)

    REVIEWED.parent.mkdir(parents=True, exist_ok=True)
    out = io.open(REVIEWED, "a", encoding="utf-8", newline="\n")
    reviewed = 0
    for rec in todo:
        q = query_text(con, rec["turnId"])
        cand_ids = rec["candidateIds"]
        texts = {mid: (memory_text(con, mid) or "(text unavailable / forgotten)") for mid in cand_ids}
        auth = rec["authoritative"]["ranking"]
        ce = (rec.get("crossEncoder") or {}).get("ranking") or []
        rule = (rec.get("rule") or {}).get("ranking") or []

        print("=" * 90)
        print(f"turn {rec['turnId'][:8]}  query: {q or '(private / unavailable)'}")
        print("candidates (retrieval order):")
        for i, mid in enumerate(cand_ids):
            tags = []
            if auth and auth[0] == mid: tags.append("3B#1")
            if ce and ce[0] == mid: tags.append("CE#1")
            if rule and rule[0] == mid: tags.append("RULE#1")
            print(f"  [{i}] {texts[mid][:80]:<80} {' '.join(tags)}")
        print(f"rankings  3B:{_short(auth,cand_ids)}  CE:{_short(ce,cand_ids)}  RULE:{_short(rule,cand_ids)}")
        if (rec.get('crossEncoder') or {}).get('failed'): print("  (cross-encoder FAILED this turn)")

        action = input("[c]onfirm3B [f]lip [a]mbiguous [g]raded [s]kip [q]uit > ").strip().lower()
        if action == "q":
            break
        label = build_label(action, rec, cand_ids, texts)
        if label is None:
            continue  # skip
        label.update({
            "turnId": rec["turnId"], "reviewer": args.reviewer,
            "reviewedAt": _now(), "schemaVersion": SCHEMA_VERSION,
            "candidateSetHash": rec["candidateSetHash"],
        })
        out.write(json.dumps(label) + "\n")
        out.flush()
        reviewed += 1

    out.close()
    print(f"\nRecorded {reviewed} reviews to {REVIEWED}.")
    return 0


def _short(ranking, cand_ids):
    idx = {mid: i for i, mid in enumerate(cand_ids)}
    return "[" + ",".join(str(idx.get(m, "?")) for m in ranking) + "]"


def build_label(action, rec, cand_ids, texts):
    if action == "s" or action == "":
        return None
    if action == "a":
        return {"kind": "ambiguous", "relevant": None, "reason": input("  reason (optional): ").strip() or None}
    if action == "c":
        top = rec["authoritative"]["ranking"][:1]
        return {"kind": "confirm", "relevantTop": top, "basis": "reviewer-confirmed-3b"}
    if action == "f":
        raw = input("  correct candidate index(es), comma-separated: ").strip()
        try:
            idxs = [int(x) for x in raw.split(",") if x.strip() != ""]
            return {"kind": "flip", "relevantTop": [cand_ids[i] for i in idxs if 0 <= i < len(cand_ids)],
                    "reason": input("  reason (optional): ").strip() or None}
        except ValueError:
            print("  (unparseable — skipped)")
            return None
    if action == "g":
        graded = {}
        for i, mid in enumerate(cand_ids):
            raw = input(f"  [{i}] {texts[mid][:50]} score 0-3 (blank=0): ").strip()
            graded[mid] = int(raw) if raw.isdigit() else 0
        return {"kind": "graded", "graded": graded}
    return None


def export():
    """Versioned, conversation/user-grouped dataset from reviewed labels. No train/test leakage:
    grouping is by user, so a user's turns never straddle splits."""
    if not REVIEWED.exists():
        print("No reviewed labels yet; nothing to export.")
        return 0
    rows = [json.loads(l) for l in io.open(REVIEWED, encoding="utf-8") if l.strip()]
    labelled = [r for r in rows if r.get("kind") in ("confirm", "flip", "graded")]
    con = sqlite3.connect(f"file:{DB}?mode=ro", uri=True) if DB.exists() else None

    def user_of(turn_id):
        if not con:
            return "unknown"
        hexid = turn_id.replace("-", "").upper()
        row = con.execute("SELECT UserId FROM TurnRecords WHERE replace(upper(Id),'-','')=?", (hexid,)).fetchone()
        return row[0] if row else "unknown"

    for r in rows:
        r["_user"] = user_of(r["turnId"])
    users = sorted({r["_user"] for r in rows})
    def bucket(u):
        h = int(hashlib.sha256(("rerank:" + u).encode()).hexdigest(), 16) % 100
        return "test" if h < 20 else "validation" if h < 35 else "train"
    split = {u: bucket(u) for u in users}

    manifest = {
        "schemaVersion": SCHEMA_VERSION, "exportedAt": _now(),
        "totalReviewed": len(rows), "usableLabels": len(labelled),
        "ambiguous": sum(1 for r in rows if r.get("kind") == "ambiguous"),
        "grouping": "by user (hash split, leakage-controlled)",
        "splitByUser": split,
        "strata": "reviewed-real only; synthetic/borrowed live in their own files and are never mixed here",
    }
    out = EXPORT_DIR / "rerank.dataset.jsonl"
    with io.open(out, "w", encoding="utf-8", newline="\n") as f:
        for r in labelled:
            r["split"] = split.get(r["_user"], "train")
            r.pop("_user", None)
            f.write(json.dumps(r) + "\n")
    io.open(EXPORT_DIR / "rerank.dataset.manifest.json", "w", encoding="utf-8", newline="\n").write(
        json.dumps(manifest, indent=2))
    print(f"Exported {len(labelled)} labels -> {out}")
    print(f"Splits by user: {split}")
    return 0


def _now():
    # Wall clock is fine here — this is an offline human-review tool, not the turn path.
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


if __name__ == "__main__":
    sys.exit(main())
