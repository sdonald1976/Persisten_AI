"""Provenance replay: one real turn's exact plan through all four arms.

For each arm: greedy decoding (temperature 0) once, then the production sampling
configuration (temperature 0.6, top_p 0.9, num_predict 220) three times to show the
distribution rather than one lucky draw. The server is expected on --port already
loaded with the right adapter; this script only sends requests.
"""
import argparse
import json
import urllib.request
from pathlib import Path

import sys
sys.path.insert(0, str(Path(__file__).parent))
from train_run1a import SYSTEM_PROMPT, build_user_prompt  # byte-exact mirrors

parser = argparse.ArgumentParser()
parser.add_argument("--port", type=int, default=11435)
parser.add_argument("--arm", required=True)
parser.add_argument("--model", default="replay")
parser.add_argument("--plan2", required=True)
parser.add_argument("--message", required=True)
args = parser.parse_args()

plan2 = open(args.plan2, encoding="utf-8", newline="").read()
user = build_user_prompt(plan2, [], args.message)

def ask(temperature):
    payload = json.dumps({
        "model": args.model, "stream": False,
        "options": {"temperature": temperature, "num_predict": 220},
        "messages": [{"role": "system", "content": SYSTEM_PROMPT},
                     {"role": "user", "content": user}],
    }).encode()
    req = urllib.request.Request(f"http://localhost:{args.port}/api/chat", payload,
                                 {"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=300) as r:
        return json.load(r)["message"]["content"].strip()

out = {"arm": args.arm, "greedy": ask(0.0), "sampled": [ask(0.6) for _ in range(3)]}
print(json.dumps(out, ensure_ascii=False))
