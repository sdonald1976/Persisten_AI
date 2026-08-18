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

# Windows consoles default to cp1252, and these scripts (and torch's own exporter) print em-dashes
# and the odd emoji. Encoding is not cosmetic here: on the first real run torch.onnx.export
# captured the graph successfully and then died with UnicodeEncodeError writing its own success
# message, which reads exactly like a failed export. Reconfigured rather than left to the caller
# to set PYTHONIOENCODING, because the failure names the wrong culprit.
for _stream in (sys.stdout, sys.stderr):
    if hasattr(_stream, "reconfigure"):
        _stream.reconfigure(encoding="utf-8", errors="replace")

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


# The corpus marks an unannotated sentence with this rather than omitting the triple, so it arrives
# looking like a relation named "<none>". Counting it as a relation puts every unannotated row into
# the different-relation bucket and dilutes the one control this audit has.
NONE_TRIPLE = "<none>"

# Relations that are each other's negation. A contradiction between these is manufactured by the
# corpus rather than judged, so they are separated from the control instead of counted against it:
# the paper's rule is that a relation SWAP is neutral, and it means unrelated relations, not a
# relation against its own negation. Held as a stated assumption because it is one — the audit
# prints the antonym bucket's own label split, which is the number that would falsify it. If those
# pairs are not overwhelmingly contradiction, this list is wrong and the control below is polluted.
LIKING = ("like", "favorite")


def is_antonym_pair(first, second):
    if first.startswith("not_") or second.startswith("not_"):
        return True
    return ((first.startswith("dislike") and second.startswith(LIKING))
            or (second.startswith("dislike") and first.startswith(LIKING)))


def leading_count(value):
    """Splits "2 dog" into ("2", "dog"), and leaves "dog" as (None, "dog").

    DialogueNLI writes quantities into the value, so "I have two dogs" against "I have five dogs"
    is a same-relation/different-value pair exactly like "a dog" against "a cat" — and it is a
    different question. One is arithmetic, the other is the question memory asks. Reporting them
    together is how the decisive bucket came out 96 % contradiction while the case we care about
    was 100 % neutral underneath it.
    """
    head, _, rest = str(value).strip().partition(" ")
    if head.isdigit() and rest.strip():
        return head, rest.strip().lower()
    return None, str(value).strip().lower()


def conflict_kind(first, second):
    """"count" when two values differ only in a leading number, "kind" otherwise."""
    number1, base1 = leading_count(first)
    number2, base2 = leading_count(second)
    return "count" if base1 == base2 and number1 != number2 else "kind"


CONTROL = "different relation, unrelated (control: neutral by rule)"
ANTONYM = "different relation, negated (contradiction by construction)"
SAME_VALUE = "same relation, same value (entailment by construction)"
KIND = "same relation, different KIND of value"
COUNT = "same relation, different COUNT of one value"


def bucket_of(triple1, triple2):
    """Which question a pair of triples is asking, or None when it cannot be read.

    Returns (bucket, relation). The relation is carried because the answer to this audit turned out
    to live entirely inside the per-relation breakdown: aggregated, the decisive bucket is 96 %
    contradiction; split by relation it is nineteen relations at 0-2 % neutral and seven at 100 %,
    with nothing in between. That is not a corpus being inconsistent, it is a corpus encoding
    cardinality — and an aggregate hides it completely.
    """
    if not triple1 or not triple2 or len(triple1) < 3 or len(triple2) < 3:
        return None
    relation1, relation2 = str(triple1[1]), str(triple2[1])
    if relation1 == NONE_TRIPLE or relation2 == NONE_TRIPLE:
        return None
    if relation1 != relation2:
        return (ANTONYM if is_antonym_pair(relation1, relation2) else CONTROL), None
    value1, value2 = str(triple1[2]).strip().lower(), str(triple2[2]).strip().lower()
    if value1 == value2:
        return SAME_VALUE, relation1
    return (COUNT if conflict_kind(value1, value2) == "count" else KIND), relation1


CANONICAL = ("entailment", "neutral", "contradiction")


def derive_label_names(buckets, dominant=0.8):
    """Works out which of this mirror's label strings means what, from the corpus's construction.

    NOT from the strings themselves. `pietrolesci/dialogue_nli` calls them positive/neutral/negative
    while the canonical release says entailment/neutral/contradiction, and the previous version of
    this audit compared against the literal string "contradiction", counted zero every time, and
    printed a verdict that could not come out the other way. Hardcoding the other vocabulary would
    only move the same bug.

    So it is derived from two anchors the paper states and this file can check:

      * a pair of persona sentences sharing a triple is an ENTAILMENT by construction — that is how
        the corpus makes its positives;
      * a pair across two unrelated relations is NEUTRAL by rule.

    Whatever label dominates each of those names that class, and the third is contradiction by
    elimination. Both anchors must be overwhelming and they must disagree with each other, or this
    returns None and the caller withholds the verdict rather than guessing — which is the rule this
    repo has already paid for elsewhere: SuperGLUE's 0 meaning entailment, read as a Likert 0.0
    meaning undecided, inverted the labels on the rows that mattered most.

    A mirror that already uses the canonical names must AGREE with the derivation. If it does not,
    something is being read wrong, and that is a failure rather than a preference.
    """
    def dominant_label(bucket):
        counts = buckets.get(bucket)
        if not counts:
            return None
        label, count = counts.most_common(1)[0]
        return label if count / sum(counts.values()) >= dominant else None

    entailment, neutral = dominant_label(SAME_VALUE), dominant_label(CONTROL)
    if not entailment or not neutral or entailment == neutral:
        return None

    observed = {label for counts in buckets.values() for label in counts}
    remaining = observed - {entailment, neutral}
    if len(remaining) != 1:
        return None
    contradiction = remaining.pop()

    derived = {entailment: "entailment", neutral: "neutral", contradiction: "contradiction"}
    if any(raw in CANONICAL and canonical != raw for raw, canonical in derived.items()):
        return None
    return derived


def audit(name, rows, label_names=None):
    """Cross-tabulates DialogueNLI's own relation triples against its own labels.

    This exists because the claim that made DialogueNLI worth borrowing — that it is annotated for
    "can both be true of one person" rather than "do these describe one scene" — was a recollection
    of the annotation scheme rather than a reading of the data.

    IT HAS NOW BEEN WRONG THREE TIMES, and every one is the same kind of mistake:

      1. It compared the label against the string "neutral" while the mirror stores integers, so the
         count was always zero and it printed "mostly NOT neutral" whatever the data said.
      2. It bucketed on the RELATION alone, so same-triple pairs — entailments by construction —
         landed in the same bucket as the case in question.
      3. It then compared against the string "contradiction" while this mirror says "negative", so
         the verdict was again computed from a count that was structurally zero, and it announced
         the worst of the three possible answers about a corpus that gives the best one. Labels are
         now DERIVED from the corpus's construction (see derive_label_names) rather than named.

    All three shipped believing they had measured something, and what they had in common was a
    number that could only come out one way. That is the thing to check first in anything here.

    The fourth reading is the one that answers the question, and it needed one more split: values
    differing by a leading COUNT ("2 dog" against "5 dog") are arithmetic rather than cardinality,
    and they are an eighth of the decisive bucket at 96 % contradiction. Separating them is what
    turned an apparently inconsistent corpus into a completely regular one.
    """
    import collections

    buckets = collections.defaultdict(collections.Counter)
    by_relation = collections.defaultdict(collections.Counter)
    dropped = 0
    for row in rows:
        placed = bucket_of(row.get("triple1"), row.get("triple2"))
        if placed is None:
            dropped += 1
            continue
        bucket, relation = placed
        label = label_of(row, label_names)
        buckets[bucket][label] += 1
        if bucket == KIND:
            by_relation[relation][label] += 1

    if not buckets:
        print(f"{name}: no usable triple annotations on these rows, so the label rule cannot be "
              f"audited. The canonical release and pietrolesci/dialogue_nli both carry triple1 and "
              f"triple2; a mirror that dropped them cannot answer this.")
        return

    total = sum(sum(counts.values()) for counts in buckets.values())
    print(f"{name}: {total} pairs with both triples annotated "
          f"({dropped} dropped: no triple, or one side unannotated)\n")

    names = derive_label_names(buckets)
    if names is None:
        print("  LABELS COULD NOT BE DERIVED. The two anchors this reads them from — same-triple")
        print("  pairs are entailment by construction, unrelated-relation pairs are neutral by")
        print("  rule — did not come out cleanly, so which class is which is unknown and every")
        print("  verdict below is withheld rather than guessed. Counts are printed raw.\n")
    else:
        print("  label vocabulary, derived from the corpus's construction, not from its spelling:")
        for raw, canonical in sorted(names.items(), key=lambda pair: CANONICAL.index(pair[1])):
            print(f"     {raw:<14} = {canonical}")
        print()

    def share(counts, want):
        """The fraction of one bucket carrying a canonical class, or None if labels are unknown."""
        if names is None:
            return None
        n = sum(counts.values())
        return sum(v for k, v in counts.items() if names.get(k) == want) / n if n else 0.0

    for bucket in (KIND, COUNT, SAME_VALUE, CONTROL, ANTONYM):
        counts = buckets.get(bucket)
        if not counts:
            continue
        n = sum(counts.values())
        rendered = "  ".join(f"{names[k] if names else k} {v / n:.0%}"
                             for k, v in counts.most_common(3))
        print(f"  {bucket:<50} n={n:<8} {rendered}")

    # The control and the antonym split are what say whether anything else here can be believed.
    control = buckets.get(CONTROL)
    if names and control and share(control, "neutral") < 0.9:
        print("\n  THE CONTROL FAILED. Unrelated relations are neutral by the paper's rule and here")
        print("  they are not, so the triples are not being read as intended and nothing above")
        print("  should be believed.")
        return
    antonyms = buckets.get(ANTONYM)
    if names and antonyms and share(antonyms, "contradiction") < 0.6:
        print("\n  The negated-relation bucket is not mostly contradiction, so the antonym rule in")
        print("  this file is wrong and the control it was separated from is polluted. Fix")
        print("  is_antonym_pair before reading the verdict.")
        return

    if not by_relation:
        print("\n  No same-relation/different-kind pairs at all, which is itself the answer: the")
        print("  corpus never asks our question and cannot settle it.")
        return
    if names is None:
        return

    print("\n  THE DECISIVE BUCKET, PER RELATION — same relation, different KIND of value")
    print("  (\"I have a corgi\" against \"I have a cat\"). Relations with at least 100 pairs:\n")
    ranked = [(relation, counts) for relation, counts in by_relation.items()
              if sum(counts.values()) >= 100]
    ranked.sort(key=lambda pair: share(pair[1], "neutral"))
    for relation, counts in ranked:
        print(f"     {relation:<28} n={sum(counts.values()):<7} "
              f"neutral {share(counts, 'neutral'):>4.0%}  "
              f"contradiction {share(counts, 'contradiction'):>4.0%}")

    if not ranked:
        print("     (no relation reaches 100 pairs, so it cannot be read per relation)")
        return

    shares = [share(counts, "neutral") for _, counts in ranked]
    many = [relation for (relation, _), s in zip(ranked, shares) if s >= 0.8]
    single = [relation for (relation, _), s in zip(ranked, shares) if s <= 0.2]
    middle = len(shares) - len(many) - len(single)

    aggregate = collections.Counter()
    for counts in by_relation.values():
        aggregate.update(counts)
    neutral = share(aggregate, "neutral")

    print()
    if middle == 0 and many and single:
        print(f"  BIMODAL, CLEANLY: {len(single)} relations treat a different value as a "
              f"contradiction and {len(many)}")
        print("  treat it as neutral, with NONE in between. That is not an inconsistent corpus, it")
        print("  is a corpus encoding RELATION CARDINALITY — the same axis PredicateVocabulary")
        print("  already encodes, and the axis MNLI was measured getting wrong. Usable whole.")
        print()
        print(f"  Read the aggregate ({neutral:.0%} neutral) as meaningless: it is dominated by "
              f"whichever")
        print("  relation happens to be largest. The per-relation split is the finding.")
        print()
        print("  many-valued here:   " + ", ".join(sorted(many)))
        print("  single-valued here: " + ", ".join(sorted(single)))
        print()
        print("  Worth diffing against PredicateVocabulary before trusting a fine-tune on this: a")
        print("  relation this corpus calls single-valued and the companion calls multi-valued is a")
        print("  disagreement a model would learn and the pipeline would then quietly override.")
    elif neutral >= 0.6:
        print(f"  {neutral:.0%} NEUTRAL. One person may hold both, which is the question memory")
        print("  supersession asks and the reason to prefer this corpus over MNLI. Use it whole.")
    elif 1 - neutral >= 0.6:
        print(f"  {1 - neutral:.0%} CONTRADICTION, and not split by relation either "
              f"({middle} relations sit in the")
        print("  middle), so two different pets read as a conflict however it is sliced. For")
        print("  supersession this corpus shares MNLI's problem exactly. Train only on rows whose")
        print("  contradiction comes from an explicitly negating triple, and treat")
        print("  same-relation/different-kind rows as unusable.")
    else:
        print(f"  Split — {neutral:.0%} neutral, and {middle} of {len(ranked)} relations sit in the "
              f"middle rather than")
        print("  at one end, so it is not cardinality either. That is the third answer and the")
        print("  worst one: it cannot be used whole and cannot be filtered by label alone.")


def rows_from_file(name, path):
    """A corpus downloaded by hand. Accepts JSONL or a JSON list — both are what these ship as."""
    text = pathlib.Path(path).read_text(encoding="utf-8")
    stripped = text.lstrip()
    if stripped.startswith("["):
        return json.loads(text)
    return [json.loads(line) for line in text.splitlines() if line.strip()]


def derive_dialogue_nli_labels(rows, label_names=None):
    """The label vocabulary this mirror uses, read off its own triples.

    Same machinery as --audit, for the same reason and against the same trap: the canonical release
    says entailment/neutral/contradiction and pietrolesci/dialogue_nli says positive/neutral/
    negative, and an adapter that knows only the first spelling silently discarded every row that
    was not neutral — 100,000 rows, none positive, which the all-negative guard caught and would
    not have caught had two of the three spellings happened to match.

    Returns None when the corpus cannot state its own encoding, and the caller stops rather than
    picking one.
    """
    import collections

    buckets = collections.defaultdict(collections.Counter)
    for row in rows:
        placed = bucket_of(row.get("triple1"), row.get("triple2"))
        if placed is None:
            continue
        buckets[placed[0]][label_of(row, label_names)] += 1
    return derive_label_names(buckets) if buckets else None


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

    materialised = list(rows)

    # DialogueNLI states its classes as strings, but which strings depends on the mirror. Derived
    # from the corpus's construction rather than read off the spelling, and printed, because a
    # decoding nobody sees is a decoding nobody can question.
    if name == "dialogue-nli":
        derived = derive_dialogue_nli_labels(materialised, kwargs.get("label_names"))
        if derived is None:
            raise SystemExit(
                f"{name}: could not derive what this mirror's labels mean. The two anchors it "
                f"reads them from — same-triple pairs are entailment by construction, unrelated "
                f"relation swaps are neutral by rule — did not come out cleanly, so the classes "
                f"cannot be named without guessing, and guessing here inverts the corpus. Run "
                f"`--audit` to see the buckets, or pass the mapping by hand.")
        if any(raw != canonical for raw, canonical in derived.items()):
            print("  label vocabulary derived from the corpus's own triples: " +
                  ", ".join(f"{raw} = {canonical}" for raw, canonical in sorted(
                      derived.items(), key=lambda pair: CANONICAL.index(pair[1]))))
        kwargs["label_map"] = derived

    mapped = adapters.ADAPTERS[name](materialised, **kwargs)

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
