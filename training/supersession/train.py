"""Trains and honestly scores the specialised supersession model against the pair corpus.

    python training/supersession/generate.py
    python training/supersession/assemble.py [--weak]
    dotnet run --project tools/Companion.Eval -- --only corpus --out training/corpus   # stamp incumbent
    python training/supersession/train.py [--weak] [--epochs 4]

Grouped 5-fold cross-validation over families, a 7-way MiniLM through the SAME training loop the
binary encoder used (imported, not copied), and metrics at the level deployment actually cares
about:

* per-label precision/recall at family level;
* the ACTION-level numbers — false-supersede rate above all, because a wrong supersede-action
  buries a true fact and every migration criterion is written against it;
* calibrated confidence (temperature on pooled out-of-fold predictions) and the SAFE-COVERAGE
  table: how much of the decision surface the model could own at each confidence bar while staying
  inside the false-supersede budget. Progressive adoption is the designed outcome, not a
  consolation prize — see docs/SUPERSESSION_TASK.md §migration.

The incumbent's verdict comes stamped on each row by the C# PairStamp — the shipped rules run over
the same pairs, never a transcription. Rows without a stamp are excluded from the comparison and
counted out loud.
"""
import argparse, collections, json, math, pathlib, random, statistics, sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / "cognition"))
from taxonomy import LABELS, REVIEW_ACTIONS, SUPERSEDE_ACTIONS, is_valid  # noqa: E402
import finetune_encoder  # noqa: E402  — the one training loop; see its fit() docstring

for _stream in (sys.stdout, sys.stderr):
    if hasattr(_stream, "reconfigure"):
        _stream.reconfigure(encoding="utf-8", errors="replace")

CORPUS = pathlib.Path(__file__).resolve().parents[1] / "corpus"
FOLDS = 5
BASE = "memory.supersession.pair"


def load(suffix):
    path = CORPUS / f"{BASE}.{suffix}.jsonl"
    if not path.exists():
        return []
    rows = [json.loads(line) for line in path.open(encoding="utf-8") if line.strip()]
    bad = [r for r in rows if not is_valid(r)]
    if bad:
        sys.exit(f"{path.name}: {len(bad)} rows fail the pair-schema invariants; fix the producer.")
    # Harvested rows arrive structured but unrendered; the renderer is one function on purpose.
    from taxonomy import render
    for r in rows:
        r.setdefault("text", render(r))
    return rows


def allowed_sources():
    """The manifest is the authority on what may contribute to a trained artifact."""
    path = CORPUS / f"{BASE}.manifest.json"
    if not path.exists():
        sys.exit(f"no manifest at {path} — run training/supersession/assemble.py first. The rule "
                 f"it enforces: we always know which sources, under which licences, fed a model.")
    return set(json.loads(path.read_text(encoding="utf-8"))["sources"])


def family_verdicts(rows, predicted):
    """Family-level truth and prediction: the modal predicted class per family."""
    grouped = collections.defaultdict(list)
    for row, label in zip(rows, predicted):
        grouped[row["family"]].append((row["label"], label))
    truth, pred = [], []
    for votes in grouped.values():
        truth.append(votes[0][0])
        counts = collections.Counter(label for _, label in votes)
        pred.append(counts.most_common(1)[0][0])
    return truth, pred


def per_label_table(truth, pred):
    lines = []
    for label in LABELS:
        tp = sum(1 for t, q in zip(truth, pred) if t == label and q == label)
        fp = sum(1 for t, q in zip(truth, pred) if t != label and q == label)
        fn = sum(1 for t, q in zip(truth, pred) if t == label and q != label)
        n = sum(1 for t in truth if t == label)
        precision = tp / (tp + fp) if tp + fp else 0.0
        recall = tp / (tp + fn) if tp + fn else 0.0
        f1 = 2 * precision * recall / (precision + recall) if precision + recall else 0.0
        lines.append(f"      {label:<12} n={n:<4} P={precision:.3f} R={recall:.3f} F1={f1:.3f}")
    return "\n".join(lines)


def action_stats(truth, pred):
    """Supersede-action precision/recall and the false-supersede rate, truth-UNCERTAIN excluded.

    UNCERTAIN truths have no right action to be scored against; a model ANSWER of UNCERTAIN (or
    CONTRADICTS) routes to review, which is never a supersede, so it is scored as declining one.
    """
    pairs = [(t in SUPERSEDE_ACTIONS, q in SUPERSEDE_ACTIONS)
             for t, q in zip(truth, pred) if t != "UNCERTAIN"]
    if not pairs:
        return 0.0, 0.0, 0.0, 0.0
    tp = sum(1 for t, q in pairs if t and q)
    fp = sum(1 for t, q in pairs if not t and q)
    fn = sum(1 for t, q in pairs if t and not q)
    precision = tp / (tp + fp) if tp + fp else 0.0
    recall = tp / (tp + fn) if tp + fn else 0.0
    f1 = 2 * precision * recall / (precision + recall) if precision + recall else 0.0
    false_supersede = fp / len(pairs)
    return precision, recall, f1, false_supersede


def bootstrap_action_delta(rows, pred_a, pred_b, iterations=4000, seed=7):
    """95% interval on (A - B) supersede-action F1, resampling FAMILIES — same discipline as
    crossval.py, because 'A scored higher' is not a finding and an interval that straddles zero is."""
    grouped = collections.defaultdict(list)
    for row, a, b in zip(rows, pred_a, pred_b):
        if row["label"] != "UNCERTAIN":
            grouped[row["family"]].append((row["label"], a, b))
    families = list(grouped.values())
    rng = random.Random(seed)

    def f1_of(sample, which):
        tp = fp = fn = 0
        for votes in sample:
            for t, a, b in votes:
                truth = t in SUPERSEDE_ACTIONS
                said = (a if which == 0 else b) in SUPERSEDE_ACTIONS
                tp += truth and said
                fp += said and not truth
                fn += truth and not said
        precision = tp / (tp + fp) if tp + fp else 0.0
        recall = tp / (tp + fn) if tp + fn else 0.0
        return 2 * precision * recall / (precision + recall) if precision + recall else 0.0

    deltas = sorted(
        f1_of(sample, 0) - f1_of(sample, 1)
        for sample in ([families[rng.randrange(len(families))] for _ in families]
                       for _ in range(iterations)))
    return (statistics.mean(deltas),
            deltas[int(0.025 * len(deltas))],
            deltas[int(0.975 * len(deltas)) - 1])


def temperature(probabilities, truths):
    """Grid-fit temperature on pooled out-of-fold predictions — never on the holdout.

    An uncalibrated softmax's '0.92' means nothing, and every threshold in the safe-coverage table
    inherits the nothing. Scaling log-probabilities and renormalising is equivalent to scaling the
    logits, which the pooled predictions no longer carry.
    """
    def nll(t):
        total = 0.0
        for probs, truth in zip(probabilities, truths):
            scaled = [math.log(max(p, 1e-9)) / t for p in probs]
            peak = max(scaled)
            log_z = peak + math.log(sum(math.exp(v - peak) for v in scaled))
            total -= scaled[LABELS.index(truth)] - log_z
        return total / len(truths)

    best = min((nll(t / 100), t / 100) for t in range(25, 401, 5))
    return best[1]


def calibrated(probs, t):
    scaled = [math.log(max(p, 1e-9)) / t for p in probs]
    peak = max(scaled)
    z = sum(math.exp(v - peak) for v in scaled)
    return [math.exp(v - peak) / z for v in scaled]


def expected_calibration_error(probabilities, truths, bins=10):
    bucketed = collections.defaultdict(list)
    for probs, truth in zip(probabilities, truths):
        top = max(range(len(LABELS)), key=lambda i: probs[i])
        bucketed[min(int(probs[top] * bins), bins - 1)].append((probs[top], LABELS[top] == truth))
    error = 0.0
    for values in bucketed.values():
        confidence = statistics.mean(p for p, _ in values)
        accuracy = statistics.mean(1.0 if ok else 0.0 for _, ok in values)
        error += len(values) / len(truths) * abs(confidence - accuracy)
    return error


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--weak", action="store_true", help="pretrain on the weak stage first")
    # 16, not the binary trainer's 4: ninety rows over seven classes is ~25 steps per epoch of
    # batch-16 training, and at 4 epochs the first run never left the prior (max softmax 0.16 on
    # every holdout row). Underfitting that looks like a verdict is worse than a slow default.
    parser.add_argument("--epochs", type=float, default=16)
    parser.add_argument("--base", default=finetune_encoder.DEFAULT_BASE)
    args = parser.parse_args()

    sources = allowed_sources()
    develop, holdout = [], []
    for suffix in ("synthetic", "regression", "reviewed"):
        for row in load(suffix):
            if row.get("source") not in sources and suffix != "reviewed":
                sys.exit(f"row source {row.get('source')!r} is not in the manifest — a corpus "
                         f"file cannot contribute unaccounted. Re-run assemble.py.")
            (holdout if row.get("split") == "holdout" else develop).append(row)
    weak = load("weak") if args.weak else []

    families = {r["family"] for r in develop}
    print(f"develop {len(develop)} rows / {len(families)} families   "
          f"holdout {len(holdout)} rows (frozen regression)   weak {len(weak)} rows")
    if all(r["source"] == "synthetic" for r in develop):
        print("ALL DEVELOP ROWS ARE SYNTHETIC — one author's templates. Real pairs arrive via "
              "CognitiveModels:Capture and the adjudication queue; every number below is "
              "provisional until they do.")
    print()

    ordered = sorted(families)
    random.Random(11).shuffle(ordered)
    fold_of = {family: i % FOLDS for i, family in enumerate(ordered)}

    pooled = [None] * len(develop)
    for fold in range(FOLDS):
        train_rows = [r for r in develop if fold_of[r["family"]] != fold]
        test_idx = [i for i, r in enumerate(develop) if fold_of[r["family"]] == fold]
        if weak:
            # Weak stage first, gold stage second, per fold — the weak rows never cross into the
            # fold's test families because they come from a different corpus entirely.
            train_rows = weak + train_rows
        print(f"   fold {fold + 1}/{FOLDS}: {len(train_rows)} train rows...", flush=True)
        model, tokenizer, device = finetune_encoder.fit(
            train_rows, base=args.base, epochs=args.epochs, quiet=True, classes=list(LABELS))
        probs = finetune_encoder.predict(
            model, tokenizer, [develop[i] for i in test_idx], device=device, full=True)
        for i, p in zip(test_idx, probs):
            pooled[i] = p

    predicted = [LABELS[max(range(len(LABELS)), key=lambda j: p[j])] for p in pooled]
    truth_rows = [r["label"] for r in develop]

    print()
    print("per-label, family level (each family predicted by a model that never saw it):")
    family_truth, family_pred = family_verdicts(develop, predicted)
    print(per_label_table(family_truth, family_pred))

    precision, recall, f1, false_supersede = action_stats(truth_rows, predicted)
    print()
    print(f"   supersede ACTION (row level): P={precision:.3f} R={recall:.3f} F1={f1:.3f}   "
          f"false-supersede rate {false_supersede:.3f}")

    stamped = [r.get("incumbent") for r in develop]
    if any(stamped):
        missing = sum(1 for s in stamped if not s)
        if missing:
            print(f"   ({missing} rows carry no incumbent stamp and are excluded from the comparison)")
        rows_cmp = [r for r, s in zip(develop, stamped) if s]
        pred_cmp = [p for p, s in zip(predicted, stamped) if s]
        inc_cmp = [s for s in stamped if s]
        ip, ir, if1, ifs = action_stats([r["label"] for r in rows_cmp], inc_cmp)
        print(f"   incumbent, same rows:         P={ip:.3f} R={ir:.3f} F1={if1:.3f}   "
              f"false-supersede rate {ifs:.3f}")
        mean_d, lo, hi = bootstrap_action_delta(rows_cmp, pred_cmp, inc_cmp)
        verdict = ("beats the incumbent" if lo > 0
                   else "LOSES to the incumbent" if hi < 0
                   else "indistinguishable from the incumbent")
        print(f"   model - incumbent, action F1: {mean_d:+.3f} [{lo:+.3f}, {hi:+.3f}]   {verdict}")
    else:
        print("   no incumbent stamps at all — run the eval tool's corpus pass; no comparison is "
              "reported against an incumbent that never ran.")

    # ---- calibration, then the safe-coverage table ---------------------------------------------
    t = temperature(pooled, truth_rows)
    scaled = [calibrated(p, t) for p in pooled]
    ece_before = expected_calibration_error(pooled, truth_rows)
    ece_after = expected_calibration_error(scaled, truth_rows)
    print()
    # NLL-optimal temperature is the standard fit and, on a corpus this small, it can sharpen a
    # barely-trained model and make calibration WORSE. A temperature that raises ECE is refused:
    # the safe-coverage bars below are only meaningful over probabilities that mean something,
    # and an identity temperature is honest where a fitted one is flattering.
    if ece_after > ece_before:
        print(f"   calibration: fitted temperature {t:.2f} REJECTED (ECE {ece_before:.3f} -> "
              f"{ece_after:.3f}); using T=1. A fit this unstable is a symptom of too little data.")
        t, scaled = 1.0, pooled
    else:
        print(f"   calibration: temperature {t:.2f}, ECE {ece_before:.3f} -> {ece_after:.3f}")

    print()
    print("   safe coverage — how much of the surface the model could OWN at each confidence bar")
    print("   (owning = acting without the incumbent; abstentions fall back to it):")
    print(f"      {'bar':>5} {'owns':>6} {'false-supersede in owned':>25}")
    scoreable = [(r, p) for r, p in zip(develop, scaled) if r["label"] != "UNCERTAIN"]
    for bar in (0.5, 0.6, 0.7, 0.8, 0.9, 0.95):
        owned = [(r, LABELS[max(range(len(LABELS)), key=lambda j: p[j])])
                 for r, p in scoreable if max(p) >= bar]
        owned = [(r, q) for r, q in owned if q not in REVIEW_ACTIONS]
        if not owned:
            print(f"      {bar:>5.2f} {'0%':>6} {'-':>25}")
            continue
        fs = sum(1 for r, q in owned
                 if q in SUPERSEDE_ACTIONS and r["label"] not in SUPERSEDE_ACTIONS) / len(owned)
        print(f"      {bar:>5.2f} {len(owned) / len(scoreable):>6.0%} {fs:>25.3f}")

    # ---- the frozen holdout, once ---------------------------------------------------------------
    if holdout:
        print()
        print(f"   frozen holdout ({len(holdout)} production incidents), fit on all develop rows:")
        model, tokenizer, device = finetune_encoder.fit(
            (weak + develop) if weak else develop,
            base=args.base, epochs=args.epochs, quiet=True, classes=list(LABELS))
        probs = finetune_encoder.predict(model, tokenizer, holdout, device=device, full=True)
        hold_pred = [LABELS[max(range(len(LABELS)), key=lambda j: p[j])] for p in probs]
        right = sum(1 for r, q in zip(holdout, hold_pred) if r["label"] == q)
        print(f"      {right}/{len(holdout)} exact-label; every miss printed, because twelve rows "
              f"are twelve incidents, not a statistic:")
        for r, q, p in zip(holdout, hold_pred, probs):
            if r["label"] != q:
                print(f"         truth {r['label']:<11} said {q:<11} ({max(p):.2f})  "
                      f"{r['incoming']['utterance'][:60]!r}")

    print()
    print("This trains and measures; it does not decide. Migration criteria live in "
          "docs/SUPERSESSION_TASK.md §migration, and the first of them — real adjudicated pairs — "
          "has a queue, not a number, until capture has run.")


if __name__ == "__main__":
    main()
