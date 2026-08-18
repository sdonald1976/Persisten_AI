"""Assembles the supersession pair corpus from its sources, with a provenance manifest.

    python training/supersession/assemble.py            # regression gold + captured/reviewed if present
    python training/supersession/assemble.py --weak     # also the DialogueNLI weak-supervision stage

Writes:

    training/corpus/memory.supersession.pair.regression.jsonl   frozen holdout gold
    training/corpus/memory.supersession.pair.weak.jsonl         weak rows (only with --weak)
    training/corpus/memory.supersession.pair.manifest.json      who contributed what, under which licence

THE MANIFEST IS NOT DECORATION. The decision record (docs/SUPERSESSION_TASK.md) requires that we
always know which sources contributed to a trained artifact, because licences differ: DialogueNLI's
is unconfirmed, MSC's is unverified and therefore EXCLUDED from production training entirely, and
the synthetic and captured rows are this repo's own. The trainer refuses rows whose source is not
in the manifest, so a corpus file cannot slip in unaccounted.

THE REGRESSION MAPPING IS BY HAND, ON PURPOSE. The 12 rows in tools/Companion.Eval/datasets carry
binary labels from production incidents; the 7-way labels below are a human re-reading of each
incident, keyed to the exact `said` text. If the eval file gains rows this script does not know,
it fails loudly rather than guessing — new gold is labelled by a person, not by string similarity.
"""
import argparse, collections, json, pathlib, sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from taxonomy import LABELS, is_valid, render  # noqa: E402

for _stream in (sys.stdout, sys.stderr):
    if hasattr(_stream, "reconfigure"):
        _stream.reconfigure(encoding="utf-8", errors="replace")

ROOT = pathlib.Path(__file__).resolve().parents[2]
CORPUS = ROOT / "training" / "corpus"
EVAL_SET = ROOT / "tools" / "Companion.Eval" / "datasets" / "supersession.jsonl"

# said-text -> (label, predicate, single_valued, incoming_value, existing_value).
# The 7-way label is a re-adjudication of each production incident. Two are worth their notes:
# the keto row reads as SUPERSEDES rather than REFINES because the user frames it as a change
# ("Actually ... now"), and the Scott Donald row is REFINES — the binary set called it a
# replacement only because `name` is single-valued and displacement was the available verb.
REGRESSION = {
    "Actually I've gone off black coffee. I take oat milk lattes now.":
        ("SUPERSEDES", "likes", False, "oat milk lattes", "black coffee"),
    "I've started a second thing too - a raised-bed build over at the Marsh Lane plot.":
        ("COEXIST", "works_on", False, "raised-bed build", "greenhouse irrigation rebuild"),
    "I don't like olives either.":
        ("COEXIST", "dislikes", False, "olives", "coriander"),
    "I live in Cambridge now.":
        ("SUPERSEDES", "lives_in", True, "Cambridge", "Norwich"),
    "We got a cat as well, she's called Mim.":
        ("COEXIST", "has_pet", False, "a cat called Mim", "a corgi called Kanga"),
    "Actually I do keto now.":
        ("SUPERSEDES", "diet", True, "keto", "low-carb"),
    "I left the university, I'm at a startup now.":
        ("SUPERSEDES", "employer", True, "a startup", "the university"),
    "Separate thing entirely: I'm rebuilding the greenhouse irrigation.":
        ("COEXIST", "works_on", False, "greenhouse irrigation", "soil chemistry talk"),
    "I've started a second allotment plot at Marsh Lane.":
        ("COEXIST", "works_on", False, "Marsh Lane plot", "Norwich"),
    "I'm on decaf.":
        ("SUPERSEDES", "likes", False, "decaf", "black coffee"),
    "I've taken up piano too.":
        ("COEXIST", "skilled_at", False, "piano", "cello"),
    "It's Scott Donald, formally.":
        ("REFINES", "name", True, "Scott Donald", "Scott"),
}

# Which relations the DialogueNLI audit measured as many-valued (>=80% neutral on kind conflicts).
# Derived from a real run over 81,486 pairs — see docs/SPECIALIST_MODELS.md §"The audit". Only
# these become weak COEXIST rows; single-valued kind conflicts have no utterance and no time
# axis, so for THIS task they are bare incompatible pairs: weak CONTRADICTS.
MANY_VALUED_RELATIONS = frozenset({
    "have_pet", "have_sibling", "have_chidren", "have", "like_activity", "like_general", "not_have",
})


def regression_rows():
    if not EVAL_SET.exists():
        sys.exit(f"regression set missing: {EVAL_SET}")
    rows = []
    for line in EVAL_SET.read_text(encoding="utf-8").splitlines():
        if not line.strip():
            continue
        raw = json.loads(line)
        said = raw["said"]
        if said not in REGRESSION:
            sys.exit(f"regression row not in the hand mapping: {said!r}\n"
                     f"New gold is labelled by a person — add it to REGRESSION in this script "
                     f"with a 7-way label before assembling.")
        label, predicate, single, in_value, ex_value = REGRESSION[said]
        row = {
            "decision": "memory.supersession.pair",
            "label": label,
            "family": f"regression:{said[:50]}",
            "difficulty": 1,
            "source": "regression",
            "generator": "eval-supersession-1",
            # Frozen holdout: these are the production incidents everything is ultimately judged
            # against, and a set the trainer can see is a set the trainer fits.
            "split": "holdout",
            "incoming": {"fact": raw["incoming"], "value": in_value, "predicate": predicate,
                         "utterance": said},
            "existing": {"fact": raw["existing"], "value": ex_value, "predicate": predicate,
                         "age_days": None, "confirmed_days": None},
            "pair": {"same_slot": True, "single_valued": single},
        }
        row["text"] = render(row)
        rows.append(row)
    return rows


def weak_rows(limit):
    """DialogueNLI relabelled through the audited cardinality mapping. Weak, one axis, capped.

    What each bucket may honestly claim for THIS task:
      same triple            -> DUPLICATE   (entailment by construction; two wordings of one fact)
      kind conflict, many-v  -> COEXIST     (the audit's 100%-neutral relations)
      negation-derived pairs -> CONTRADICTS (incompatible, and nothing marks change vs error —
                                             which is exactly what CONTRADICTS means here)
    Everything else is skipped: single-valued kind conflicts are scene-style conflicts between
    different people as often as changes within one, and a weak label that is wrong half the time
    is not weak supervision, it is noise with provenance.

    No utterance exists in this corpus, and none is invented. The renderer leaves the utterance
    slot empty, which is itself honest: the model learns these rows carry no wording signal.
    """
    from datasets import load_dataset
    dataset = load_dataset("pietrolesci/dialogue_nli", split="train")

    made, seen = [], collections.Counter()
    for r in dataset:
        if limit and len(made) >= limit:
            break
        t1, t2 = r.get("triple1"), r.get("triple2")
        if not t1 or not t2 or len(t1) < 3 or len(t2) < 3 or "<none>" in (t1[1], t2[1]):
            continue
        rel1, rel2 = t1[1], t2[1]
        v1, v2 = str(t1[2]).strip().lower(), str(t2[2]).strip().lower()
        label = str(r["original_label"])

        if rel1 == rel2 and v1 == v2 and label == "positive":
            weak = "DUPLICATE"
        elif rel1 == rel2 and v1 != v2 and rel1 in MANY_VALUED_RELATIONS and label == "neutral":
            weak = "COEXIST"
        elif rel1 != rel2 and label == "negative" and (
                rel1.startswith("not_") or rel2.startswith("not_")):
            weak = "CONTRADICTS"
        else:
            continue

        seen[weak] += 1
        row = {
            "decision": "memory.supersession.pair",
            "label": weak,
            "family": f"dnli:{rel1}:{r['sentence1'][:40]}",
            "difficulty": 0,
            "source": "research_corpus_weak",
            "generator": "dialogue-nli-weak-1",
            "split": "develop",
            "incoming": {"fact": r["sentence2"], "value": v2, "predicate": rel2, "utterance": ""},
            "existing": {"fact": r["sentence1"], "value": v1, "predicate": rel1,
                         "age_days": None, "confirmed_days": None},
            "pair": {"same_slot": rel1 == rel2, "single_valued": False},
        }
        row["text"] = render(row)
        made.append(row)
    print(f"   weak stage: {len(made)} rows " +
          " ".join(f"{k}={v}" for k, v in sorted(seen.items())))
    return made


def write(path, rows):
    bad = [r for r in rows if not is_valid(r)]
    if bad:
        sys.exit(f"{path.name}: {len(bad)} invalid rows; first family {bad[0].get('family')}")
    with path.open("w", encoding="utf-8") as out:
        for r in rows:
            out.write(json.dumps(r, ensure_ascii=False) + "\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--weak", action="store_true",
                        help="include the DialogueNLI weak-supervision stage (downloads)")
    parser.add_argument("--weak-limit", type=int, default=8000)
    args = parser.parse_args()

    CORPUS.mkdir(parents=True, exist_ok=True)

    regression = regression_rows()
    write(CORPUS / "memory.supersession.pair.regression.jsonl", regression)
    print(f"   regression: {len(regression)} rows, all split=holdout")

    weak_count = 0
    if args.weak:
        weak = weak_rows(args.weak_limit)
        write(CORPUS / "memory.supersession.pair.weak.jsonl", weak)
        weak_count = len(weak)

    # The manifest: every source that may contribute to a trained artifact, its licence status,
    # and the ones excluded with the reason. The trainer refuses sources not listed here.
    manifest = {
        "task": "memory.supersession.pair",
        "labels": list(LABELS),
        "sources": {
            "synthetic": {"generator": "pair-gen-1", "licence": "this repository",
                          "role": "gold"},
            "regression": {"file": "memory.supersession.pair.regression.jsonl",
                           "licence": "this repository", "role": "gold holdout",
                           "rows": len(regression)},
            "real_conversation": {"file": "memory.supersession.pair.captured.jsonl",
                                  "licence": "the user's own conversations",
                                  "role": "adjudication queue; gold once reviewed"},
            "research_corpus_weak": {
                "file": "memory.supersession.pair.weak.jsonl",
                "corpus": "DialogueNLI (pietrolesci/dialogue_nli)",
                "licence": "UNCONFIRMED — verify at wellecks.github.io/dialogue_nli before "
                           "any trained artifact leaves this machine",
                "role": "weak pretrain only; dropped if it does not improve gold-stage CV",
                "rows": weak_count},
        },
        "excluded": {
            "multi_session_chat": "licence unverified; excluded from production training by "
                                  "decision (docs/SUPERSESSION_TASK.md). May be evaluated in "
                                  "isolation; must not contribute to a distributable artifact.",
        },
    }
    (CORPUS / "memory.supersession.pair.manifest.json").write_text(
        json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"   manifest -> memory.supersession.pair.manifest.json")
    print()
    print("Next: dotnet run --project tools/Companion.Eval -- --only corpus --out training/corpus")
    print("      python training/supersession/train.py")


if __name__ == "__main__":
    main()
