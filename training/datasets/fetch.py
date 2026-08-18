"""Downloads the borrowed corpora and writes them in this repo's training-row shape.

    pip install datasets
    python training/datasets/fetch.py --list
    python training/datasets/fetch.py dialogue-nli commitment-bank
    python training/datasets/fetch.py --all --limit 20000

Output goes to training/corpus/<decision>.borrowed.jsonl, alongside the generated
<decision>.{train,validation,test}.jsonl and the harvested <decision>.captured.jsonl. Same shape,
same family field, so crossval.py reads them without knowing where a row came from.

UNVERIFIED. The mapping in adapters.py is tested offline; the downloads below are not, because the
session that wrote this had no route to Hugging Face. The dataset ids and column names come from
published descriptions. `require()` in adapters.py turns a schema surprise into an error naming the
columns it actually found, rather than a corpus that is quietly all-negative — so the first run of
this is expected to need a fix, and is designed to tell you which one.

LICENCES ARE NOT ALL PERMISSIVE. DailyDialog is CC BY-NC-SA 4.0: non-commercial, and ShareAlike
propagates to derivatives, which for a fine-tuned model plausibly means the weights. The others were
not confirmed. Check each before anything trained on it leaves your machine — this repo already
keeps a licence column for its models and the same discipline applies to data.
"""
import argparse, json, pathlib, sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import adapters  # noqa: E402

CORPUS = pathlib.Path(__file__).resolve().parents[1] / "corpus"

# id, config, split, the decision it feeds, and how to map it. Kept as data so adding a corpus is a
# row rather than a function, and so --list can print the register without importing `datasets`.
SOURCES = {
    "dialogue-nli": dict(
        hf="xksteven/dialogue_nli", config=None, split="train",
        decision="memory.supersession", licence="unconfirmed — check wellecks.github.io/dialogue_nli",
        note="(persona, persona) pairs labelled E/N/C. The Phase 4 failure cases ARE its subject."),
    "commitment-bank": dict(
        hf="super_glue", config="cb", split="train",
        decision="memory.assertion", licence="unconfirmed — CB is CC-BY per its repo; SuperGLUE packaging differs",
        note="clause-embedding predicates under question/modal/negation/conditional.",
        kwargs=dict(encoding="superglue")),
    "clinc150": dict(
        hf="clinc_oos", config="plus", split="train",
        decision="tool.capability", licence="unconfirmed — CC BY-SA 3.0 per the UCI listing",
        note="150 intents + out-of-scope. Positives are the assistant-about-itself intents."),
    "daily-dialog": dict(
        hf="li2017dailydialog/daily_dialog", config=None, split="train",
        decision="companion.commitment", licence="CC BY-NC-SA 4.0 — NON-COMMERCIAL, ShareAlike",
        note="commissive acts. The DETECTION half only; the capability gate stays code."),
}


def show():
    print(f"{'name':<17} {'decision':<22} licence")
    for name, s in SOURCES.items():
        print(f"{name:<17} {s['decision']:<22} {s['licence']}")
        print(f"{'':<17} {s['hf']}{'/' + s['config'] if s['config'] else ''} — {s['note']}")
    print()
    print("None of these downloads have been run. See the module docstring.")


def load(name, limit):
    from datasets import load_dataset
    spec = SOURCES[name]
    data = load_dataset(spec["hf"], spec["config"]) if spec["config"] else load_dataset(spec["hf"])
    rows = data[spec["split"]]
    if limit:
        rows = rows.select(range(min(limit, len(rows))))

    kwargs = dict(spec.get("kwargs", {}))

    # CLINC ships intents as label ids; the adapter needs the names to match against, and passing
    # ids straight through would silently label everything negative.
    if name == "clinc150":
        feature = rows.features.get("intent")
        kwargs["label_names"] = list(getattr(feature, "names", []) or [])

    mapped = adapters.ADAPTERS[name](list(rows), **kwargs)

    # A corpus with no positives is the failure this is most likely to produce and the least likely
    # to be noticed: it trains a model that says no to everything and reports excellent accuracy.
    positives = sum(r["label"] for r in mapped)
    if positives == 0:
        raise SystemExit(
            f"{name}: mapped {len(mapped)} rows and NONE are positive. That is almost certainly a "
            f"mapping problem rather than a dataset with no positive examples — check the label "
            f"column and, for clinc150, whether any of {sorted(adapters.CAPABILITY_CORE)} appear "
            f"in the intent names.")
    return mapped, positives


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("names", nargs="*", metavar="CORPUS", help=f"one or more of: {', '.join(SOURCES)}")
    parser.add_argument("--all", action="store_true")
    parser.add_argument("--list", action="store_true")
    parser.add_argument("--limit", type=int, default=0, help="cap rows per corpus; 0 for all")
    args = parser.parse_args()

    if args.list or (not args.names and not args.all):
        show()
        return

    unknown = [n for n in args.names if n not in SOURCES]
    if unknown:
        raise SystemExit(f"unknown corpus {unknown}; known: {', '.join(SOURCES)}")

    CORPUS.mkdir(parents=True, exist_ok=True)
    wanted = list(SOURCES) if args.all else args.names
    print(f"{'corpus':<17} {'rows':>8} {'positive':>9}  ->")
    for name in wanted:
        rows, positives = load(name, args.limit)
        path = CORPUS / f"{SOURCES[name]['decision']}.borrowed.jsonl"
        with path.open("w", encoding="utf-8") as out:
            for row in rows:
                out.write(json.dumps(row, ensure_ascii=False) + "\n")
        families = len({r["family"] for r in rows})
        print(f"{name:<17} {len(rows):>8} {positives / len(rows):>8.0%}  {path.name} "
              f"({families} families)")

    print()
    print("Families, not rows, are what the metric counts. A corpus of 300k rows over 200 families")
    print("is 200 observations — see the retraction in training/README.md.")


if __name__ == "__main__":
    main()
