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
    # CONFIRMED by a --probe run: loads, and carries dtype/id/label/original_label/sentence1/
    # sentence2/triple1/triple2. The triples are what make --audit possible at all.
    "dialogue-nli": dict(
        hf=[("pietrolesci/dialogue_nli", None), ("tasksource/dialogue-nli", None),
            ("xksteven/dialogue_nli", None)],
        split="train", decision="memory.supersession",
        licence="unconfirmed — check wellecks.github.io/dialogue_nli",
        note="(persona, persona) pairs labelled E/N/C. Columns differ by mirror: the author's JSON "
             "uses sentence1/sentence2, the Hub mirrors premise/hypothesis. Both are handled.",
        manual="https://wellecks.github.io/dialogue_nli/ — download, then --from-file the JSON. "
               "This is the reliable route, not the fallback: the Hub copies are script-based."),
    # CONFIRMED by --probe: aps/super_glue/cb loads, columns premise/hypothesis/label/idx.
    "commitment-bank": dict(
        hf=[("aps/super_glue", "cb"), ("super_glue", "cb")],
        split="train", decision="memory.assertion",
        licence="unconfirmed — CB itself is CC-BY per its repo; SuperGLUE packaging differs",
        note="clause-embedding predicates under question/modal/negation/conditional.",
        kwargs=dict(encoding="superglue"),
        manual="https://github.com/mcdm/CommitmentBank — the original release keeps the Likert "
               "ratings; use --from-file with adapters.commitment_bank(encoding='likert')"),
    # CONFIRMED by --probe: clinc/clinc_oos/plus loads, columns text/intent.
    "clinc150": dict(
        hf=[("clinc/clinc_oos", "plus"), ("DeepPavlov/clinc150", None), ("clinc_oos", "plus")],
        split="train", decision="tool.capability",
        licence="unconfirmed — CC BY-SA 3.0 per the UCI listing",
        note="150 intents + out-of-scope. Positives are the assistant-about-itself intents.",
        manual="https://github.com/clinc/oos-eval — data/data_full.json"),
    # The one that does NOT resolve. Both of the obvious ids are script-based and fail outright on
    # datasets >= 4.5, confirmed by --probe. The two below are untried alternatives; if neither
    # works the corpus is a hand download, and it is also the one with the awkward licence, so it
    # is the least costly of the four to go without.
    "daily-dialog": dict(
        hf=[("roskoN/dailydialog", None), ("Akhil391/daily_dialog", None),
            ("li2017dailydialog/daily_dialog", None), ("daily_dialog", None)],
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


def label_of(row, label_names=None):
    """The string label, however this mirror happens to store it.

    `pietrolesci/dialogue_nli` carries both an integer `label` and a string `original_label`; the
    integer column is a plain int64 with no ClassLabel metadata, so nothing can be read off the
    schema and the string column is the only thing that states what an id means.
    """
    if row.get("original_label") not in (None, ""):
        return str(row["original_label"]).strip().lower()
    raw = row.get("label")
    if isinstance(raw, int) and not isinstance(raw, bool):
        if not label_names:
            return f"id:{raw}"
        return str(label_names[raw]).strip().lower()
    return str(raw).strip().lower()


def audit(name, rows, label_names=None):
    """Cross-tabulates DialogueNLI's own relation triples against its own labels.

    This exists because the claim that made DialogueNLI worth borrowing — that it is annotated for
    "can both be true of one person" rather than "do these describe one scene" — was a recollection
    of the annotation scheme rather than a reading of the data.

    THE FIRST VERSION OF THIS FUNCTION WAS WRONG TWICE, and both are worth keeping written down
    because they are the same kind of mistake the rest of this project keeps finding:

      1. It compared the label against the string "neutral" while the mirror stores integers, so
         the count was always zero and it printed "mostly NOT neutral" whatever the data said. A
         verdict that cannot come out the other way is not a measurement. Labels are now decoded
         through `original_label`, which is the column that actually states them.

      2. It bucketed on the RELATION alone, which conflates two unrelated cases. A pair of persona
         sentences sharing the *same triple* is an entailment by construction — that is how the
         corpus makes its positives — and it lands in the same bucket as the case we care about.
         The question is specifically SAME RELATION, DIFFERENT VALUE: "I have a corgi" against
         "I have a cat", one `have_pet` against another. Only that bucket decides whether this
         corpus answers our question or shares MNLI's problem.

    The third bucket, different relations, is the control: the paper labels relation swaps neutral
    by construction, so if it does not come out overwhelmingly neutral, the triples are not being
    read correctly and nothing else here should be believed.
    """
    import collections

    buckets = collections.defaultdict(collections.Counter)
    for r in rows:
        t1, t2 = r.get("triple1"), r.get("triple2")
        if not t1 or not t2 or len(t1) < 3 or len(t2) < 3:
            continue
        label = label_of(r, label_names)
        if t1[1] != t2[1]:
            buckets["different relation (control: should be neutral)"][label] += 1
        elif str(t1[2]).strip().lower() == str(t2[2]).strip().lower():
            buckets["same relation, SAME value"][label] += 1
        else:
            buckets["same relation, DIFFERENT value"][label] += 1

    if not buckets:
        print(f"{name}: no usable triple annotations on these rows, so the label rule cannot be "
              f"audited. The canonical release and pietrolesci/dialogue_nli both carry triple1 and "
              f"triple2; a mirror that dropped them cannot answer this.")
        return

    total = sum(sum(c.values()) for c in buckets.values())
    print(f"{name}: {total} pairs carrying triples\n")
    for bucket in ("same relation, DIFFERENT value", "same relation, SAME value",
                   "different relation (control: should be neutral)"):
        counts = buckets.get(bucket)
        if not counts:
            continue
        n = sum(counts.values())
        print(f"  {bucket:<46} n={n:<8} " +
              "  ".join(f"{k} {v / n:.0%}" for k, v in counts.most_common(4)))

    decisive = buckets.get("same relation, DIFFERENT value")
    if not decisive:
        print("\n  No same-relation/different-value pairs at all, which is itself the answer: the "
              "corpus never asks our question and cannot settle it.")
        return

    n = sum(decisive.values())
    neutral = decisive.get("neutral", 0) / n
    contradiction = decisive.get("contradiction", 0) / n
    unknown = sum(v for k, v in decisive.items() if k.startswith("id:")) / n
    print()
    if unknown > 0.5:
        print("  Labels could not be decoded — no original_label column and no label names. The")
        print("  percentages above are raw ids and the verdict below is withheld rather than")
        print("  guessed, because a wrong mapping here inverts the conclusion entirely.")
    elif neutral >= 0.6:
        print(f"  {neutral:.0%} NEUTRAL. One person may hold both, which is the question memory")
        print("  supersession asks and the reason to prefer this corpus over MNLI. Use it whole.")
    elif contradiction >= 0.6:
        print(f"  {contradiction:.0%} CONTRADICTION. Two different pets read as a conflict, so for")
        print("  supersession this corpus shares MNLI's problem exactly. Train only on the rows")
        print("  whose contradiction comes from an explicitly negating triple (not_have and its")
        print("  kin), and treat same-relation/different-value rows as unusable.")
    else:
        print(f"  Split — {neutral:.0%} neutral against {contradiction:.0%} contradiction. The")
        print("  corpus is inconsistent on exactly the case we need, which is a third answer and")
        print("  the worst one: it cannot be used whole and cannot be filtered by label alone.")


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
            names = list(getattr(getattr(rows, "features", {}).get("label", None), "names", []) or [])
            audit(name, list(rows), label_names=names)
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
