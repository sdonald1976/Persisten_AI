"""Adapts the synthetic-life corpus (artifacts/synthetic-life-*.jsonl) to the pair task — audited.

    python training/supersession/adapt_synthetic_life.py
    # -> training/corpus/memory.supersession.pair.synthlife.jsonl, and the audit it survived

The generator (tools/Companion.Eval/SyntheticLife.cs, merged via dev_automate) labels EVENTS in a
simulated life. This task judges PAIRS. Those mostly coincide and sometimes do not, and the audit
found the divergences concentrated exactly on the class boundaries the model exists to learn, so
every rule below is an exclusion with a stated reason rather than a relabel — the generator's
labels are never edited, only declined.

WHAT THE AUDIT FOUND (2026-08-18, on synthetic-life-phase3-1000.jsonl, 10,000 rows):

* `establish-fact` (831 rows, COEXIST) re-establishes each life's SEEDED initial value regardless
  of current state. 509 rows pair an utterance against an identical stored value — the definition
  of DUPLICATE — and after a supersession has run in that life, the rest silently revert the slot
  to its original value (and leave two active values in the generator's own canonical state).
  Excluded as a family. Recorded as a generator defect, not fixed here.
* Value-identical pairs under non-DUPLICATE labels, throughout: 206/346 of
  `return-to-previous-state` rows "supersede" a value that is already the active one, 181+53+133
  corrections correct a value to itself, 265 refinements refine a value to itself, 346/498
  self-other rows restate the stored value as COEXIST, 63 contradictions contradict nothing.
  A pair whose two values are identical is DUPLICATE by this taxonomy's definition; under any
  other label the row is internally inconsistent and is excluded.
* 1,963 rows have no previousFact (establishments, other-person facts, expiries, hypotheticals) —
  no pair exists, so no pair decision exists. Excluded.
* Every utterance carries notebook/page noise ("(page 830 of my notebook, note 14626.)"), evenly
  across all labels — not label-leaky, kept verbatim (their data is not edited), recorded as a
  realism cost.
* The shipped split files in artifacts/*-splits/ are NOT grouped: 995 of 1,000 lives straddle
  train/validation/test. Not used. Folds here group on lifeId.
* `pass`, `incumbentDecision`, `disagreementCategory`, `canonicalStateBefore/After`, `currentFact.
  active/temporalScope` are generator ground truth or replay results and never enter the rendered
  input. The render uses exactly what production would have: the utterance, the incoming fact, the
  existing fact, slot, cardinality, age bucket.
"""
import collections, json, pathlib, sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from taxonomy import LABELS, is_valid, render  # noqa: E402

for _stream in (sys.stdout, sys.stderr):
    if hasattr(_stream, "reconfigure"):
        _stream.reconfigure(encoding="utf-8", errors="replace")

ROOT = pathlib.Path(__file__).resolve().parents[2]
ARTIFACT = ROOT / "artifacts" / "synthetic-life-phase3-1000.jsonl"
CORPUS = ROOT / "training" / "corpus"

# The generator's fact keys, mapped onto the companion's predicate vocabulary so cardinality — and
# therefore PairStamp's incumbent — behaves as it would in production. Unknown keys stay unknown
# and are treated many-valued, the same safe default PredicateVocabulary uses for legacy predicates.
PREDICATE = {
    "identity.display_name": ("name", True),
    "location.city": ("lives_in", True),
    "occupation.current": ("occupation", True),
    "preference.coffee": ("likes", False),
    "preference.drink.espresso": ("likes", False),
    "preference.drink.breakfast": ("likes", False),
    "preference.drink.oat": ("likes", False),
    "preference.food": ("likes", False),
    "pet.primary": ("has_pet", False),
    "project.active": ("works_on", False),
    "opinion.city": ("belief", False),
}

# The generator schedules in TURNS; the pair schema carries days. Its own distance buckets are the
# honest unit, so they map onto representative day values that land in the same age_bucket the
# renderer would print — a recorded approximation, not a measurement.
AGE_DAYS = {"adjacent": 0, "short-distance": 7, "medium-distance": 45,
            "long-distance": 200, "very-long-distance": 800}

norm = lambda v: " ".join(str(v).lower().split())


def fact_sentence(fact):
    """A canonical fact as the normalized sentence production would hold. The generator stores
    (label, value); production stores "The user's <label> is <value>." — flat and uniform, so no
    family is identifiable from sentence shape alone."""
    return f"The user's {fact['label']} is {fact['value']}."


def main():
    if not ARTIFACT.exists():
        sys.exit(f"no artifact at {ARTIFACT}")
    rows = [json.loads(line) for line in ARTIFACT.open(encoding="utf-8") if line.strip()]

    excluded = collections.Counter()
    made = []
    for r in rows:
        label = r["expectedLabel"]
        if label not in LABELS:
            excluded["unknown-label"] += 1
            continue
        if not r.get("trustedForTraining", False):
            excluded["untrusted-verbalization"] += 1
            continue
        prev, cur = r.get("previousFact"), r.get("currentFact")
        if not prev or not cur:
            excluded["no-pair (no previous fact)"] += 1
            continue
        if r["family"] == "establish-fact":
            excluded["establish-fact (seeded re-establishment)"] += 1
            continue
        if norm(prev["value"]) == norm(cur["value"]) and label != "DUPLICATE":
            excluded[f"value-identical under {label}"] += 1
            continue

        predicate, single = PREDICATE.get(cur["key"], (cur["key"], False))
        days = AGE_DAYS.get(r.get("eventDistanceBucket"), None)
        row = {
            "decision": "memory.supersession.pair",
            "label": label,
            # Metric family: the scenario family, which the audit confirmed is label-pure. Fold
            # grouping is the LIFE, carried separately — one simulated person's timeline shares
            # state and phrasing, and 995 of 1000 lives straddled the shipped splits.
            "family": f"synthlife:{r['family']}",
            "fold_group": r["lifeId"],
            "difficulty": 1 if "multiple-candidate-memories" in (r.get("difficulty") or []) else 0,
            "source": "synthetic_life",
            "generator": r.get("generator", "synthetic-life"),
            "split": "develop",
            "incoming": {"fact": fact_sentence(cur), "value": cur["value"],
                         "predicate": predicate, "utterance": r["utterance"]},
            "existing": {"fact": fact_sentence(prev), "value": prev["value"],
                         "predicate": PREDICATE.get(prev["key"], (prev["key"], False))[0],
                         "age_days": days, "confirmed_days": days},
            "pair": {"same_slot": prev["key"] == cur["key"], "single_valued": single},
            # Provenance for adjudication and error analysis — read by nothing that renders.
            "provenance": {"lifeId": r["lifeId"], "scenarioId": r["scenarioId"],
                           "templateFamilyId": r.get("templateFamilyId"),
                           "eventDistanceBucket": r.get("eventDistanceBucket")},
        }
        row["text"] = render(row)
        made.append(row)

    bad = [x for x in made if not is_valid(x)]
    if bad:
        sys.exit(f"{len(bad)} adapted rows fail the pair-schema invariants")

    CORPUS.mkdir(parents=True, exist_ok=True)
    out = CORPUS / "memory.supersession.pair.synthlife.jsonl"
    with out.open("w", encoding="utf-8") as handle:
        for x in made:
            handle.write(json.dumps(x, ensure_ascii=False) + "\n")

    # The manifest is the authority on what may feed a trained artifact; a new source registers or
    # the trainer refuses it.
    manifest_path = CORPUS / "memory.supersession.pair.manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8")) if manifest_path.exists() else {
        "task": "memory.supersession.pair", "labels": list(LABELS), "sources": {}, "excluded": {}}
    manifest["sources"]["synthetic_life"] = {
        "file": out.name,
        "corpus": "artifacts/synthetic-life-phase3-1000.jsonl (dev_automate, generator "
                  + (made[0]["generator"] if made else "?") + ")",
        "licence": "this repository",
        "role": "gold-synthetic, audited — see adapt_synthetic_life.py header for exclusions",
        "rows": len(made),
        "excluded": dict(excluded),
    }
    manifest_path.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n",
                             encoding="utf-8")

    print(f"{len(made)} pair rows from {len(rows)} events -> {out.name}")
    print("\nexcluded, by reason:")
    for reason, n in excluded.most_common():
        print(f"   {n:>5}  {reason}")
    labels = collections.Counter(x["label"] for x in made)
    lives = len({x["fold_group"] for x in made})
    families = collections.Counter(x["family"] for x in made)
    print(f"\nretained: {lives} lives, {len(families)} scenario families")
    for label in LABELS:
        print(f"   {label:<12} {labels.get(label, 0):>5}")
    print("\nNext: dotnet run --project tools/Companion.Eval -- --only corpus --out training/corpus")
    print("      python training/supersession/train.py --develop synthlife")


if __name__ == "__main__":
    main()
