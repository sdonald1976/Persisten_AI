"""Builds the blind naturalness review package from the baseline bench transcripts.

Outputs (training/renderer/review/):
  blind-review.md   - the review copy: context, collapsible plan, anonymous A-D outputs,
                      fill-in judgment lines. No model names, no fidelity scores.
  answer-key.json   - SEALED: the per-fixture letter->model mapping. Do not open before
                      judging.

Judgments stay in the review file / chat - evaluation data, never the conversation DB.
"""
import json
import random
import re
from pathlib import Path

ROOT = Path(__file__).parent
MODELS = {
    "qwen2.5:1.5b-instruct": "baseline-results.md",
    "llama3.2:3b": "baseline-results.md",
    "hf.co/bartowski/L3-8B-Stheno-v3.2-GGUF:Q4_K_M": "baseline-results.md",
    "qwen3:8b": "baseline-qwen3-rerun.md",  # corrected think:false run
}
SEED = 20260820

def parse_outputs(path: Path, model: str) -> dict[str, str]:
    text = path.read_text(encoding="utf-8")
    section = None
    for chunk in text.split("\n## ")[1:]:
        if chunk.startswith(model):
            section = chunk
            break
    if section is None:
        raise SystemExit(f"model section not found: {model} in {path}")
    outputs = {}
    for m in re.finditer(r"\n### (\S+).*?\n> (.*?)\n", section):
        outputs[m.group(1)] = m.group(2).strip()
    return outputs

def compact_plan(plan: dict) -> str:
    lines = [f"ACT: {plan['act']}"]
    for a in plan.get("acknowledgments", []):
        lines.append(f"ACK {a['kind']} (error: {a['errorOwner']}): \"{a['text']}\"")
    for c in plan.get("content", []):
        label = {"must-state": "MUST-STATE", "must-not-contradict": "NEVER-CONTRADICT"}.get(
            c["requirement"], "MAY-USE")
        lines.append(f"{label} {c['kind']}: \"{c['text']}\"")
    for e in plan.get("epistemic", []):
        lines.append(f"EPISTEMIC {e['kind']}: {e['subject']}")
    q = plan.get("question")
    if q:
        lines.append(f"QUESTION {q['kind']}{' (mandatory)' if q.get('mandatory') else ''}: {q['text']}")
    t = plan.get("tone", {})
    lines.append(f"TONE register: {t.get('register')} | mood: {t.get('moodNote')} | persona: {t.get('personaStyle')}")
    return "\n".join(lines)

fixtures = [json.loads(l) for l in (ROOT / "fixtures.jsonl").read_text(encoding="utf-8").splitlines() if l.strip()]
outputs = {m: parse_outputs(ROOT / f, m) for m, f in MODELS.items()}

rng = random.Random(SEED)
review, key = [], {}
review.append("# Blind naturalness review — renderer baselines\n")
review.append(
    "Four renderers produced each reply from the SAME authoritative ResponsePlan. "
    "Model identities are hidden and shuffled per specimen. For each output, mark one of:\n\n"
    "**would use / acceptable / technically correct but lifeless / unnatural / wrong**\n\n"
    "and optionally a note on what bothered you. Judge NATURALNESS as Ava's voice — "
    "fidelity has already been machine-scored and is deliberately not shown here.\n")

for f in fixtures:
    fid = f["id"]
    letters = ["A", "B", "C", "D"]
    models = list(MODELS.keys())
    rng.shuffle(models)
    key[fid] = dict(zip(letters, models))

    review.append(f"\n---\n\n## {f['name']}\n")
    review.append("**Context:**\n")
    for t in f["transcript"]:
        who = "Scott" if t["role"] == "user" else "Ava"
        review.append(f"> [{who}] {t['text']}")
    review.append(f"> [Scott] **{f['userMessage']}**\n")
    review.append("<details><summary>ResponsePlan (reference)</summary>\n")
    review.append("```")
    review.append(compact_plan(f["plan"]))
    review.append("```\n</details>\n")
    for letter, model in key[fid].items():
        reply = outputs[model].get(fid, "(no output)")
        review.append(f"**{letter}.** {reply}\n")
        review.append(f"- [ ] {letter}: judgment: ________   notes: ________\n")

out = ROOT / "review"
out.mkdir(exist_ok=True)
(out / "blind-review.md").write_text("\n".join(review), encoding="utf-8")
(out / "answer-key.json").write_text(
    json.dumps({"seed": SEED, "sealed": "DO NOT OPEN BEFORE JUDGING", "key": key}, indent=2),
    encoding="utf-8")
print(f"review: {sum(1 for _ in fixtures)} specimens x 4 outputs -> {out / 'blind-review.md'}")
