"""A bounded decoding comparison on a representative validation subset.

The collapse this exists to test is not a fidelity failure. Run-2 passes 93% of the deterministic
gates and still answers hard cases with a handful of near-identical stubs, and answered a plumber
question with an offer of tea. No gate catches either, because terseness violates nothing and
topical drift was only ever checked by a critic that is not in the turn path.

So the measurements here are the ones the gates do not make: does the reply engage with what was
actually asked, and does the model say different things to different turns.

SELECTION USES VALIDATION ROWS ONLY. Test and hard-eval stay closed - the failure shapes are
recreated by matching their STRUCTURE inside validation, never by reaching into the splits the
final numbers come from.

    python decoding_grid.py --stage subset     # build and show the subset
    python decoding_grid.py --stage grid       # run the grid
    python decoding_grid.py --stage full --config <name>
"""
import argparse
import io
import json
import re
import statistics
import time
import urllib.request
from pathlib import Path

ROOT = Path(__file__).parent
DATASET = ROOT / "dataset"
EV = ROOT / "evaluation"
ENDPOINT = "http://127.0.0.1:11436"

MUST, MAY, BACKGROUND = 0, 1, 2

# The grid. Small on purpose: every cell is a full pass over the subset, and a grid nobody can
# finish is a grid whose winner nobody trusts.
GRID = [
    # name,               temperature, top_p, repetition_penalty, no_repeat_ngram
    ("current-greedy",    0.0, 1.00, 1.00, 0),
    ("greedy-rep1.10",    0.0, 1.00, 1.10, 0),
    ("greedy-rep1.15-ng4",0.0, 1.00, 1.15, 4),
    ("t0.3-p0.9",         0.3, 0.90, 1.00, 0),
    ("t0.5-p0.9",         0.5, 0.90, 1.00, 0),
    ("t0.7-p0.9",         0.7, 0.90, 1.00, 0),
    ("t0.5-p0.9-rep1.10", 0.5, 0.90, 1.10, 0),
    ("t0.7-p0.92-rep1.15-ng4", 0.7, 0.92, 1.15, 4),
]

STOP = set(
    "the a an is was were are be been of to in on at for and or but it that this with as by from "
    "has have had not no you your i my we they there here just so then now if when what how why "
    "all any some one two more most much very really quite still yet also too about into over "
    "under after before while since until can could would should will shall may might must do "
    "does did done get got go went come came make made take took give gave say said know knew "
    "think thought want wanted need needed like liked yeah yep okay sure right fine good great "
    "well hey look listen honestly actually basically anyway though mean sort kind bit thing "
    "things stuff going gonna wanna lot sorry thanks please maybe perhaps guess suppose".split())


def content_words(text):
    return {w for w in re.split(r"[^A-Za-z0-9']+", (text or "").lower())
            if len(w) > 3 and w not in STOP}


def post(payload, timeout=180):
    body = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(
        ENDPOINT + "/api/chat", data=body,
        headers={"Content-Type": "application/json"}, method="POST")
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.loads(r.read())


def load():
    rows = [json.loads(l) for l
            in io.open(DATASET / "mouth-v2-validation.jsonl", encoding="utf-8-sig") if l.strip()]
    meta = {m["id"]: m for m in
            (json.loads(l) for l in io.open(DATASET / "accepted.metadata.jsonl", encoding="utf-8")
             if l.strip())}
    scen = {s["id"]: s for s in
            (json.loads(l) for l in io.open(DATASET / "scenarios.jsonl", encoding="utf-8")
             if l.strip())}
    out = []
    for r in rows:
        m = meta.get(r["id"])
        if not m:
            continue
        sc = scen.get(m["scenarioId"])
        if not sc:
            continue
        out.append((r, m, sc))
    return out


def subset(rowset):
    """Deterministic, stratified, and drawn only from validation.

    The last two strata recreate the live failure shapes by STRUCTURE - a turn whose plan licenses
    a question and whose facts invite a stock closer, and a turn whose subject is narrow enough
    that answering something adjacent is visibly wrong. Neither reaches into test or hard-eval.
    """
    def has(sc, policy):
        return any(f["policy"] == policy for f in sc["approvedFacts"])

    strata = {
        # no-must reactions: the acknowledgement stratum, where evasion showed up
        "no-must": lambda r, m, sc: not has(sc, MUST),
        # forbidden-question turns: where the base model asks anyway
        "question-forbidden": lambda r, m, sc:
            sc["question"]["policy"] == "none" and has(sc, MUST),
        # ambiguity / unknown: the hard-case structure, recreated in validation
        "ambiguity-unknown": lambda r, m, sc:
            bool(sc.get("intentionalAmbiguities")) or bool(sc.get("epistemicUnknowns")),
        # b4 register combinations - the family where unsupported detail appeared
        "b4-structure": lambda r, m, sc: m["familyId"] == "b4",
        # b6 distractor resistance - background that must not surface
        "b6-structure": lambda r, m, sc:
            m["familyId"] == "b6" or has(sc, BACKGROUND),
        # the live "Hope..." shape: a licensed question plus a stock-closer temptation
        "stock-closer-risk": lambda r, m, sc:
            sc["question"]["policy"] in ("may_ask", "must_ask") and has(sc, MUST),
        # the live plumber/tea shape: one narrow concrete subject, nothing else licensed
        "narrow-subject": lambda r, m, sc:
            len([f for f in sc["approvedFacts"] if f["policy"] in (MUST, MAY)]) == 1
            and sc["question"]["policy"] == "none",
    }

    want = {"no-must": 8, "question-forbidden": 8, "ambiguity-unknown": 6,
            "b4-structure": 6, "b6-structure": 6, "stock-closer-risk": 8, "narrow-subject": 8}

    chosen, seen = [], set()
    for name, pred in strata.items():
        matches = sorted((r["id"] for r, m, sc in rowset if pred(r, m, sc) and r["id"] not in seen))
        n = min(want[name], len(matches))
        if n == 0:
            continue
        stride = len(matches) / n
        for i in range(n):
            rid = matches[int(i * stride)]
            if rid in seen:
                continue
            seen.add(rid)
            chosen.append((name, rid))
    return chosen


def measure(results, rowset):
    """Everything the deterministic gates do not measure, plus the ones they do."""
    by_id = {r["id"]: (r, m, sc) for r, m, sc in rowset}
    replies, openings, endings, lengths, lat = [], [], [], [], []
    relevant = grounded = asked_when_forbidden = missing_required_q = 0
    unsupported = 0
    forbidden_qs = required_qs = 0

    for rid, text, ms in results:
        r, m, sc = by_id[rid]
        replies.append(" ".join(text.lower().split()))
        words = text.split()
        lengths.append(len(words))
        lat.append(ms)
        openings.append(" ".join(words[:4]).lower())
        endings.append(" ".join(words[-4:]).lower())

        said = content_words(text)
        topic = content_words(sc.get("userMessage"))
        for f in sc["approvedFacts"]:
            if f["policy"] in (MUST, MAY):
                topic |= content_words(f["text"])
        # Topical relevance: does the reply engage the subject at all? A relative measure across
        # configurations, not a gate - a faithful far paraphrase can share no vocabulary.
        if said & topic:
            relevant += 1

        supplied = set(topic)
        for t in sc.get("history", []):
            supplied |= content_words(t.get("text"))
        for f in sc["approvedFacts"]:
            supplied |= content_words(f["text"])
        if not (said - supplied):
            grounded += 1
        unsupported += len(said - supplied)

        policy = sc["question"]["policy"]
        has_q = "?" in text
        if policy == "none":
            forbidden_qs += 1
            if has_q:
                asked_when_forbidden += 1
        elif policy == "must_ask":
            required_qs += 1
            if not has_q:
                missing_required_q += 1

    n = len(results)
    lengths.sort(); lat.sort()
    return {
        "rows": n,
        "topicalRelevance": round(relevant / n, 4),
        "distinctReplies": round(len(set(replies)) / n, 4),
        "distinctOpenings": round(len(set(openings)) / n, 4),
        "distinctEndings": round(len(set(endings)) / n, 4),
        "questionCompliance": round(
            1 - (asked_when_forbidden + missing_required_q) / max(1, forbidden_qs + required_qs), 4),
        "askedWhenForbidden": asked_when_forbidden,
        "missingRequiredQuestion": missing_required_q,
        "fullyGrounded": round(grounded / n, 4),
        "unsupportedWordsPerReply": round(unsupported / n, 2),
        "medianWords": lengths[len(lengths) // 2],
        "latencyP50Ms": round(lat[len(lat) // 2]),
        "latencyP95Ms": round(lat[min(len(lat) - 1, int(len(lat) * 0.95))]),
    }


def run(ids, rowset, cfg, tag):
    name, temp, top_p, rep, ng = cfg
    by_id = {r["id"]: r for r, m, sc in rowset}
    results, gens = [], []
    for rid in ids:
        r = by_id[rid]
        t0 = time.perf_counter()
        resp = post({
            "model": "run-2", "stream": False,
            "options": {"temperature": temp, "top_p": top_p, "repetition_penalty": rep,
                        "no_repeat_ngram_size": ng, "num_predict": 220, "seed": 20260830},
            "messages": [{"role": "system", "content": r["system"]},
                         {"role": "user", "content": r["input"]}],
        })
        ms = (time.perf_counter() - t0) * 1000
        text = resp["message"]["content"].strip()
        results.append((rid, text, ms))
        gens.append({"id": rid, "target": text})
    out = EV / f"gen-run-2-{tag}-{name}.jsonl"
    with io.open(out, "w", encoding="utf-8", newline="\n") as f:
        for g in gens:
            f.write(json.dumps(g) + "\n")
    return results, out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--stage", default="grid")
    ap.add_argument("--config", default=None)
    args = ap.parse_args()

    rowset = load()
    chosen = subset(rowset)
    ids = [rid for _, rid in chosen]

    if args.stage == "subset":
        from collections import Counter
        print(f"validation rows available: {len(rowset)}")
        print(f"subset: {len(ids)} rows")
        for k, v in Counter(s for s, _ in chosen).items():
            print(f"  {k:22}{v}")
        (EV / "decoding-subset.json").write_text(
            json.dumps([{"stratum": s, "id": r} for s, r in chosen], indent=2) + "\n",
            encoding="utf-8")
        return

    if args.stage == "full":
        cfg = next(c for c in GRID if c[0] == args.config)
        full = [r["id"] for r, m, sc in rowset]
        results, path = run(full, rowset, cfg, "validation-full")
        m = measure(results, rowset)
        print(f"FULL VALIDATION  {cfg[0]}  n={len(full)}")
        print(json.dumps(m, indent=2))
        (EV / f"decoding-full-{cfg[0]}.json").write_text(
            json.dumps({"config": cfg[0], "metrics": m}, indent=2) + "\n", encoding="utf-8")
        print(f"-> {path}")
        return

    table = []
    for cfg in GRID:
        started = time.perf_counter()
        results, _ = run(ids, rowset, cfg, "grid")
        m = measure(results, rowset)
        m["config"] = cfg[0]
        m["wallSec"] = round(time.perf_counter() - started, 1)
        table.append(m)
        print(f"{cfg[0]:26} rel {m['topicalRelevance']:.3f}  distinct {m['distinctReplies']:.3f}  "
              f"open {m['distinctOpenings']:.3f}  end {m['distinctEndings']:.3f}  "
              f"qc {m['questionCompliance']:.3f}  unsup {m['unsupportedWordsPerReply']:.2f}  "
              f"med {m['medianWords']}  p50 {m['latencyP50Ms']}ms", flush=True)

    (EV / "decoding-grid.json").write_text(json.dumps(table, indent=2) + "\n", encoding="utf-8")
    print(f"\n-> {EV / 'decoding-grid.json'}")


if __name__ == "__main__":
    main()
