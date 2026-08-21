"""Render the 47 held-out-family validation scenarios through a serve_tuned.py
endpoint and score them with the same deterministic checks the curator used.

Used for the gates the 11-fixture bench is too small to judge statistically:
over-specialization (distinct-opening-trigram ratio >= 0.60), question discipline,
and validation CLR. Run once against the base (prompted control) and once against
the tuned adapter; compare.

  python eval_val.py --ollama http://localhost:11435 --out runs/run-1a/val-tuned.jsonl
"""
import argparse
import json
import re
import urllib.request
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).parent
DATASET = ROOT / "dataset"

SYSTEM_PROMPT_FILE = None  # inline below, kept identical to train_run1a.py
from train_run1a import SYSTEM_PROMPT, build_user_prompt  # noqa: E402

parser = argparse.ArgumentParser()
parser.add_argument("--ollama", default="http://localhost:11435")
parser.add_argument("--out", required=True)
args = parser.parse_args()

rows = [json.loads(l) for l in (DATASET / "train-200.jsonl").read_text(encoding="utf-8").splitlines() if l.strip()]
splits = json.loads((DATASET / "splits.json").read_text(encoding="utf-8"))
val = [r for r in rows if r["family"] in set(splits["validationFamilies"])]
print(f"{len(val)} validation scenarios -> {args.ollama}")

CONTROL_TOKENS = ["[plan/2]", "CONTROL", "SITUATION", "PALETTE", "CONSTRAINTS", "act =", "question ="]

def q_mode(plan2):
    m = re.search(r"question = (\S+)", plan2)
    return "none" if not m or m.group(1) == "none" else m.group(1).split(":")[-1]

def check(r, reply):
    fails = []
    if not reply.strip():
        return ["empty reply"]
    for tok in CONTROL_TOKENS:
        if tok in reply:
            fails.append(f"control '{tok}'")
    if re.search(r"\bthe user\b", reply, re.I):
        fails.append("'the user'")
    for term in r.get("required") or []:
        if term.lower() not in reply.lower():
            fails.append(f"required '{term}' missing")
    any_terms = r.get("requiredAny") or []
    if any_terms and not any(t.lower() in reply.lower() for t in any_terms):
        fails.append("requiredAny unmet")
    for term in r.get("forbidden") or []:
        if term.lower() in reply.lower():
            fails.append(f"forbidden '{term}'")
    mode = q_mode(r["plan2"])
    ends_q = reply.rstrip().endswith("?")
    if mode == "none" and ends_q:
        fails.append("question on closed plan")
    if mode == "mandatory" and not ends_q:
        fails.append("mandatory question missing")
    return fails

results = []
for i, r in enumerate(val, 1):
    payload = json.dumps({
        "model": "run-1a", "stream": False,
        "options": {"temperature": 0.6, "num_predict": 220},
        "messages": [
            {"role": "system", "content": SYSTEM_PROMPT},
            {"role": "user", "content": build_user_prompt(r["plan2"], r["transcript"], r["userMessage"])},
        ]}).encode("utf-8")
    req = urllib.request.Request(args.ollama.rstrip("/") + "/api/chat", data=payload,
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=600) as resp:
        reply = json.loads(resp.read())["message"]["content"].strip()
    fails = check(r, reply)
    results.append({"id": r["id"], "stratum": r["stratum"], "reply": reply, "fails": fails})
    print(f"  [{i}/{len(val)}] {r['id']}: {'pass' if not fails else fails}")

out = Path(args.out)
out.parent.mkdir(parents=True, exist_ok=True)
out.write_text("\n".join(json.dumps(x, ensure_ascii=False) for x in results) + "\n", encoding="utf-8")

failed = [x for x in results if x["fails"]]
openings = Counter(" ".join(re.findall(r"[a-z']+", x["reply"].lower())[:3]) for x in results)
closed = [x for x in results if q_mode(next(r for r in val if r["id"] == x["id"])["plan2"]) == "none"]
closed_q = [x for x in closed if x["reply"].rstrip().endswith("?")]
print(f"\nval CLR: {len(failed)}/{len(results)} ({100*len(failed)/len(results):.1f}%)")
print(f"opening-trigram diversity: {len(openings)}/{len(results)} ({len(openings)/len(results):.2f}; gate floor 0.60)")
print(f"questions on closed plans: {len(closed_q)}/{len(closed)}")
print(f"written: {out}")
