"""Downloads the borrowed corpora and writes them in this repo's training-row shape.

    pip install datasets
    python training/datasets/fetch.py --list           # the register: what feeds what, and licences
    python training/datasets/fetch.py --probe          # try every download, report what works
    python training/datasets/fetch.py dialogue-nli
    python training/datasets/fetch.py --all --limit 20000

Output goes to training/corpus/<decision>.borrowed.jsonl, alongside the generated
<decision>.{train,validation,test}.jsonl and the harvested <decision>.captured.jsonl. Same shape,
same family field, so crossval.py reads them without knowing where a row came from.

--------------------------------------------------------------------------------------------
THE DOWNLOADS ARE THE UNVERIFIED PART

The mapping in adapters.py is tested offline. The fetching is not: the session that wrote this had
no route to Hugging Face, and the first attempt to run it proved the point twice over — a missing
dependency reported *after* the results header had already printed, and dataset ids that no longer
resolve, because `datasets` 4.x removed loading scripts entirely and several of these corpora were
script-based.

So this now behaves like something that expects to be wrong:

  * every source has a LIST of candidate repository ids, tried in order, and the one that worked is
    reported. Mirrors on the Hub churn; a single hard-coded id is a script with a shelf life.
  * `--probe` tries them all, in streaming mode, and prints the columns each actually has. One run
    tells you everything that is broken instead of one error per run.
  * `--from-file` skips the Hub entirely for a corpus you downloaded by hand, which matters most for
    DialogueNLI: it is the highest-value corpus here and has the least certain mirror, and its
    canonical distribution is a JSON download from the author's site.

LICENCES ARE NOT ALL PERMISSIVE. DailyDialog is CC BY-NC-SA 4.0: non-commercial, and ShareAlike
propagates to derivatives, which for a fine-tuned model plausibly means the weights. The others are
unconfirmed. Check each before anything trained on it leaves your machine — this repo already keeps
a licence column for its models and the same discipline applies to data.
"""
import argparse, json, pathlib, sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import adapters  # noqa: E402

CORPUS = pathlib.Path(__file__).resolve().parents[1] / "corpus"

# `hf` is a list of (repo, config) candidates in preference order. The parquet-backed mirrors come
# first: `datasets` >= 4.0 dropped trust_remote_code and >= 4.5 dropped loading scripts, so the
# original ids for super_glue and clinc_oos are dead on any recent install.
SOURCES = {
    # Order is evidence, not preference. `xksteven/dialogue_nli` is KNOWN to fail on any modern
    # install: it is script-based, its Hub viewer is disabled for that reason, and because the
    # viewer never ran there is no auto-converted parquet branch to fall back to either. It is kept
    # last only so the error names it. `pietrolesci/dialogue_nli` is what the tasksource collection
    # loads programmatically, which is decent evidence it is script-free.
    "dialogue-nli": dict(
        hf=[("pietrolesci/dialogue_nli", None), ("tasksource/dialogue-nli", None),
            ("xksteven/dialogue_nli", None)],
        split="train", decision="memory.supersession",
        licence="unconfirmed — check wellecks.github.io/dialogue_nli",
        note="(persona, persona) pairs labelled E/N/C. Columns differ by mirror: the author's JSON "
             "uses sentence1/sentence2, the Hub mirrors premise/hypothesis. Both are handled.",
        manual="https://wellecks.github.io/dialogue_nli/ — download, then --from-file the JSON. "
               "This is the reliable route, not the fallback: the Hub copies are script-based."),
    "commitment-bank": dict(
        hf=[("aps/super_glue", "cb"), ("super_glue", "cb")],
        split="train", decision="memory.assertion",
        licence="unconfirmed — CB itself is CC-BY per its repo; SuperGLUE packaging differs",
        note="clause-embedding predicates under question/modal/negation/conditional.",
        kwargs=dict(encoding="superglue"),
        manual="https://github.com/mcdm/CommitmentBank — the original release keeps the Likert "
               "ratings; use --from-file with adapters.commitment_bank(encoding='likert')"),
    "clinc150": dict(
        hf=[("clinc/clinc_oos", "plus"), ("DeepPavlov/clinc150", None), ("clinc_oos", "plus")],
        split="train", decision="tool.capability",
        licence="unconfirmed — CC BY-SA 3.0 per the UCI listing",
        note="150 intents + out-of-scope. Positives are the assistant-about-itself intents.",
        manual="https://github.com/clinc/oos-eval — data/data_full.json"),
    "daily-dialog": dict(
        hf=[("li2017dailydialog/daily_dialog", None), ("daily_dialog", None)],
        split="train", decision="companion.commitment",
        licence="CC BY-NC-SA 4.0 — NON-COMMERCIAL, ShareAlike",
        note="commissive acts. The DETECTION half only; the capability gate stays code.",
        manual="http://yanran.li/dailydialog"),
}


def need_datasets():
    """Checked before anything else prints. The first run reported this as a traceback under a
    results header that had already been written, which reads as "the download failed" rather than
    "you have not installed the library"."""
    try:
        import datasets  # noqa: F401
        return
    except ModuleNotFoundError:
        sys.exit(
            "The `datasets` library is not installed, so nothing can be downloaded.\n\n"
            "    pip install datasets\n"
            "    # or the whole specialist-model stack:\n"
            "    pip install -r training/requirements-encoder.txt\n\n"
            "`--list` works without it and prints the register.")


def show():
    print(f"{'name':<17} {'decision':<22} licence")
    for name, s in SOURCES.items():
        print(f"{name:<17} {s['decision']:<22} {s['licence']}")
        ids = ", ".join(f"{r}{'/' + c if c else ''}" for r, c in s["hf"])
        print(f"{'':<17} {ids}")
        print(f"{'':<17} {s['note']}")
    print()
    print("Repository ids are candidates tried in order — Hub mirrors churn, and `datasets` 4.x")
    print("dropped the loading scripts several of these originally shipped with.")
    print("Run --probe to find out which of them actually resolve from here.")


def try_load(repo, config, split, streaming=False):
    from datasets import load_dataset
    args = (repo, config) if config else (repo,)
    return load_dataset(*args, split=split, streaming=streaming)


def resolve(name, limit, streaming=False):
    """The first candidate id that loads, with every failure kept for the error message."""
    spec = SOURCES[name]
    split = spec["split"] if (streaming or not limit) else f"{spec['split']}[:{limit}]"
    problems = []
    for repo, config in spec["hf"]:
        try:
            return try_load(repo, config, split, streaming), f"{repo}{'/' + config if config else ''}"
        except Exception as e:                                  # noqa: BLE001 — any failure is a candidate failure
            problems.append(f"    {repo}{'/' + config if config else ''}: {type(e).__name__}: {e}")
    raise SystemExit(
        f"{name}: none of the candidate repositories loaded.\n" + "\n".join(problems) +
        f"\n\n  Search the Hub for a current mirror and add it to SOURCES['{name}']['hf'], or "
        f"download it by hand:\n    {spec['manual']}\n"
        f"  then: python fetch.py {name} --from-file <path>")


def probe():
    print("trying each candidate in streaming mode — no files are written\n")
    for name, spec in SOURCES.items():
        print(f"{name}")
        found = False
        for repo, config in spec["hf"]:
            label = f"{repo}{'/' + config if config else ''}"
            try:
                stream = try_load(repo, config, spec["split"], streaming=True)
                first = next(iter(stream))
                print(f"   OK   {label}")
                print(f"        columns: {sorted(first.keys())}")
                found = True
                break
            except Exception as e:                              # noqa: BLE001
                print(f"   fail {label}: {type(e).__name__}: {str(e)[:110]}")
        if not found:
            print(f"        none resolved. By hand: {spec['manual']}")
        print()
    print("A column list that does not match what adapters.py expects is the OTHER likely failure,")
    print("and it raises with both lists rather than writing an all-negative corpus.")


def audit(name, rows):
    """Cross-tabulates DialogueNLI's own relation triples against its labels.

    This exists because the claim that made DialogueNLI worth borrowing is not fully verified. Its
    labels come from human-annotated triples — (i, have_pet, dog) — with contradiction assigned via
    an explicitly negating triple such as (i, not_have, dog), and pairs across DIFFERENT relations
    labelled neutral by rule. That is person-level coherence rather than scene identity, which is
    the right question and the one an off-the-shelf MNLI model was measured getting wrong.

    What is NOT confirmed is the case the whole argument rests on: SAME relation, DIFFERENT value.
    "I have a corgi" against "I have a cat" is one `have_pet` triple against another, and whether
    that reads as neutral (a person may have both — what memory needs) or as contradiction (which
    would make this corpus no better than MNLI for supersession) depends on whether `have_pet` is
    treated as many-valued. The paper's rules as published do not settle it.

    Five minutes of arithmetic over the real file does. If same-relation pairs are overwhelmingly
    neutral, the corpus answers our question. If they are largely contradiction, it does not, and
    the sensible move is to train only on the negation-derived rows.
    """
    triples = [(r.get("triple1"), r.get("triple2"), str(r.get("label", "")).lower())
               for r in rows if r.get("triple1") and r.get("triple2")]
    if not triples:
        print(f"{name}: no triple annotations on these rows, so the label rule cannot be audited "
              f"from here. The canonical release at wellecks.github.io carries them; a mirror may "
              f"have dropped the columns.")
        return

    import collections
    same = collections.Counter()
    cross = collections.Counter()
    for t1, t2, label in triples:
        r1 = t1[1] if len(t1) > 1 else "?"
        r2 = t2[1] if len(t2) > 1 else "?"
        (same if r1 == r2 else cross)[label] += 1

    def rate(counter, what):
        total = sum(counter.values()) or 1
        print(f"  {what:<34} n={total:<7} " + "  ".join(
            f"{k} {v / total:.0%}" for k, v in counter.most_common()))

    print(f"{name}: how the corpus labels {len(triples)} pairs that carry triples")
    rate(same, "SAME relation, e.g. pet vs pet")
    rate(cross, "different relations")
    print()
    neutral = same.get("neutral", 0) / (sum(same.values()) or 1)
    if neutral >= 0.6:
        print("  Same-relation pairs are mostly NEUTRAL — one person may hold both. That is the")
        print("  question memory supersession asks, and the reason to prefer this over MNLI.")
    else:
        print("  Same-relation pairs are mostly NOT neutral. This corpus then shares MNLI's problem")
        print("  for our purposes: two different pets read as a conflict. Train on the")
        print("  negation-derived rows only, and treat the rest as unusable for supersession.")


def rows_from_file(name, path):
    """A corpus downloaded by hand. Accepts JSONL or a JSON list — both are what these ship as."""
    text = pathlib.Path(path).read_text(encoding="utf-8")
    stripped = text.lstrip()
    if stripped.startswith("["):
        return json.loads(text)
    return [json.loads(line) for line in text.splitlines() if line.strip()]


def build(name, rows):
    spec = SOURCES[name]
    kwargs = dict(spec.get("kwargs", {}))

    # Both of these ship their labels as integer ids, and neither adapter will guess what an id
    # means — so the names are read off the dataset's own feature schema, which is the one place
    # they are stated rather than assumed.
    if hasattr(rows, "features"):
        column = {"clinc150": "intent", "dialogue-nli": "label"}.get(name)
        if column:
            names = list(getattr(rows.features.get(column), "names", []) or [])
            if names:
                kwargs["label_names"] = names

    mapped = adapters.ADAPTERS[name](list(rows), **kwargs)

    # A corpus with no positives is the failure this is most likely to produce and the least likely
    # to be noticed: it trains a model that says no to everything and reports excellent accuracy.
    positives = sum(r["label"] for r in mapped)
    if positives == 0:
        raise SystemExit(
            f"{name}: mapped {len(mapped)} rows and NONE are positive. That is almost certainly a "
            f"mapping problem rather than a dataset with no positive examples — check the label "
            f"column, and for clinc150 whether any of {sorted(adapters.CAPABILITY_CORE)} appear in "
            f"the intent names.")
    return mapped, positives


def write(name, mapped, positives, source_label):
    path = CORPUS / f"{SOURCES[name]['decision']}.borrowed.jsonl"
    CORPUS.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8") as out:
        for row in mapped:
            out.write(json.dumps(row, ensure_ascii=False) + "\n")
    families = len({r["family"] for r in mapped})
    print(f"{name:<17} {len(mapped):>8} {positives / len(mapped):>8.0%}  {path.name} "
          f"({families} families)  from {source_label}")


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("names", nargs="*", metavar="CORPUS",
                        help=f"one or more of: {', '.join(SOURCES)}")
    parser.add_argument("--all", action="store_true")
    parser.add_argument("--list", action="store_true")
    parser.add_argument("--probe", action="store_true", help="try every download, write nothing")
    parser.add_argument("--audit", action="store_true",
                        help="dialogue-nli: cross-tabulate its relation triples against its labels, "
                             "which settles whether it answers the question we need")
    parser.add_argument("--from-file", metavar="PATH",
                        help="map a corpus downloaded by hand instead of using the Hub")
    parser.add_argument("--limit", type=int, default=0, help="cap rows per corpus; 0 for all")
    args = parser.parse_args()

    if args.list or (not args.names and not args.all and not args.probe):
        show()
        return

    unknown = [n for n in args.names if n not in SOURCES]
    if unknown:
        raise SystemExit(f"unknown corpus {unknown}; known: {', '.join(SOURCES)}")

    if args.from_file:
        if len(args.names) != 1:
            raise SystemExit("--from-file takes exactly one corpus name")
        name = args.names[0]
        raw = rows_from_file(name, args.from_file)
        if args.audit:
            audit(name, raw)
            return
        mapped, positives = build(name, raw)
        print(f"{'corpus':<17} {'rows':>8} {'positive':>9}  ->")
        write(name, mapped, positives, args.from_file)
        return

    need_datasets()

    if args.probe:
        probe()
        return

    wanted = list(SOURCES) if args.all else args.names

    if args.audit:
        for name in wanted:
            rows, source_label = resolve(name, args.limit)
            audit(name, list(rows))
        return

    results = []
    for name in wanted:
        rows, source_label = resolve(name, args.limit)
        results.append((name, *build(name, rows), source_label))

    # Printed only once something has actually loaded. A header above a traceback reads as a failed
    # download rather than as a missing library, which is how the first run of this was misread.
    print(f"{'corpus':<17} {'rows':>8} {'positive':>9}  ->")
    for name, mapped, positives, source_label in results:
        write(name, mapped, positives, source_label)

    print()
    print("Families, not rows, are what the metric counts. A corpus of 300k rows over 200 families")
    print("is 200 observations — see the retraction in training/README.md.")
    print("Next: python training/cognition/crossval.py")


if __name__ == "__main__":
    main()
