"""Blind review round 2: the plan/2 outputs of the three small finalists.

Outputs (training/renderer/review/):
  blind-review-2.md    - review copy: context + anonymous A/B/C, judgment + note axes.
  answer-key-2.json    - SEALED until judging is complete.
Deterministic scores exist in ab-v2.md and are deliberately not shown here.
"""
import json
import random
import re
from pathlib import Path

ROOT = Path(__file__).parent
MODELS = ["qwen2.5:1.5b-instruct", "llama3.2:3b", "qwen2.5:3b-instruct"]
SOURCE = ROOT / "ab-v2.md"
SEED = 20260821

def parse_outputs(model: str) -> dict[str, str]:
    text = SOURCE.read_text(encoding="utf-8")
    for chunk in text.split("\n## ")[1:]:
        if chunk.startswith(model):
            return {m.group(1): m.group(2).strip()
                    for m in re.finditer(r"\n### (\S+).*?\n> (.*?)\n", chunk)}
    raise SystemExit(f"missing section: {model}")

fixtures = [json.loads(l) for l in (ROOT / "fixtures.jsonl").read_text(encoding="utf-8").splitlines() if l.strip()]
outputs = {m: parse_outputs(m) for m in MODELS}
rng = random.Random(SEED)

review, key = [], {}
review.append("# Blind naturalness review, round 2 — plan/2 serialization\n")
review.append(
    "Three renderers, same specimens, new serialization. Identities hidden and shuffled "
    "per specimen. Mark each output:\n\n"
    "**would use / acceptable / technically correct but lifeless / unnatural / wrong**\n\n"
    "Notes welcome on: humor, warmth, spontaneity, sentence variety, repetitiveness, "
    "assistant-ish tone, plan-parroting, and whether it feels like Ava rather than a "
    "renderer following instructions. Fidelity scores exist and are hidden until after "
    "your judgment.\n")

for f in fixtures:
    fid = f["id"]
    letters = ["A", "B", "C"]
    models = MODELS[:]
    rng.shuffle(models)
    key[fid] = dict(zip(letters, models))

    review.append(f"\n---\n\n## {f['name']}\n")
    review.append("**Context:**\n")
    for t in f["transcript"]:
        who = "Scott" if t["role"] == "user" else "Ava"
        review.append(f"> [{who}] {t['text']}")
    review.append(f"> [Scott] **{f['userMessage']}**\n")
    for letter, model in key[fid].items():
        review.append(f"**{letter}.** {outputs[model].get(fid, '(no output)')}\n")
        review.append(f"- [ ] {letter}: judgment: ________   notes: ________\n")

out = ROOT / "review"
(out / "blind-review-2.md").write_text("\n".join(review), encoding="utf-8")
(out / "answer-key-2.json").write_text(
    json.dumps({"seed": SEED, "sealed": "DO NOT OPEN BEFORE JUDGING", "key": key}, indent=2),
    encoding="utf-8")
print(f"round 2: {len(fixtures)} specimens x 3 outputs -> {out / 'blind-review-2.md'}")
