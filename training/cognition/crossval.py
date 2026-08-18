"""Trains a classifier for each cognitive decision in the corpus and scores it honestly.

    python training/cognition/crossval.py                    # every decision
    python training/cognition/crossval.py memory.unfinished  # one

Run the generator first, which also writes each row's incumbent answer into the data:

    dotnet run --project tools/Companion.Eval -- --only corpus --out training/corpus

---------------------------------------------------------------------------------------------
WHY THIS CROSS-VALIDATES INSTEAD OF REPORTING ONE SPLIT

An earlier version of this script fitted on the train split and scored the ten held-out test
families of memory.unfinished:

    regex   F1 0.609        model   F1 0.922        "the first heuristic worth replacing"

The same code, same seed, scored on the ten VALIDATION families instead:

    regex   F1 0.000        model   F1 0.595

The regex fires on nothing at all in one draw of ten families and on 44 % of rows in another.
Neither number is wrong. Both are properties of WHICH TEN FAMILIES landed in the split rather
than of the method, and a +0.322 headline drawn from one of them is a coin flip reported to
three decimal places. Ten families is not a sample.

So: grouped cross-validation over the train+validation families, every family predicted exactly
once by a model that has never seen it, and a fold-to-fold spread reported beside every mean. If
the spread is wider than the gap between two variants, there is no result - which is the case
this corpus is in today, and saying so is the entire point of the harness.

The test families stay untouched until the end and are scored once, on whatever
cross-validation picked. Choosing on the test set is not a measurement, it is a fit.

FAMILY-MACRO IS THE PRIMARY METRIC. Rows are templates crossed with fillers, and a template
carrying a {when} filler renders sixty rows where a bare one renders ten - so a row-weighted F1
counts "I need to finish {t} {w}" six times as heavily as "I'm behind on {t}", for no reason
except how many fillers somebody wrote. A family is one way of saying a thing, and generalising
to unseen ways of saying things is the whole question.

THE INCUMBENT IS NOT REIMPLEMENTED HERE. Every row carries what the shipped C# rule answered for
it. A baseline transcribed into Python is a baseline that drifts from the code it claims to
measure, and the day it drifts the comparison silently stops being one.
"""
import collections, json, pathlib, random, statistics, sys

# Windows consoles default to cp1252, and these scripts (and torch's own exporter) print em-dashes
# and the odd emoji. Encoding is not cosmetic here: on the first real run torch.onnx.export
# captured the graph successfully and then died with UnicodeEncodeError writing its own success
# message, which reads exactly like a failed export. Reconfigured rather than left to the caller
# to set PYTHONIOENCODING, because the failure names the wrong culprit.
for _stream in (sys.stdout, sys.stderr):
    if hasattr(_stream, "reconfigure"):
        _stream.reconfigure(encoding="utf-8", errors="replace")

from sklearn.feature_extraction.text import TfidfVectorizer
from sklearn.linear_model import LogisticRegression
from sklearn.metrics import precision_recall_fscore_support
from sklearn.model_selection import GroupKFold
from sklearn.pipeline import make_pipeline

CORPUS = pathlib.Path(__file__).resolve().parents[1] / "corpus"

# What fraction of real conversational sentences carry the signal. The corpus is built to separate
# two classes, not to imitate a conversation, so it is far denser in positives than anything the
# companion will ever see. Precision measured on it is an upper bound and a generous one.
CONVERSATIONAL_PRIOR = 0.03

FOLDS = 5

# Sentences the settled-marker check would veto. This is the ONE deterministic signal the
# compositions below add to the model, and it is deliberately the incumbent's own: "did", "done",
# "finished", "sorted", "already", "no longer". Not a new phrase list — the existing one, applied
# to the whole sentence rather than only to the object of an obligation phrase.
SETTLED_WORDS = ("did", "done", "finished", "sorted", "already", "no longer",
                 "don't need", "dont need", "didn't need", "didnt need")


def settled(text):
    lowered = f" {text.lower()} "
    return any(f" {w} " in lowered or lowered.startswith(f" {w} ") for w in SETTLED_WORDS)


class Row:
    __slots__ = ("text", "label", "family", "heuristic")

    def __init__(self, d):
        self.text = d["text"]
        self.label = d["label"]
        self.family = d["family"]
        self.heuristic = d.get("heuristic")


def load(decision, split):
    path = CORPUS / f"{decision}.{split}.jsonl"
    if not path.exists():
        return []
    return [Row(json.loads(line)) for line in path.open(encoding="utf-8") if line.strip()]


def load_extra(decision, suffix):
    """Rows from outside the generator: harvested real conversations, or a borrowed research corpus.

    These go into the DEVELOPMENT set rather than the held-out one, which is the opposite of what
    it feels like it should be. Real rows are the scarce and valuable thing, and the temptation is
    to save them for the final test — but the held-out families exist to catch a model that has
    learned a phrasing rather than the concept, and a real sentence is its own family, so a test
    set made of them measures something different from the one the synthetic families measure. Two
    incomparable numbers is worse than one honest one. When there are enough real rows to hold some
    back, they should become their own suite rather than being mixed into this one.
    """
    path = CORPUS / f"{decision}.{suffix}.jsonl"
    if not path.exists():
        return []
    rows = [Row(json.loads(line)) for line in path.open(encoding="utf-8") if line.strip()]
    return [r for r in rows if r.label is not None]


SUFFIXES = ("train", "validation", "test", "borrowed", "reviewed")


def decisions():
    """Every decision with a corpus of any kind, not only the ones the C# generator writes.

    This used to glob *.train.jsonl alone, which meant a decision whose only rows are BORROWED was
    invisible: `fetch.py dialogue-nli` wrote 310,110 supersession pairs and a bare `crossval.py`
    scored four generated decisions and never mentioned them. The corpora with the most evidence
    behind them were the ones silently skipped, and nothing said so.
    """
    found = set()
    for suffix in SUFFIXES:
        for path in CORPUS.glob(f"*.{suffix}.jsonl"):
            found.add(path.name[: -len(f".{suffix}.jsonl")])
    return sorted(found)


# Families, not rows, are the unit of evidence — and a borrowed corpus brings tens of thousands of
# them, far past the point where another one changes an interval. Capping keeps a run finishable;
# capping SILENTLY would be the same sin as reporting a ten-family draw to three decimals, so the
# sample is seeded and what it dropped is printed.
MAX_FAMILIES = 4000


# Which learner is being cross-validated. The linear model is the default because it is the one
# every published verdict was measured against; --encoder swaps in the 22M MiniLM that
# finetune_encoder.py exports, THROUGH THE SAME FOLDS, the same family-macro metric and the same
# paired bootstrap. That is the whole point: "the fine-tune does not get to skip the comparison the
# linear model was held to", and a separate script with its own splits would be a different
# measurement wearing the same words.
#
# The training loop is imported from finetune_encoder rather than reimplemented. A second copy of
# the thing under test is the drift this repo already removed once, when the incumbent's verdict
# stopped being transcribed into Python and started being stamped into the data.
LEARNERS = ("linear", "encoder")


def fresh_model():
    # Character n-grams as well as words: the signal is often morphological ("haven't", "won't",
    # "-ing"), and a word-only model on a few hundred rows leans on the filler nouns instead.
    return make_pipeline(
        TfidfVectorizer(analyzer="char_wb", ngram_range=(3, 5), min_df=2, sublinear_tf=True),
        LogisticRegression(max_iter=2000, class_weight="balanced", C=2.0))


# Each variant answers yes or no from the model's probability and the row. The interesting ones are
# compositions rather than replacements: a union cannot lose a case the incumbent gets, which is a
# structural guarantee rather than a measured one, and a veto restores a precision a threshold
# cannot buy back.
VARIANTS = {
    "incumbent": lambda p, r: bool(r.heuristic),
    "model @.50": lambda p, r: p >= 0.5,
    "union": lambda p, r: p >= 0.5 or bool(r.heuristic),
    "union + settled veto": lambda p, r: (p >= 0.5 or bool(r.heuristic)) and not settled(r.text),
    "model + settled veto": lambda p, r: p >= 0.5 and not settled(r.text),
}


def prf(truth, pred):
    p, r, f, _ = precision_recall_fscore_support(truth, pred, average="binary", zero_division=0)
    return p, r, f


def by_family(rows, pred):
    """One vote per family: how a way of saying something is handled, not how often it was said."""
    grouped = collections.defaultdict(list)
    for row, q in zip(rows, pred):
        grouped[row.family].append((row.label, q))
    truth = [v[0][0] for v in grouped.values()]
    calls = [sum(q for _, q in v) * 2 >= len(v) for v in grouped.values()]
    return truth, calls


def bootstrap_delta(rows, pred_a, pred_b, iterations=4000, seed=7):
    """A confidence interval on (A - B) family-macro F1, resampling FAMILIES with replacement.

    The per-fold spread above says how little eight families can tell you. This says the thing
    actually being asked: given these forty families, how sure are we that A is better than B?
    Paired, because both variants are scored on the same families and the pairing removes the
    variance that comes from which families exist at all — which is most of it.

    An interval that straddles zero is a result too: it means the corpus is not big enough to
    separate them, and the answer is more families rather than more tuning.
    """
    grouped = collections.defaultdict(list)
    for row, a, b in zip(rows, pred_a, pred_b):
        grouped[row.family].append((row.label, a, b))
    families = [(v[0][0],
                 sum(a for _, a, _ in v) * 2 >= len(v),
                 sum(b for _, _, b in v) * 2 >= len(v)) for v in grouped.values()]

    rng = random.Random(seed)
    deltas = []
    for _ in range(iterations):
        sample = [families[rng.randrange(len(families))] for _ in families]
        truth = [t for t, _, _ in sample]
        fa = precision_recall_fscore_support(
            truth, [a for _, a, _ in sample], average="binary", zero_division=0)[2]
        fb = precision_recall_fscore_support(
            truth, [b for _, _, b in sample], average="binary", zero_division=0)[2]
        deltas.append(fa - fb)
    deltas.sort()
    lo = deltas[int(0.025 * len(deltas))]
    hi = deltas[int(0.975 * len(deltas)) - 1]
    return statistics.mean(deltas), lo, hi


def precision_at_prior(truth, pred, prior):
    """Precision this recall and false-positive rate would give at a realistic base rate.

    Precision is the one metric that moves when the class balance does, and reporting only the
    corpus number lets a model look safe here and invent memories in production. That is the
    failure mode that matters: an open loop is surfaced unprompted, so a false positive is her
    asking how work that does not exist is going.
    """
    pos = [q for t, q in zip(truth, pred) if t]
    neg = [q for t, q in zip(truth, pred) if not t]
    recall = sum(pos) / len(pos) if pos else 0.0
    fpr = sum(neg) / len(neg) if neg else 0.0
    hits, false = recall * prior, fpr * (1 - prior)
    return hits / (hits + false) if hits + false else 0.0


def fit_encoder_fold(train_rows, test_rows, options):
    """One fold of the encoder: fine-tune on this fold's training rows, score its test rows.

    Row objects here carry __slots__, and finetune_encoder.fit reads dicts, so they are converted
    rather than the trainer being taught a second row shape. The conversion is deliberately total —
    text, label and family — so a fold sees exactly what the standalone script sees.
    """
    import importlib, sys as _sys
    _sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
    encoder = importlib.import_module("finetune_encoder")

    as_dicts = lambda rows: [{"text": r.text, "label": r.label, "family": r.family} for r in rows]
    model, tokenizer, device = encoder.fit(as_dicts(train_rows), quiet=True, **options)
    return encoder.predict(model, tokenizer, as_dicts(test_rows),
                           options.get("max_len", 128), device)


def run(decision, learner="linear", encoder_options=None):
    reviewed = load_extra(decision, "reviewed")
    borrowed = load_extra(decision, "borrowed")
    develop = load(decision, "train") + load(decision, "validation") + reviewed + borrowed
    holdout = load(decision, "test")
    families = {r.family for r in develop}

    print(f"=== {decision} ===")
    if len(families) > MAX_FAMILIES:
        keep = sorted(families)
        random.Random(11).shuffle(keep)
        keep = set(keep[:MAX_FAMILIES])
        dropped_rows = len(develop) - sum(1 for r in develop if r.family in keep)
        develop = [r for r in develop if r.family in keep]
        borrowed = [r for r in borrowed if r.family in keep]
        reviewed = [r for r in reviewed if r.family in keep]
        print(f"    CAPPED at {MAX_FAMILIES} families of {len(families)} "
              f"({dropped_rows} rows dropped, seeded sample). Every number below is over the "
              f"sample, not the corpus.")
        families = keep
    if not develop or len(families) < FOLDS * 2 or len({r.label for r in develop}) < 2:
        print(f"    {len(develop)} rows / {len(families)} families — too few to cross-validate; skipped")
        print()
        return
    unstamped = sum(1 for r in develop if r.heuristic is None)
    judged = unstamped < len(develop)
    if not judged:
        print("    NO INCUMBENT VERDICT ON ANY ROW. Every comparison against it is withheld below:")
        print("    an incumbent scored as declining on rows it was never run over is not a")
        print("    baseline, it is a zero, and beating a zero is not evidence of anything. The")
        print("    model's own numbers are reported and nothing is called a win.")
    elif unstamped:
        # Counting an absent verdict as "said no" would credit the incumbent with perfect precision
        # on rows it was never run over, which flatters exactly the thing under test.
        print(f"    {unstamped} rows carry no incumbent verdict (borrowed or captured). The "
              f"incumbent's row below is scored as if it declined on those; run the real detector "
              f"over them before believing its precision.")

    print(f"    development {len(develop):>4} rows / {len(families)} families   "
          f"held out {len(holdout):>4} rows / {len({r.family for r in holdout})} families")
    if reviewed or borrowed:
        parts = []
        if reviewed:
            parts.append(f"{len(reviewed)} human-labelled from real conversations")
        if borrowed:
            parts.append(f"{len(borrowed)} from a research corpus")
        print(f"    of which {' and '.join(parts)} "
              f"({(len(reviewed) + len(borrowed)) / len(develop):.0%} of the corpus is not synthetic)")
    else:
        print("    all synthetic — every number below is a statement about one person's templates. "
              "See training/datasets/fetch.py and training/cognition/harvest.py")

    texts = [r.text for r in develop]
    labels = [r.label for r in develop]
    groups = [r.family for r in develop]

    variants = VARIANTS if judged else {
        name: decide for name, decide in VARIANTS.items()
        if name in ("model @.50", "model + settled veto")}

    pooled = [0.0] * len(develop)
    per_fold = collections.defaultdict(list)
    for fold, (train_idx, test_idx) in enumerate(
            GroupKFold(n_splits=FOLDS).split(texts, labels, groups=groups), start=1):
        fold_rows = [develop[i] for i in test_idx]
        if learner == "encoder":
            print(f"      fold {fold}/{FOLDS}: fine-tuning on {len(train_idx)} rows...", flush=True)
            probs = fit_encoder_fold(
                [develop[i] for i in train_idx], fold_rows, encoder_options or {})
        else:
            model = fresh_model().fit([texts[i] for i in train_idx], [labels[i] for i in train_idx])
            probs = model.predict_proba([texts[i] for i in test_idx])[:, 1]
        for i, prob in zip(test_idx, probs):
            pooled[i] = prob
        for name, decide in variants.items():
            pred = [decide(prob, row) for prob, row in zip(probs, fold_rows)]
            per_fold[name].append(prf(*by_family(fold_rows, pred))[2])

    print()
    print(f"    {FOLDS} folds, family-macro F1:")
    print(f"      {'variant':<22} {'mean':>6} {'spread':>8}   per fold")
    means = {}
    for name in variants:
        fold_f1 = per_fold[name]
        means[name] = statistics.mean(fold_f1)
        print(f"      {name:<22} {means[name]:>6.3f} {statistics.stdev(fold_f1):>+8.3f}   "
              + " ".join(f"{v:.2f}" for v in fold_f1))

    ordered = sorted(means, key=means.get, reverse=True)
    winner, runner = ordered[0], ordered[1 if len(ordered) > 1 else 0]
    gap = means[winner] - means[runner]
    spread = max(statistics.stdev(per_fold[winner]), statistics.stdev(per_fold[runner]))
    print()
    print(f"    best: {winner} ({means[winner]:.3f}), ahead of {runner} by {gap:.3f}; "
          f"fold spread ±{spread:.3f}")
    if spread > gap:
        print("    ->  the fold spread is wider than the gap, so the per-fold means order nothing. "
              "See the interval below.")

    # ---- pooled out-of-fold: every family predicted once, by a model that never saw it ----------
    print()
    print(f"    pooled out-of-fold over all {len(families)} families:")
    print(f"      {'variant':<22} {'row F1':>7} {'fam P':>6} {'fam R':>6} {'fam F1':>7} "
          f"{'P @' + format(CONVERSATIONAL_PRIOR, '.0%'):>9}")
    pooled_pred = {}
    for name, decide in variants.items():
        pred = [decide(p, r) for p, r in zip(pooled, develop)]
        pooled_pred[name] = pred
        ftruth, fcalls = by_family(develop, pred)
        fp_, fr, ff = prf(ftruth, fcalls)
        print(f"      {name:<22} {prf(labels, pred)[2]:>7.3f} {fp_:>6.3f} {fr:>6.3f} {ff:>7.3f} "
              f"{precision_at_prior(labels, pred, CONVERSATIONAL_PRIOR):>9.3f}")

    # ---- is the difference real? -----------------------------------------------------------
    print()
    if not judged:
        print("    no paired bootstrap: there is no incumbent verdict on these rows to pair with.")
        print("    Run the shipped detector over them (tools/Companion.Eval) if a comparison is")
        print("    wanted; until then this is a measurement of the model alone.")
        print()
        return
    print(f"    paired bootstrap over families, 95 % interval on the difference in family-macro F1:")
    for name in dict.fromkeys([winner, "union", "model @.50"]):
        if name == "incumbent":
            continue
        mean_d, lo, hi = bootstrap_delta(develop, pooled_pred[name], pooled_pred["incumbent"])
        verdict = ("beats the incumbent" if lo > 0
                   else "LOSES to the incumbent" if hi < 0
                   else "indistinguishable from the incumbent")
        print(f"      {name + ' - incumbent':<26} {mean_d:>+7.3f}  "
              f"[{lo:+.3f}, {hi:+.3f}]   {verdict}")

    # ---- the regression check, which is the reason the compositions exist ----------------------
    #
    # Not a metric. An F1 that rises while the incumbent's own cases start failing is a trade being
    # reported as an upgrade, and an average hides it completely.
    print()
    for name, pred in pooled_pred.items():
        lost = [r.text for r, q in zip(develop, pred) if r.label and r.heuristic and not q]
        print(f"      {name:<22} " + ("keeps every case the incumbent gets"
                                      if not lost else f"LOSES {len(lost)}: {lost[0]!r}"))

    # ---- what it gets wrong, by family ---------------------------------------------------------
    print()
    for name in dict.fromkeys([winner, "model @.50", "incumbent"]):
        grouped = collections.defaultdict(list)
        for row, q in zip(develop, pooled_pred[name]):
            grouped[row.family].append((row.label, q))
        wrong = [(fam, sum(q for _, q in v) * 2 >= len(v))
                 for fam, v in sorted(grouped.items())
                 if (sum(q for _, q in v) * 2 >= len(v)) != v[0][0]]
        print(f"      {name} misses {len(wrong)} of {len(families)} families"
              + (":" if wrong else ""))
        for fam, said in wrong[:8]:
            print(f"         said {'yes' if said else 'no ':<3} - {fam}")
        if len(wrong) > 8:
            print(f"         ... and {len(wrong) - 8} more")

    # ---- the held-out families, once -----------------------------------------------------------
    if holdout and len({r.label for r in holdout}) == 2:
        if learner == "encoder":
            probs = fit_encoder_fold(develop, holdout, encoder_options or {})
        else:
            model = fresh_model().fit(texts, labels)
            probs = model.predict_proba([r.text for r in holdout])[:, 1]
        print()
        print(f"    held-out families, scored once, on the variant CV chose ({winner}):")
        for name in dict.fromkeys(["incumbent", winner]):
            pred = [VARIANTS[name](p, r) for p, r in zip(probs, holdout)]
            fp_, fr, ff = prf(*by_family(holdout, pred))
            print(f"      {name:<22} family P={fp_:.3f} R={fr:.3f} F1={ff:.3f}   "
                  f"P @{CONVERSATIONAL_PRIOR:.0%} base rate "
                  f"{precision_at_prior([r.label for r in holdout], pred, CONVERSATIONAL_PRIOR):.3f}")
    print()


def main():
    if not CORPUS.exists():
        sys.exit(f"no corpus at {CORPUS} — run: "
                 f"dotnet run --project tools/Companion.Eval -- --only corpus --out training/corpus")

    argv = [a for a in sys.argv[1:]]
    learner = "linear"
    if "--encoder" in argv:
        argv.remove("--encoder")
        learner = "encoder"
    base = None
    if "--base" in argv:
        i = argv.index("--base")
        base = argv[i + 1]
        del argv[i:i + 2]
    epochs = None
    if "--epochs" in argv:
        i = argv.index("--epochs")
        epochs = float(argv[i + 1])
        del argv[i:i + 2]

    options = {}
    if base:
        options["base"] = base
    if epochs is not None:
        options["epochs"] = epochs

    wanted = argv or decisions()
    if learner == "encoder":
        print(f"learner: encoder ({options.get('base', 'sentence-transformers/all-MiniLM-L6-v2')}), "
              f"{FOLDS} folds fine-tuned independently — slow on purpose, because the alternative is")
        print("a single split, and a single split is what produced the retracted +0.322 headline.")
        print()
    for decision in wanted:
        run(decision, learner=learner, encoder_options=options)


if __name__ == "__main__":
    main()
