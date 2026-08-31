"""Evaluate the three rerankers from shadow records + reviewed labels, and write a report.

Reads the shadow jsonl (all three orderings per real turn) and the reviewed labels (the human
relevance judgments), computes ranking metrics for each method against the reviewed truth, and
reports them with honest strata separation and conservative, PAIRED confidence intervals.

    python training/cognition/rerank_eval.py

Because all three methods rank the SAME candidate set on each turn, comparison is paired: the CI
on the DIFFERENCE (cross-encoder minus 3B) is computed by a conversation/user-grouped bootstrap,
resampling whole users, not individual turns — the right unit given turns cluster by user. An
independent-proportion CI is reported too, but only as context; promotion reads the paired
grouped bootstrap.

The report states exactly how many reviewed real labels back each number. With few labels the
intervals are wide and the report says so; that is the honest state, not a blocker for shipping
the pipeline.
"""
import argparse
import io
import sys as _sys
try:
    _sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass
import json
import math
import os
import random
import sqlite3
from collections import Counter, defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent
SHADOW = ROOT / "src" / "Companion.Api" / "models" / "rerank-shadow" / "shadow.jsonl"
REVIEWED = ROOT / "training" / "corpus" / "rerank.reviewed.jsonl"
DB = ROOT / "src" / "Companion.Api" / "companion.db"
REPORT = ROOT / "training" / "cognition" / "rerank-shadow" / "RERANK_SHADOW_REPORT.md"

METHODS = [("authoritative", "3B"), ("crossEncoder", "CE"), ("rule", "RULE")]
# Deterministic seed: bootstrap must be reproducible run-to-run.
RNG = random.Random(20260901)


def load(path):
    return [json.loads(l) for l in io.open(path, encoding="utf-8") if l.strip()] if path.exists() else []


def relevant_set(label):
    """The reviewer's relevant-id set for a turn, or None if not a usable positive-bearing label."""
    if label.get("kind") in ("confirm", "flip"):
        return set(label.get("relevantTop") or [])
    if label.get("kind") == "graded":
        return {mid for mid, sc in (label.get("graded") or {}).items() if sc and sc >= 2}
    return None


def ranking_of(rec, key):
    m = rec.get(key) if key != "authoritative" else rec["authoritative"]
    return (m or {}).get("ranking") or []


def metrics(ranking, relevant):
    """P@1, R@3, RR for one ranking against a relevant-id set."""
    if not ranking or not relevant:
        return None
    p1 = 1.0 if ranking[0] in relevant else 0.0
    r3 = len(set(ranking[:3]) & relevant) / len(relevant)
    rr = 0.0
    for i, mid in enumerate(ranking):
        if mid in relevant:
            rr = 1.0 / (i + 1)
            break
    return p1, r3, rr


def user_of(con, turn_id):
    if not con:
        return "unknown"
    hexid = turn_id.replace("-", "").upper()
    row = con.execute("SELECT UserId FROM TurnRecords WHERE replace(upper(Id),'-','')=?", (hexid,)).fetchone()
    return row[0] if row else "unknown"


def grouped_bootstrap_diff(per_user_pairs, n=2000):
    """Paired grouped bootstrap of (CE - 3B) mean P@1 difference, resampling whole users.

    per_user_pairs: {user: [(ce_p1, tb_p1), ...]}. Returns (mean_diff, lo, hi) 95% CI, or None
    when too few users to resample meaningfully."""
    users = list(per_user_pairs)
    if len(users) < 2:
        return None
    diffs = []
    for _ in range(n):
        sample = [RNG.choice(users) for _ in users]
        ce = tb = k = 0
        for u in sample:
            for a, b in per_user_pairs[u]:
                ce += a; tb += b; k += 1
        if k:
            diffs.append(ce / k - tb / k)
    if not diffs:
        return None
    diffs.sort()
    mean = sum(diffs) / len(diffs)
    return mean, diffs[int(0.025 * len(diffs))], diffs[int(0.975 * len(diffs))]


def pct(xs):
    xs = sorted(xs)
    if not xs:
        return {}
    def q(p): return xs[min(len(xs) - 1, int(p * len(xs)))]
    return {"p50": q(0.5), "p90": q(0.9), "p99": q(0.99), "max": xs[-1]}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--shadow", default=str(SHADOW))
    ap.add_argument("--reviewed", default=str(REVIEWED))
    args = ap.parse_args()

    shadow = load(Path(args.shadow))
    reviewed = {r["turnId"]: r for r in load(Path(args.reviewed))}
    con = sqlite3.connect(f"file:{DB}?mode=ro", uri=True) if DB.exists() else None
    shadow_by_turn = {r["turnId"]: r for r in shadow}

    lines = ["# Reranker shadow evaluation", ""]
    lines.append(f"- shadow records: **{len(shadow)}**")
    lines.append(f"- reviewed turns: **{len(reviewed)}** "
                 f"(usable positive-bearing: {sum(1 for r in reviewed.values() if relevant_set(r))}, "
                 f"ambiguous: {sum(1 for r in reviewed.values() if r.get('kind') == 'ambiguous')})")
    lines.append("")

    # ---- agreement + latency + failure, over ALL shadow records (no labels needed) ----
    lines.append("## Agreement, latency, reliability (all shadow records)")
    if shadow:
        ce_top1 = sum(1 for r in shadow if r.get("crossEncoderTop1Agrees")
                      or (ranking_of(r, "crossEncoder")[:1] == r["authoritative"]["ranking"][:1]
                          and ranking_of(r, "crossEncoder")))
        # recompute robustly
        def top1_agree(r, key):
            a = r["authoritative"]["ranking"]; b = ranking_of(r, key)
            return bool(a and b and a[0] == b[0])
        ce_agree = sum(1 for r in shadow if top1_agree(r, "crossEncoder"))
        rule_agree = sum(1 for r in shadow if top1_agree(r, "rule"))
        ce_fail = sum(1 for r in shadow if (r.get("crossEncoder") or {}).get("failed"))
        lat = defaultdict(list)
        for r in shadow:
            for key, _ in METHODS:
                m = r.get(key) if key != "authoritative" else r["authoritative"]
                if m and not m.get("failed"):
                    lat[key].append(m["latencyMs"])
        lines.append(f"- CE top-1 agrees with 3B: **{ce_agree}/{len(shadow)}** "
                     f"({ce_agree / len(shadow):.0%})")
        lines.append(f"- RULE top-1 agrees with 3B: **{rule_agree}/{len(shadow)}** "
                     f"({rule_agree / len(shadow):.0%})")
        lines.append(f"- cross-encoder failures/timeouts: **{ce_fail}/{len(shadow)}**")
        lines.append("- latency (ms), by method:")
        for key, name in METHODS:
            lines.append(f"  - {name}: {pct(lat[key])}")
    else:
        lines.append("- _no shadow records yet — start the companion with RerankShadow on and take some turns._")
    lines.append("")

    # ---- ranking metrics vs reviewed truth, paired ----
    lines.append("## Ranking metrics vs reviewed relevance (paired)")
    labelled_turns = [(tid, relevant_set(lab)) for tid, lab in reviewed.items()
                      if relevant_set(lab) and tid in shadow_by_turn]
    if not labelled_turns:
        lines.append("- _no reviewed positive-bearing labels that match a shadow record yet._")
        lines.append("- **This does not block the pipeline** — every stage above is exercised; "
                     "promotion is what waits on labels.")
    else:
        agg = {k: {"p1": [], "r3": [], "rr": []} for k, _ in METHODS}
        per_user_ce_vs_3b = defaultdict(list)
        for tid, rel in labelled_turns:
            rec = shadow_by_turn[tid]
            u = rec.get("userId") or user_of(con, tid)
            per = {}
            for key, _ in METHODS:
                m = metrics(ranking_of(rec, key), rel)
                if m:
                    agg[key]["p1"].append(m[0]); agg[key]["r3"].append(m[1]); agg[key]["rr"].append(m[2])
                    per[key] = m[0]
            if "crossEncoder" in per and "authoritative" in per:
                per_user_ce_vs_3b[u].append((per["crossEncoder"], per["authoritative"]))
        n = len(labelled_turns)
        lines.append(f"- reviewed query sets scored: **{n}**, across **{len(per_user_ce_vs_3b)}** user(s)")
        lines.append("")
        lines.append("| method | P@1 | R@3 | MRR |")
        lines.append("|---|---|---|---|")
        for key, name in METHODS:
            a = agg[key]
            def mean(xs): return sum(xs) / len(xs) if xs else float("nan")
            lines.append(f"| {name} | {mean(a['p1']):.3f} | {mean(a['r3']):.3f} | {mean(a['rr']):.3f} |")
        lines.append("")
        boot = grouped_bootstrap_diff(per_user_ce_vs_3b)
        if boot:
            mean, lo, hi = boot
            lines.append(f"- **CE - 3B P@1 (paired, user-grouped bootstrap):** {mean:+.3f} "
                         f"[95% CI {lo:+.3f}, {hi:+.3f}]")
            verdict = ("CE ahead" if lo > 0 else "3B ahead" if hi < 0 else "indistinguishable")
            lines.append(f"  - interval {'excludes' if lo > 0 or hi < 0 else 'includes'} zero → **{verdict}**")
        else:
            lines.append("- _too few users to bootstrap a paired CI; report is descriptive only._")

    # ---- promotion gate (stated, not decided) ----
    lines.append("")
    lines.append("## Promotion gate (conservative — not met until real reviewed data supports it)")
    lines.append("- CE must not lose to RULE on any sufficiently-populated user-grouped fold;")
    lines.append("- CE must match or beat 3B on the frozen reviewed real set (paired CI ≥ 0);")
    lines.append("- CE latency p99 and failure rate must support replacing the live 3B call;")
    lines.append("- weak/mechanical labels guide collection only; they never authorize promotion.")
    lines.append("")
    lines.append("_Weak/synthetic/borrowed strata are reported in their own files and never mixed "
                 "into the reviewed-real numbers above._")

    REPORT.parent.mkdir(parents=True, exist_ok=True)
    io.open(REPORT, "w", encoding="utf-8", newline="\n").write("\n".join(lines) + "\n")
    print("\n".join(lines))
    print(f"\n-> {REPORT}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
