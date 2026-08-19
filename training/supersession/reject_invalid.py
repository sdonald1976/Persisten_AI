"""Evaluator's rejection pass: drop semantically INVALID rows from a synthetic-life artifact.

Never relabels. Applied identically to the structured and naturalized artifacts so the
controlled comparison stays controlled. Every rule is a semantic-validity argument:

R1 cross-domain value: a routine value stored as the coffee preference is not a fact.
R2 vacuous refinement: 'X with extra detail' adds words, not specificity - REFINES does not hold.
R3 vacuous correction: 'actually X' where stripping 'actually ' equals the previous value
   corrects nothing - the phase-3 'corrects a value to itself' defect wearing a prefix.
R4 listener-subject shift (naturalized only): the utterance asserts the fact about 'you'
   while the fact's subject is the user - the validator only checks other:* subjects.
R5 task-instruction leakage (naturalized only): the utterance contains the prompt's own
   meta-language about the classification task.
R6 subject-id narration (naturalized only): the utterance names the simulator's subject id
   ("Life-0026 switched to...") - third-person narration of a first-person fact, which no
   real user produces and which quietly shifts the subject.
"""
import json, re, sys, collections

src, dst = sys.argv[1], sys.argv[2]
rows = [json.loads(l) for l in open(src, encoding="utf-8")]
ROUTINES = ("late lunches", "weekend shifts", "evening walks", "early gym sessions", "night shifts", "hotel breakfasts")
META = ("change over time", "corrected from", "semantic relation", "temporal scope",
        "this task", "immutable", "ground truth")
LISTENER = re.compile(r"\byour (favorite|favourite|preferred|preference)|\byou (prefer|enjoy|drink|like)\b", re.I)

kept, rejected = [], collections.Counter()
examples = collections.defaultdict(list)
for r in rows:
    cur = r["currentFact"]["value"]
    prev = (r.get("previousFact") or {}).get("value")
    u = r["utterance"]
    why = None
    if r["currentFact"]["key"] == "preference.coffee" and any(rt in cur.lower() for rt in ROUTINES):
        why = "R1 cross-domain value in coffee slot"
    elif "with extra detail" in cur.lower():
        why = "R2 vacuous refinement value"
    elif cur.lower().startswith("actually ") and prev and cur.lower().removeprefix("actually ").strip() == prev.lower().strip():
        why = "R3 vacuous correction (actually + same value)"
    elif r.get("verbalizerId", "").startswith("llm:"):
        subj = r["currentFact"]["subjectId"]
        if not subj.startswith("other:") and LISTENER.search(u):
            why = "R4 listener-subject shift"
        elif any(m in u.lower() for m in META):
            why = "R5 task-instruction leakage"
        elif re.search(r"life-\d{4}", u, re.I):
            why = "R6 subject-id narration"
    if why:
        rejected[(why, r["expectedLabel"])] += 1
        if len(examples[why]) < 2:
            examples[why].append(u[:110])
    else:
        kept.append(r)

with open(dst, "w", encoding="utf-8") as f:
    for r in kept:
        f.write(json.dumps(r, ensure_ascii=False) + "\n")
print(f"{src} -> {dst}: kept {len(kept)} of {len(rows)}")
for (why, label), n in sorted(rejected.items()):
    print(f"   {n:4d}  {why}  [{label}]")
for why, es in examples.items():
    for e in es:
        print(f"        {why[:2]} e.g. {e!r}")
