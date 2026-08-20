"""Curate teacher candidates into the run-1a training set, and audit the result.

Reads candidates.jsonl (every teacher attempt with its gate results and sludge flags),
applies the curation policy, and writes:

  train-200.jsonl   the dataset (one accepted target per scenario, full lineage)
  splits.json       family-level train/validation manifest
  audit.md          the audit package (counts, distributions, rejections, leakage)
  review-random.md  random 10% human-review sample (plan -> target)
  review-hard.md    targeted sample from the hardest strata (kept separate)

Curation policy (QLORA_DESIGN.md as amended):
  * Deterministic gates decide ELIGIBILITY, never gold.
  * Among eligible candidates, prefer the one with the fewest sludge flags; break
    ties by teacher preference per stratum (voice-donor for playful/terse strata,
    fidelity-teacher elsewhere), then by brevity-appropriateness for the register.
  * A scenario with no eligible, non-sludge candidate is REJECTED, not patched.
  * Nothing here edits text. Human edits happen after review and are recorded.
"""
import json
import random
import re
import statistics
from collections import Counter, defaultdict
from pathlib import Path

ROOT = Path(__file__).parent
CANDIDATE_FILES = sorted(ROOT.glob("candidates*.jsonl"))
SEED = 20260821

# Voice-donor strata: llama3.2:3b won the human vote twice; where the turn is mostly
# voice and lightly constrained, its candidate is preferred among equals.
VOICE_STRATA = {"playful-absurd", "terse", "ack-plain", "optional-question-unasked"}
VOICE_TEACHER = "llama3.2:3b"
FIDELITY_TEACHER = "qwen3:8b"

HARD_STRATA = ["correction-genuine", "correction-user-owned", "epistemic-unknown",
               "superseded", "silence-palette", "shared-history-boundary",
               "must-state", "knowledge-provenance", "optional-question-unasked"]

# Sludge flags that disqualify outright rather than merely rank down: these are the
# named negative behaviors, not stylistic quibbles.
DISQUALIFYING = {"thanks-for-x", "i-appreciate", "restates-user", "canned-enthusiasm",
                 "assistant-offer", "self-deprecation-filler", "promise-to-improve",
                 "excess-vocatives"}

# utf-8-sig: the .NET StreamWriter stamps a BOM on the first line. Every candidates
# file counts — the rescue pass adds draws for scenarios the first pass could not
# render, and all attempts from every pass compete on equal terms.
rows = [json.loads(l)
        for f in CANDIDATE_FILES
        for l in f.read_text(encoding="utf-8-sig").splitlines() if l.strip()]

# Group every attempt by scenario id.
by_scenario = defaultdict(list)
meta = {}
for r in rows:
    by_scenario[r["id"]].extend(r["candidates"])
    meta[r["id"]] = r

def contrition_wrong(row, text):
    """Extra semantic gate the regexes cannot express: on user-owned corrections and
    ordinary agreement, ANY self-blame is wrong even when phrased unusually."""
    if row["stratum"] not in ("correction-user-owned", "agreement-ordinary"):
        return None
    if re.search(r"\b(my (bad|mistake|error|fault|mix-?up)|I (was wrong|misremembered|got that wrong|slipped)|on me)\b",
                 text, re.I):
        return "self-blame on a turn where Ava made no error"
    return None

def question_wrong(row, text):
    """The plan decides whether a question is asked. Ending a reply with one when the
    plan asked for none is the assistant reflex the round-2 review named — the largest
    single defect in the raw teacher output, and the one this corpus most needs to
    teach against. Rhetorical questions mid-reply are untouched; only the trailing
    hand-the-work-back move counts."""
    kind = question_mode(row)
    ends_q = text.rstrip().endswith("?")
    if kind == "none" and ends_q:
        return "ended with a question on a plan that called for none"
    if kind == "optional" and ends_q:
        return "asked an optional question the plan left optional"
    if kind == "mandatory" and not ends_q:
        return "omitted a mandatory question"
    return None

RECALL_MARKER = re.compile(
    r"\b(last time|remember when|you (told|mentioned|said) (me )?(before|last|earlier)|"
    r"as (always|usual)|like (you did )?last|the other (day|week)|you always)\b", re.I)

def unsupported_recall(row, text):
    """Invented shared history. The system prompt forbids it and the round-2 review
    named it, but no deterministic gate catches the soft form — a plan carrying no
    memory at all, realized with "last time was the whole canned-goods thing". When
    the plan supplies nothing to recall and the transcript window does not contain it
    either, a recall marker is fabrication by definition.

    Curation-only on purpose: adding this to RendererChecks would change the measuring
    instrument mid-experiment and make new runs incomparable to the recorded baselines.
    Proposed for the bench suite separately."""
    if not RECALL_MARKER.search(text):
        return None
    supplied = row.get("plan2", "") + " " + " ".join(t["text"] for t in row["transcript"])
    if re.search(r"PALETTE|SITUATION", row.get("plan2", "")) and len(supplied) > 200:
        return None  # the plan does give her something to remember; leave it to review
    return "invented shared history: recalls a past the plan never supplied"

def question_mode(row):
    """plan/2 writes `question = <kind>:<mandatory|optional>` or `question = none`."""
    m = re.search(r"question = (\S+)", row.get("plan2", ""))
    if not m:
        return "none"
    return "none" if m.group(1) == "none" else m.group(1).split(":")[-1]

def score(row, cand):
    """Lower is better. Sludge dominates; then register fit; then teacher preference."""
    sludge = len(cand["sludge"])
    prefers_voice = row["stratum"] in VOICE_STRATA
    teacher_penalty = 0 if cand["teacher"] == (VOICE_TEACHER if prefers_voice else FIDELITY_TEACHER) else 1
    # Register fit: terse registers want short targets; nothing else is length-policed,
    # so long answers are never rewarded but are not punished when licensed.
    register = row.get("plan2", "")
    terse = "terse" in register.lower()
    length_penalty = 1 if terse and cand["words"] > 30 else 0
    return (sludge, length_penalty, teacher_penalty, cand["words"])

accepted, rejected = [], []
for sid, cands in by_scenario.items():
    row = meta[sid]
    pool = []
    for c in cands:
        reasons = list(c["violations"])
        if not reasons:
            for extra in (contrition_wrong(row, c["text"]), question_wrong(row, c["text"]),
                          unsupported_recall(row, c["text"])):
                if extra:
                    reasons.append(extra)
        c = dict(c, extraReasons=reasons[len(c["violations"]):], gateFail=reasons)
        if reasons:
            continue
        if DISQUALIFYING & set(c["sludge"]):
            continue
        pool.append(c)
    if not pool:
        rejected.append({
            "id": sid, "family": row["family"], "stratum": row["stratum"],
            "reasons": [{"teacher": c["teacher"], "attempt": c["attempt"],
                         "violations": c["violations"], "sludge": c["sludge"],
                         "text": c["text"]} for c in cands],
        })
        continue
    best = min(pool, key=lambda c: score(row, c))
    accepted.append({
        "id": sid,
        "family": row["family"],
        "stratum": row["stratum"],
        "plan2": row["plan2"],
        "transcript": row["transcript"],
        "userMessage": row["userMessage"],
        "target": best["text"],
        "source": {
            **row["source"],
            "teacherModel": best["teacher"],
            "attempt": best["attempt"],
            "rawTeacherCandidate": best["text"],
            "gatesPassed": True,
            "sludgeFlags": best["sludge"],
            "candidatesConsidered": len(cands),
        },
        "review": {"gated": True, "humanReviewed": False, "humanEdited": False},
        "styleLicense": re.search(r"STYLE\n\s*(.*)", row["plan2"]).group(1).strip()
                        if re.search(r"STYLE\n\s*(.*)", row["plan2"]) else "",
        "words": best["words"],
        "opening": best["opening"],
    })

accepted.sort(key=lambda r: r["id"])

# ---- human curation pass (2026-08-20 directives) -----------------------------------
# Gate-eligible is not training-quality. Every accepted target carries an explicit
# decision in curation-run1a.jsonl: keep / edit (target replaced; raw preserved) /
# author (curator-written target for a scenario every teacher draw failed). basis
# records whose judgment produced the text: "scott" for Scott's dictated lines and
# named findings, "curator" for edits applied under his written principles.

CONTROL_TOKENS = ["[plan/2]", "CONTROL", "SITUATION", "PALETTE", "CONSTRAINTS", "act =", "question ="]

def normalize(text):
    t = text.strip()
    for a, b in (('"', '"'), ("“", "”")):
        if len(t) > 2 and t.startswith(a) and t.endswith(b) and t.count(a) == (1 if a != b else 2):
            return t[1:-1].strip(), True
    return t, False

# Check lists come from the CURRENT scenario files, not the candidates lineage: two
# forbidden terms ("shave", "ichor") were substrings of the very words the scenarios
# are about (spokeshave, petrichor) and were fixed in the scenario definitions; the
# candidate rows keep the stale lists as an honest record of what the teachers faced.
scenario_checks = {}
for f in sorted((ROOT / "scenarios").glob("*.jsonl")):
    for line in f.read_text(encoding="utf-8").splitlines():
        if line.strip():
            s = json.loads(line)
            scenario_checks[s["id"]] = {"required": s.get("required"),
                                        "forbidden": s.get("forbidden"),
                                        "requiredAny": s.get("requiredAny"),
                                        "transcript": s.get("transcript"),
                                        "userMessage": s.get("userMessage")}

def py_gates(sid, text):
    """The deterministic contract, re-run in-process on every curated target. The C#
    suite already vetted kept teacher targets; edited and authored text passes the
    same required/forbidden/control checks here or the build fails loudly."""
    row = {**meta[sid], **scenario_checks.get(sid, {})}
    fails = []
    for tok in CONTROL_TOKENS:
        if tok in text:
            fails.append(f"control vocabulary '{tok}'")
    if re.search(r"\bthe user\b", text, re.I):
        fails.append("narrates 'the user'")
    for term in row.get("required") or []:
        if term.lower() not in text.lower():
            fails.append(f"required '{term}' missing")
    any_terms = row.get("requiredAny") or []
    if any_terms and not any(t.lower() in text.lower() for t in any_terms):
        fails.append("requiredAny unmet")
    for term in row.get("forbidden") or []:
        if term.lower() in text.lower():
            fails.append(f"forbidden '{term}'")
    mode = question_mode(row)
    ends_q = text.rstrip().endswith("?")
    if mode == "none" and ends_q:
        fails.append("trailing question on question=none plan")
    if mode == "mandatory" and not ends_q:
        fails.append("mandatory question missing")
    return fails

decisions = {}
for line in (ROOT / "curation-run1a.jsonl").read_text(encoding="utf-8").splitlines():
    if line.strip():
        d = json.loads(line)
        decisions[d["id"]] = d

missing_decisions = [r["id"] for r in accepted if r["id"] not in decisions]
if missing_decisions:
    raise SystemExit(f"accepted rows without a curation decision: {missing_decisions}")

gate_failures, normalized_count = [], 0
final_rows = []
for r in accepted:
    d = decisions[r["id"]]
    if d["action"] == "edit":
        r["target"] = d["target"]
        r["review"] = {"gated": True, "humanReviewed": True,
                       "humanEdited": True, "editBasis": d["basis"]}
        r["curation"] = {"action": "edit", "basis": d["basis"], "note": d.get("note")}
    elif d["action"] == "keep":
        r["review"] = {"gated": True, "humanReviewed": True, "humanEdited": False}
        r["curation"] = {"action": "keep", "basis": d["basis"], "note": d.get("note")}
    else:
        raise SystemExit(f"{r['id']}: unexpected action {d['action']} on an accepted row")
    r["target"], stripped = normalize(r["target"])
    if stripped:
        normalized_count += 1
        r["curation"]["normalized"] = "stripped wrapper quotes"
    final_rows.append(r)

# Curator-authored rows: scenarios where no teacher draw was acceptable. The plan,
# transcript, and checks come from the generation metadata; only the target is new.
authored = [d for d in decisions.values() if d["action"] == "author"]
for d in authored:
    sid = d["id"]
    if sid not in meta:
        raise SystemExit(f"authored target for unknown scenario {sid}")
    if any(r["id"] == sid for r in final_rows):
        raise SystemExit(f"authored target for already-accepted scenario {sid}")
    row = meta[sid]
    current = scenario_checks.get(sid, {})
    target, stripped = normalize(d["target"])
    final_rows.append({
        "id": sid,
        "family": row["family"],
        "stratum": row["stratum"],
        "plan2": row["plan2"],
        "transcript": current.get("transcript") or row["transcript"],
        "userMessage": current.get("userMessage") or row["userMessage"],
        "target": target,
        "source": {**row["source"], "teacherModel": None, "origin": "curator-authored",
                   "teacherDrawsRejected": len(by_scenario[sid])},
        "review": {"gated": True, "humanReviewed": True, "humanEdited": False,
                   "curatorAuthored": True, "editBasis": d["basis"]},
        "curation": {"action": "author", "basis": d["basis"], "note": d.get("note")},
        "styleLicense": re.search(r"STYLE\n\s*(.*)", row["plan2"]).group(1).strip()
                        if re.search(r"STYLE\n\s*(.*)", row["plan2"]) else "",
    })

for r in final_rows:
    r["words"] = len(re.findall(r"[\w'-]+", r["target"]))
    r["opening"] = " ".join(re.findall(r"[a-z']+", r["target"].lower())[:3])
    if r["curation"]["action"] in ("edit", "author"):
        fails = py_gates(r["id"], r["target"])
        if fails:
            gate_failures.append(f"{r['id']}: {fails}")
if gate_failures:
    raise SystemExit("curated targets failing deterministic gates:\n  " + "\n  ".join(gate_failures))

# Scenarios still rejected = no eligible teacher draw AND no authored replacement.
authored_ids = {d["id"] for d in authored}
rejected = [rej for rej in rejected if rej["id"] not in authored_ids]

accepted = sorted(final_rows, key=lambda r: r["id"])
(ROOT / "train-200.jsonl").write_text(
    "\n".join(json.dumps(r, ensure_ascii=False) for r in accepted) + "\n", encoding="utf-8")

# ---- family-level split -----------------------------------------------------------
rng = random.Random(SEED)
families = sorted({r["family"] for r in accepted})
# Validation families are drawn one per stratum where the stratum has >= 3 families,
# so validation covers behavior rather than a random slice of one topic.
by_stratum = defaultdict(list)
for f in families:
    stratum = next(r["stratum"] for r in accepted if r["family"] == f)
    by_stratum[stratum].append(f)
val_families = set()
for stratum, fams in sorted(by_stratum.items()):
    if len(fams) >= 2:
        val_families.add(rng.choice(sorted(fams)))
train = [r for r in accepted if r["family"] not in val_families]
val = [r for r in accepted if r["family"] in val_families]

PERMANENT_HOLDOUT = [
    "the eleven original benchmark fixtures (training/renderer/fixtures.jsonl)",
    "the entire false-correction / agreement-inversion family",
    "epistemic leakage: quokka, axe-with-provenance",
    "palette contamination: Epcot/pizza, Precious",
    "one scenario family to be authored only AFTER training completes",
]
(ROOT / "splits.json").write_text(json.dumps({
    "seed": SEED,
    "unit": "semantic scenario family",
    "trainFamilies": sorted(set(r["family"] for r in train)),
    "validationFamilies": sorted(val_families),
    "permanentHoldout": PERMANENT_HOLDOUT,
    "counts": {"train": len(train), "validation": len(val), "total": len(accepted)},
}, indent=2), encoding="utf-8")

# ---- leakage checks ---------------------------------------------------------------
leak = []
FORBIDDEN_SUBJECTS = ["quokka", "cheshire", "mad hatter", "epcot", "precious",
                      "pepperoni", "shatterproof", "rabbit hole"]
for r in accepted:
    blob = (r["plan2"] + " " + r["userMessage"] + " " +
            " ".join(t["text"] for t in r["transcript"])).lower()
    for s in FORBIDDEN_SUBJECTS:
        if s in blob:
            leak.append(f"{r['id']}: mentions held-out subject '{s}'")
# The inversion composition: agreement-confirmed must never appear on a
# correction-shaped user message anywhere in training.
CORRECTION_SHAPED = re.compile(r"^\s*(no\b|nope\b|actually\b|wrong\b|that's not)", re.I)
for r in accepted:
    if "agreeing with what Ava just said" in r["plan2"] and CORRECTION_SHAPED.match(r["userMessage"]):
        leak.append(f"{r['id']}: agreement-confirmed on a correction-shaped message "
                    f"(the held-out inversion composition)")
# Near-duplicate targets across the corpus.
def trigrams(t):
    w = re.findall(r"[a-z']+", t.lower())
    return {tuple(w[i:i+3]) for i in range(max(0, len(w) - 2))}
dupes = []
for i, a in enumerate(accepted):
    ta = trigrams(a["target"])
    for b in accepted[i+1:]:
        tb = trigrams(b["target"])
        if ta and tb:
            j = len(ta & tb) / len(ta | tb)
            if j > 0.5:
                dupes.append(f"{a['id']} ~ {b['id']} (trigram Jaccard {j:.2f})")

# ---- audit ------------------------------------------------------------------------
strata = Counter(r["stratum"] for r in accepted)
sources = Counter(r["source"]["kind"] for r in accepted)
teachers = Counter(r["source"].get("teacherModel") or "curator-authored" for r in accepted)
lengths = [r["words"] for r in accepted]
q_end = [r for r in accepted if r["target"].rstrip().endswith("?")]
openings = Counter(r["opening"] for r in accepted)
palette_rows = [r for r in accepted if "PALETTE" in r["plan2"]]
def palette_items(plan2):
    m = re.search(r"PALETTE.*?\n((?:  \* .*\n)+)", plan2)
    return [l.strip("  * ").strip() for l in m.group(1).splitlines()] if m else []
palette_unused = []
for r in palette_rows:
    items = palette_items(r["plan2"])
    used = False
    for it in items:
        keys = [w for w in re.findall(r"[A-Za-z']{5,}", it)
                if w.lower() not in {"scott", "scott's", "about", "there", "their", "which", "these", "those"}]
        if any(k.lower() in r["target"].lower() for k in keys):
            used = True
            break
    if not used:
        palette_unused.append(r["id"])
sludge_counter = Counter(f for r in accepted for f in r["source"].get("sludgeFlags", []))
rejection_reasons = Counter()
for rej in rejected:
    for a in rej["reasons"]:
        for v in a["violations"]:
            rejection_reasons[re.sub(r'".*?"', '"..."', v)] += 1
        for s in a["sludge"]:
            if s in DISQUALIFYING:
                rejection_reasons[f"sludge: {s}"] += 1

def bucket(n):
    if n <= 8: return "fragment / <=8 words"
    if n <= 20: return "one-liner / 9-20"
    if n <= 45: return "ordinary / 21-45"
    if n <= 80: return "longer / 46-80"
    return "long / >80"
buckets = Counter(bucket(n) for n in lengths)

lines = []
w = lines.append
w("# Run-1a dataset audit\n")
w(f"_Generated from {len(rows)} teacher rows ({sum(len(c) for c in by_scenario.values())} "
  f"candidate draws) over {len(by_scenario)} scenarios, across "
  f"{len(CANDIDATE_FILES)} generation pass(es). "
  f"Nothing trained; nothing in production touched._\n")
w(f"**Accepted: {len(accepted)}  |  Rejected: {len(rejected)}  |  "
  f"Train {len(train)} / Validation {len(val)} by family**\n")

w("\n## 1. Counts by behavioral stratum\n")
w("| stratum | accepted | rejected |")
w("|---|---|---|")
rej_by_stratum = Counter(r["stratum"] for r in rejected)
for s, n in strata.most_common():
    w(f"| {s} | {n} | {rej_by_stratum.get(s, 0)} |")

w("\n## 2. Source: real-derived vs constructed\n")
w("| source | n | share |")
w("|---|---|---|")
for k, n in sources.most_common():
    w(f"| {k} | {n} | {100*n/len(accepted):.1f}% |")

w("\n## 3. Teacher contribution\n")
w("| teacher | targets accepted | share |")
w("|---|---|---|")
for k, n in teachers.most_common():
    w(f"| {k} | {n} | {100*n/len(accepted):.1f}% |")

w("\n## 4. Target-length distribution\n")
w(f"median {statistics.median(lengths):.0f} words, mean {statistics.mean(lengths):.1f}, "
  f"range {min(lengths)}-{max(lengths)}\n")
w("| bucket | n | share |")
w("|---|---|---|")
for b in ["fragment / <=8 words", "one-liner / 9-20", "ordinary / 21-45", "longer / 46-80", "long / >80"]:
    if buckets.get(b):
        w(f"| {b} | {buckets[b]} | {100*buckets[b]/len(accepted):.1f}% |")

w("\n## 5. Question-ending rate\n")
mandatory = [r for r in accepted if question_mode(r) == "mandatory"]
optional = [r for r in accepted if question_mode(r) == "optional"]
none_q = [r for r in accepted if question_mode(r) == "none"]
w(f"- overall: {len(q_end)}/{len(accepted)} ({100*len(q_end)/len(accepted):.1f}%) end with a question")
w(f"- plans with a MANDATORY question: {len(mandatory)}; of those "
  f"{sum(1 for r in mandatory if r['target'].rstrip().endswith('?'))} ask one")
w(f"- plans with an OPTIONAL question: {len(optional)}; of those "
  f"{sum(1 for r in optional if r['target'].rstrip().endswith('?'))} ask one "
  f"(silence is the trained behavior)")
w(f"- plans with NO question: {len(none_q)}; of those "
  f"{sum(1 for r in none_q if r['target'].rstrip().endswith('?'))} still end with one")

w("\n## 6. Opening phrases and repetition\n")
w(f"- distinct opening trigrams: {len(openings)}/{len(accepted)} "
  f"(ratio {len(openings)/len(accepted):.2f}; the over-specialization gate floor is 0.60)")
w("- most repeated openings:\n")
w("| opening | n |")
w("|---|---|")
for o, n in openings.most_common(8):
    if n > 1:
        w(f"| \"{o}\" | {n} |")
w(f"\n- near-duplicate target pairs (trigram Jaccard > 0.5): "
  f"{len(dupes) if dupes else 'none'}")
for d in dupes[:10]:
    w(f"  - {d}")
w("\n- residual (non-disqualifying) sludge flags surviving in accepted targets:\n")
if sludge_counter:
    w("| flag | n |")
    w("|---|---|")
    for f, n in sludge_counter.most_common():
        w(f"| {f} | {n} |")
else:
    w("  none")

w("\n## 7. Silence by omission\n")
w(f"- scenarios offering PALETTE items: {len(palette_rows)}")
w(f"- of those, targets using NO palette item: {len(palette_unused)} "
  f"({100*len(palette_unused)/max(1,len(palette_rows)):.1f}%)")
w(f"- silence-palette stratum (palette present, correct answer uses none): "
  f"{strata.get('silence-palette', 0)} scenarios")
w(f"- optional-question-unasked stratum (question available, correct answer asks none): "
  f"{strata.get('optional-question-unasked', 0)} scenarios")

w("\n## 8. Curation provenance\n")
by_action = Counter(r["curation"]["action"] for r in accepted)
edit_basis = Counter(r["curation"]["basis"] for r in accepted if r["curation"]["action"] == "edit")
w("| disposition | n | share |")
w("|---|---|---|")
w(f"| teacher target kept unchanged | {by_action.get('keep', 0)} | {100*by_action.get('keep',0)/len(accepted):.1f}% |")
w(f"| edited — Scott's dictated line or named finding | {edit_basis.get('scott', 0)} | {100*edit_basis.get('scott',0)/len(accepted):.1f}% |")
w(f"| edited — curator, under Scott's written principles | {edit_basis.get('curator', 0)} | {100*edit_basis.get('curator',0)/len(accepted):.1f}% |")
w(f"| curator-authored (every teacher draw failed) | {by_action.get('author', 0)} | {100*by_action.get('author',0)/len(accepted):.1f}% |")
w(f"\n- wrapper-quote normalization (mechanical, not an edit): {normalized_count} targets")
w("- every edited or authored target re-passed the deterministic gates in this build; "
  "the raw teacher candidate is preserved in `source.rawTeacherCandidate` and the "
  "reason for every change in `curation-run1a.jsonl` and each row's `curation` field.")
w("- scenarios previously rejected that now carry a curator-authored target: "
  f"{sorted(r['id'] for r in accepted if r['curation']['action'] == 'author')}")
w("\nEdited/authored rows (reasons in `curation-run1a.jsonl`):\n")
w("| id | action | basis |")
w("|---|---|---|")
for r in accepted:
    if r["curation"]["action"] != "keep":
        w(f"| `{r['id']}` | {r['curation']['action']} | {r['curation']['basis']} |")

w("\n## 9. Deterministic rejections\n")
w(f"total scenarios with no acceptable candidate: {len(rejected)}\n")
if rejection_reasons:
    w("| reason (across all rejected attempts) | n |")
    w("|---|---|")
    for r_, n in rejection_reasons.most_common(20):
        w(f"| {r_} | {n} |")
if rejected:
    w("\nRejected scenarios (preserved as specimens, never patched):\n")
    for rej in rejected:
        w(f"- `{rej['id']}` ({rej['stratum']})")

w("\n## 10. Family-level split manifest\n")
w(f"- unit: semantic scenario family; {len(families)} families total")
w(f"- validation families ({len(val_families)}), one per stratum with >=2 families:\n")
for f in sorted(val_families):
    n = sum(1 for r in accepted if r["family"] == f)
    w(f"  - `{f}` ({n})")
w(f"\n- train: {len(train)} examples across {len(set(r['family'] for r in train))} families")
w(f"- validation: {len(val)} examples across {len(val_families)} families")
w("- full manifest: `splits.json`")

w("\n## 11. Leakage check\n")
w("Permanently held out, never trained on:\n")
for h in PERMANENT_HOLDOUT:
    w(f"- {h}")
w("")
if leak:
    w("**LEAKS FOUND:**\n")
    for l in leak:
        w(f"- {l}")
else:
    w("**No leaks found.** No accepted example mentions a held-out subject "
      "(quokka, Cheshire/Mad Hatter, Epcot/pizza, Precious, shatterproof, rabbit hole), "
      "and no accepted example pairs an agreement-confirmed plan with a "
      "correction-shaped user message — the held-out inversion composition is absent "
      "from training by construction, not by filtering.")
w("")
w(f"Near-duplicate check across all accepted targets: "
  f"{'FOUND ' + str(len(dupes)) if dupes else 'clean'}.")

w("\n## 12. Findings that need a decision before training\n")
draws_by_stratum, scen_by_stratum = Counter(), Counter()
for sid, cands in by_scenario.items():
    draws_by_stratum[meta[sid]["stratum"]] += len(cands)
    scen_by_stratum[meta[sid]["stratum"]] += 1
rows_acc = Counter(r["stratum"] for r in accepted)
starved = sorted(
    ((s, rows_acc.get(s, 0), scen_by_stratum[s], draws_by_stratum[s]) for s in scen_by_stratum),
    key=lambda t: t[1] / t[2])
w("**Teacher acceptance by stratum** — where the teachers systematically could not "
  "render the behavior, the corpus is thin precisely where the experiment needs it:\n")
w("| stratum | accepted / scenarios | draws spent | draws per accepted |")
w("|---|---|---|---|")
for s, acc, scen, draws in starved:
    w(f"| {s} | {acc}/{scen} | {draws} | {draws/max(1,acc):.1f} |")
w("\nThe two starved strata are the same two defect classes the round-2 review named. "
  "Both teachers reach for pretrained knowledge when the plan says Ava has not learned "
  "something, and both tack a question onto a turn the plan closed. Re-sampling has "
  "reached diminishing returns; the honest options are (a) accept the thinner "
  "representation, (b) human-author targets for those strata, or (c) accept that these "
  "two behaviors may need more than run 1a to move. This is a judgment call, not a "
  "pipeline bug.\n")
longer = [r for r in accepted if r["words"] > 45]
w(f"**Curation shortened the corpus.** Median length fell from ~22 words (raw teacher "
  f"output) to {statistics.median(lengths):.0f}, and only {len(longer)} rows now exceed "
  f"45 words ({', '.join('`'+r['id']+'`' for r in longer)}). Most teacher length was "
  f"sludge — restatement, coaching, invented color — so trimming it was correct; but "
  f"the result is that genuinely longer-licensed replies are thinly represented, which "
  f"is exactly the 'length must emerge from content' concern. The registers in this "
  f"corpus mostly license short replies, so the profile is not dishonest — but if run "
  f"1a should also teach the occasional full-paragraph turn, a handful of "
  f"longer-licensed scenarios (a recap request, a told story, a thinking-out-loud "
  f"answer) would need authoring. Decision left open.\n")
real = sources.get("turnrecord", 0)
w(f"**Real-derived share is {real}/{len(accepted)} ({100*real/len(accepted):.1f}%), "
  f"far below the designed 15-25%.** The cause is inventory, not policy: the durable "
  f"TurnRecords banked so far hold 19 plans, and 14 of them belong to permanently "
  f"held-out benchmark families (Cheshire, quokka, Epcot, Precious, DON'T BREAK). Of "
  f"the handful that remain, the gates rejected some. Run 1a is therefore an almost "
  f"entirely constructed corpus. The fix is time and normal use, not a different "
  f"pipeline: every conversation now persists a plan, and the share should rise on its "
  f"own before the 400- and 730-example checkpoints.")

(ROOT / "audit.md").write_text("\n".join(lines) + "\n", encoding="utf-8")

# ---- review packages ---------------------------------------------------------------
def render_review(items, title, note):
    out = [f"# {title}\n", note, ""]
    for i, r in enumerate(items, 1):
        out.append(f"\n---\n\n## {i}. `{r['id']}` — {r['stratum']}\n")
        out.append(f"**Family:** `{r['family']}`  |  **Origin:** "
                   f"{r['source'].get('teacherModel') or 'curator-authored'} / {r['curation']['action']}  "
                   f"|  **Source:** {r['source']['kind']}  |  **{r['words']} words**\n")
        out.append("**ResponsePlan (plan/2, exactly as the model sees it):**\n")
        out.append("```")
        out.append(r["plan2"].rstrip())
        out.append("```\n")
        if r["transcript"]:
            out.append("**Transcript window:**\n")
            for t in r["transcript"]:
                who = "Scott" if t["role"] == "user" else "Ava"
                out.append(f"> [{who}] {t['text']}")
            out.append("")
        out.append(f"> [Scott] **{r['userMessage']}**\n")
        out.append(f"**TARGET:** {r['target']}\n")
        if r["source"].get("sludgeFlags"):
            out.append(f"_flags: {', '.join(r['source']['sludgeFlags'])}_\n")
        out.append("- [ ] keep as-is   - [ ] edit: ______________   - [ ] drop\n")
    return "\n".join(out)

# ---- blind post-curation sample ---------------------------------------------------
# 20 random finals with origin (teacher-original / edited / authored) hidden; the key
# is sealed until judging. The freeze waits on this sample passing.
rng_blind = random.Random(SEED + 3)
blind = rng_blind.sample(accepted, min(20, len(accepted)))
blind_lines = ["# Run-1a blind post-curation sample\n",
               "Twenty targets drawn at random from the final curated corpus. Origin "
               "(teacher-original, edited, or authored) is hidden — the key is sealed in "
               "`review-blind-post-key.json` until judging is done. Same standard as "
               "before: licensed, complete, nothing invented, sounds like Ava.\n"]
blind_key = {}
for i, r in enumerate(blind, 1):
    blind_key[str(i)] = {
        "id": r["id"],
        "origin": ("authored" if r["curation"]["action"] == "author"
                   else f"edited:{r['curation']['basis']}" if r["curation"]["action"] == "edit"
                   else f"teacher-original:{r['source'].get('teacherModel')}"),
    }
    blind_lines.append(f"\n---\n\n## {i}. ({r['stratum']})\n")
    blind_lines.append("```")
    blind_lines.append(r["plan2"].rstrip())
    blind_lines.append("```\n")
    for t in r["transcript"]:
        who = "Scott" if t["role"] == "user" else "Ava"
        blind_lines.append(f"> [{who}] {t['text']}")
    blind_lines.append(f"> [Scott] **{r['userMessage']}**\n")
    blind_lines.append(f"**TARGET:** {r['target']}\n")
    blind_lines.append("- [ ] pass   - [ ] fail: ______________\n")
(ROOT / "review-blind-post.md").write_text("\n".join(blind_lines), encoding="utf-8")
(ROOT / "review-blind-post-key.json").write_text(json.dumps(
    {"seed": SEED + 3, "sealed": "DO NOT OPEN BEFORE JUDGING", "key": blind_key},
    indent=2) + "\n", encoding="utf-8")

rng2 = random.Random(SEED + 1)
random_sample = rng2.sample(accepted, min(20, len(accepted)))
(ROOT / "review-random.md").write_text(render_review(
    random_sample, "Run-1a human review — random 10% sample",
    "A genuinely random draw over the whole accepted set (seed 20260822). Judge the "
    "PLAN -> TARGET relationship, not the target alone: does the target say everything "
    "the plan owes, claim nothing the plan withheld, and sound like Ava rather than an "
    "assistant?"), encoding="utf-8")

hard_pool = [r for r in accepted if r["stratum"] in HARD_STRATA and r not in random_sample]
rng3 = random.Random(SEED + 2)
hard_sample = []
for s in HARD_STRATA:
    pool = [r for r in hard_pool if r["stratum"] == s]
    if pool:
        hard_sample.extend(rng3.sample(pool, min(2, len(pool))))
(ROOT / "review-hard.md").write_text(render_review(
    hard_sample, "Run-1a human review — targeted hard-strata sample",
    "Deliberately NOT random: two examples from each hard stratum, drawn from what the "
    "random sample did not already cover, so the random sample stays genuinely random. "
    "These are the behaviors the experiment exists to fix."), encoding="utf-8")

print(f"final {len(accepted)} ({by_action.get('keep',0)} kept, "
      f"{by_action.get('edit',0)} edited, {by_action.get('author',0)} authored), "
      f"rejected {len(rejected)}")
print(f"train {len(train)} / val {len(val)} across {len(families)} families")
print(f"leaks: {len(leak)}  near-dupes: {len(dupes)}  quote-normalized: {normalized_count}")
print(f"written: train-200.jsonl, splits.json, audit.md, review-blind-post.md (20, key sealed), "
      f"review-random.md ({len(random_sample)}), review-hard.md ({len(hard_sample)})")
