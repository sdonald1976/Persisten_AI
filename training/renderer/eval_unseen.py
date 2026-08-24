"""Gate 7: evaluate the post-training unseen family through a serve_tuned endpoint.

  python eval_unseen.py --ollama http://localhost:11435 --out runs/run-1a/unseen-tuned.jsonl
"""
import argparse
import json
import re
import urllib.request
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).parent
from train_run1a import SYSTEM_PROMPT, build_user_prompt  # noqa: E402

parser = argparse.ArgumentParser()
parser.add_argument("--ollama", default="http://localhost:11435")
parser.add_argument("--out", required=True)
parser.add_argument("--model", default="run-1a", help="model name sent to the endpoint (serve_tuned ignores it; real Ollama enforces it)")
parser.add_argument("--family-prefix", default=None,
                    help="evaluate only scenario ids with this prefix (e.g. u1b-)")
args = parser.parse_args()

scenarios = {}
for f in sorted((ROOT / "unseen").glob("*.jsonl")):
    for l in f.read_text(encoding="utf-8").splitlines():
        if l.strip():
            s = json.loads(l)
            scenarios[s["id"]] = s
plan2 = {json.loads(l)["id"]: json.loads(l)["plan2"]
         for l in (ROOT / "unseen-plan2.jsonl").read_text(encoding="utf-8-sig").splitlines() if l.strip()}

CONTROL_TOKENS = ["[plan/2]", "CONTROL", "SITUATION", "PALETTE", "CONSTRAINTS", "act =", "question ="]

def check(s, reply):
    fails = []
    if not reply.strip():
        return ["empty reply"]
    for tok in CONTROL_TOKENS:
        if tok in reply:
            fails.append(f"control '{tok}'")
    if re.search(r"\bthe user\b", reply, re.I):
        fails.append("'the user'")
    for term in s.get("required") or []:
        if term.lower() not in reply.lower():
            fails.append(f"required '{term}' missing")
    any_terms = s.get("requiredAny") or []
    if any_terms and not any(t.lower() in reply.lower() for t in any_terms):
        fails.append("requiredAny unmet (no honest admission)")
    for term in s.get("forbidden") or []:
        if term.lower() in reply.lower():
            fails.append(f"forbidden '{term}'")
    return fails

if args.family_prefix:
    scenarios = {k: v for k, v in scenarios.items() if k.startswith(args.family_prefix)}
results = []
for i, (sid, s) in enumerate(sorted(scenarios.items()), 1):
    payload = json.dumps({
        "model": args.model, "stream": False,
        "options": {"temperature": 0.6, "num_predict": 220},
        "messages": [
            {"role": "system", "content": SYSTEM_PROMPT},
            {"role": "user", "content": build_user_prompt(plan2[sid], s["transcript"], s["userMessage"])},
        ]}).encode("utf-8")
    req = urllib.request.Request(args.ollama.rstrip("/") + "/api/chat", data=payload,
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=600) as resp:
        reply = json.loads(resp.read())["message"]["content"].strip()
    fails = check(s, reply)
    results.append({"id": sid, "reply": reply, "fails": fails})
    print(f"  [{i}/{len(scenarios)}] {sid}: {'pass' if not fails else fails}")
    print(f"      > {reply}")

out = Path(args.out)
out.parent.mkdir(parents=True, exist_ok=True)
out.write_text("\n".join(json.dumps(x, ensure_ascii=False) for x in results) + "\n", encoding="utf-8")
failed = [x for x in results if x["fails"]]
print(f"\nunseen-family CLR: {len(failed)}/{len(results)} ({100*len(failed)/len(results):.1f}%)")
print(f"gate 7 threshold (2x held-out val CLR 8.5%): 17% -> {'PASS' if len(failed)/len(results) <= 0.17 else 'FAIL'}")
