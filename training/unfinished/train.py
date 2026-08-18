"""Trains a classifier for "has the user left this unfinished?" and scores it honestly.

The incumbent is a regex listing obligation phrasings — "need to", "have to", "got to", "must".
It has precision 0.933 and recall 0.215: it never claims work that is not there, and misses
five hundred and ten cases out of six hundred and fifty. Nobody can enumerate how people say
they have not finished something, which is the whole argument for learning it.

Two things make the evaluation trustworthy rather than flattering:

  * the test split contains TEMPLATE FAMILIES the model has never seen, not merely unseen rows.
    Rows are templates crossed with fillers, so a row-level split would put "I'm behind on the
    shed roof" in training and "I'm behind on the migration" in test and call it generalisation.

  * the incumbent is scored on exactly the same test rows, in this script, so the comparison
    cannot drift apart from the thing it compares against.

Usage:  python training/unfinished/train.py
"""
import json, pathlib, re, sys

from sklearn.feature_extraction.text import TfidfVectorizer
from sklearn.linear_model import LogisticRegression
from sklearn.metrics import precision_recall_fscore_support
from sklearn.pipeline import make_pipeline

CORPUS = pathlib.Path(__file__).resolve().parents[1] / "corpus"
DECISION = "memory.unfinished"


def load(split):
    path = CORPUS / f"{DECISION}.{split}.jsonl"
    rows = [json.loads(line) for line in path.open(encoding="utf-8") if line.strip()]
    return [r["text"] for r in rows], [r["label"] for r in rows], [r["family"] for r in rows]


# The shipped regex, transcribed so the baseline is measured on the same rows rather than quoted
# from another run. If it drifts from the C#, the two numbers stop being comparable — so this is
# the one piece of duplication here that earns its place, and it is asserted below.
OBLIGATION = re.compile(
    r"\b(?:i)\s*(?:'ve|\s+have)?\s*(?:still\s+)?("
    r"need\s+to|have\s+to|got\s+to|gotta|must|"
    r"want\s+to\s+(?:get|finish|sort|build|write|fix)|"
    r"(?:'m|\s+am)\s+(?:supposed\s+to|meant\s+to|in\s+the\s+middle\s+of)"
    r")\s+([^.!?\n]{3,200})",
    re.IGNORECASE)
SETTLED = re.compile(r"\b(?:did|done|finished|sorted|already|no longer|don'?t need|didn'?t need)\b", re.I)


def heuristic(text):
    m = OBLIGATION.search(text)
    return bool(m) and not SETTLED.search(m.group(2))


def score(name, y_true, y_pred):
    p, r, f, _ = precision_recall_fscore_support(y_true, y_pred, average="binary", zero_division=0)
    print(f"  {name:<22} P={p:.3f} R={r:.3f} F1={f:.3f}")
    return f


def main():
    if not CORPUS.exists():
        sys.exit(f"no corpus at {CORPUS} — run: dotnet run --project tools/Companion.Eval -- --only corpus --out training/corpus")

    xtr, ytr, ftr = load("train")
    xva, yva, _ = load("validation")
    xte, yte, fte = load("test")

    unseen = set(fte) - set(ftr)
    print(f"train {len(xtr)} rows / {len(set(ftr))} families")
    print(f"test  {len(xte)} rows / {len(set(fte))} families, {len(unseen)} of them unseen in training")
    print()

    # Character n-grams as well as words: the signal is often morphological ("haven't", "won't",
    # "-ing"), and a word-only model on 550 rows leans on the filler nouns instead.
    model = make_pipeline(
        TfidfVectorizer(analyzer="char_wb", ngram_range=(3, 5), min_df=2, sublinear_tf=True),
        LogisticRegression(max_iter=2000, class_weight="balanced", C=2.0))
    model.fit(xtr + xva, ytr + yva)

    print("on the held-out test families:")
    base = score("regex (incumbent)", yte, [heuristic(t) for t in xte])
    learned = score("tf-idf + logreg", yte, list(model.predict(xte)))
    print()
    print(f"  delta F1: {learned - base:+.3f}")

    wrong = [(t, p) for t, y, p in zip(xte, yte, model.predict(xte)) if y != p]
    print(f"  model errors: {len(wrong)} of {len(xte)}")
    for t, p in wrong[:6]:
        print(f"     said {'yes' if p else 'no ':<3} — {t}")


if __name__ == "__main__":
    main()
