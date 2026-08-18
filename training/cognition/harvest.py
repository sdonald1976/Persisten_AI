"""Turns captured judgements from a running companion into a review queue.

    python training/cognition/harvest.py --url http://localhost:5000 --token-file .companion-api-token

Writes one file per decision under training/corpus:

    memory.unfinished.captured.jsonl        label: null   <- needs a human
    memory.unfinished.reviewed.jsonl        label: true   <- written by you, read by the trainer

---------------------------------------------------------------------------------------------
WHY THIS EXISTS

Every verdict in docs/SPECIALIST_MODELS.md so far ends at the same sentence. The corpus is
synthetic; one person wrote the templates and the rule being tested; nobody can tell whether a
model generalised or learned that person's habits. Fold-to-fold spread on memory.unfinished is
±0.27 across forty template families, which is wider than every difference being measured, and
more templates from the same hand fix neither problem.

Real sentences do. They arrive through the turn, and `CognitiveModels:Capture` writes down what
each heuristic said about them. This pulls those rows out.

WHAT A CAPTURED ROW IS AND IS NOT

The heuristic's verdict is a WEAK LABEL, not a label. Training on it directly teaches a model to
imitate the regex, including its mistakes — which for memory.unfinished means learning to miss
five cases in six, because that is what the incumbent does. Its value is that it sorts the queue:
the rows where the rule said yes are worth reading first, and the rows where it said no are where
its misses are hiding.

So `label` comes out null and a human fills it in. That is the slow part and there is no way
round it; the corpus that decides whether a model replaces a rule cannot be labelled by the rule.

`heuristic` is carried through unchanged, so the incumbent can be scored on exactly the same rows
without being reimplemented here — the same discipline the generated corpus already follows.

Rows whose text was dropped (the capture holds the verdict but not the sentence when it looked
like a credential) are written with `text: null` and excluded from the review queue. They still
count: the RATE the rule fires at is what every precision figure so far has had to assume rather
than measure, and the rate survives the redaction.
"""
import argparse, collections, json, pathlib, sys, urllib.error, urllib.parse, urllib.request
import sys

# Windows consoles default to cp1252, and these scripts (and torch's own exporter) print em-dashes
# and the odd emoji. Encoding is not cosmetic here: on the first real run torch.onnx.export
# captured the graph successfully and then died with UnicodeEncodeError writing its own success
# message, which reads exactly like a failed export. Reconfigured rather than left to the caller
# to set PYTHONIOENCODING, because the failure names the wrong culprit.
for _stream in (sys.stdout, sys.stderr):
    if hasattr(_stream, "reconfigure"):
        _stream.reconfigure(encoding="utf-8", errors="replace")


CORPUS = pathlib.Path(__file__).resolve().parents[1] / "corpus"


def fetch(url, token, subject, count):
    query = f"{url.rstrip('/')}/diagnostics/shadow/captures?count={count}"
    if subject:
        query += f"&subject={urllib.parse.quote(subject)}"
    request = urllib.request.Request(query, headers={"Authorization": f"Bearer {token}"})
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return json.load(response)
    except urllib.error.HTTPError as e:
        sys.exit(f"{e.code} from {query}\n"
                 f"  401/403 — check the token; it is generated on first startup into "
                 f".companion-api-token beside the database.")
    except urllib.error.URLError as e:
        sys.exit(f"cannot reach {url}: {e.reason}")


def existing_reviewed(decision):
    """Text already labelled by a human, so re-running never asks the same question twice."""
    path = CORPUS / f"{decision}.reviewed.jsonl"
    if not path.exists():
        return set()
    return {json.loads(line)["text"] for line in path.open(encoding="utf-8") if line.strip()}


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--url", default="http://localhost:5000")
    parser.add_argument("--token-file", default=".companion-api-token")
    parser.add_argument("--token")
    parser.add_argument("--subject", help="one decision, e.g. memory.unfinished; default all")
    parser.add_argument("--count", type=int, default=5000)
    args = parser.parse_args()

    token = args.token
    if not token:
        path = pathlib.Path(args.token_file)
        if not path.exists():
            sys.exit(f"no token at {path} — pass --token, or point --token-file at the file the "
                     f"API generated on first startup")
        token = path.read_text(encoding="utf-8").strip()

    rows = fetch(args.url, token, args.subject, args.count)
    if not rows:
        print("no captures. Is CognitiveModels:Capture switched on, and has anyone talked to her?")
        return

    CORPUS.mkdir(parents=True, exist_ok=True)
    grouped = collections.defaultdict(list)
    for row in rows:
        grouped[row["subject"]].append(row)

    print(f"{'decision':<24} {'rows':>6} {'redacted':>9} {'dupes':>6} {'queued':>7}   "
          f"heuristic fired")
    for decision, captured in sorted(grouped.items()):
        reviewed = existing_reviewed(decision)
        seen, queue, redacted, dupes = set(), [], 0, 0
        for row in captured:
            text = row.get("input")
            if not text:
                redacted += 1
                continue
            if text in reviewed or text in seen:
                dupes += 1
                continue
            seen.add(text)
            queue.append({
                "text": text,
                # Null on purpose. See the module docstring: a corpus labelled by the rule it is
                # meant to judge can only ever conclude that the rule was right.
                "label": None,
                "decision": decision,
                # One family per sentence: a real sentence is its own phrasing, and pretending
                # otherwise would let the family-macro metric count one utterance many times.
                "family": f"captured:{text[:60]}",
                "difficulty": 0,
                "source": "real_conversation",
                "generator": "capture-1",
                "heuristic": row.get("legacy") == "true",
            })

        path = CORPUS / f"{decision}.captured.jsonl"
        with path.open("w", encoding="utf-8") as out:
            for item in queue:
                out.write(json.dumps(item, ensure_ascii=False) + "\n")

        fired = sum(1 for row in captured if row.get("legacy") == "true")
        print(f"{decision:<24} {len(captured):>6} {redacted:>9} {dupes:>6} {len(queue):>7}   "
              f"{fired}/{len(captured)} = {fired / len(captured):.1%}")

    print()
    print(f"queues written to {CORPUS}/<decision>.captured.jsonl")
    print("label them by setting \"label\" to true or false, then save as <decision>.reviewed.jsonl.")
    print()
    print("The 'heuristic fired' column is the conversational base rate this project has been")
    print("assuming rather than measuring. Every precision estimate in the docs uses 3 %; if that")
    print("column says something else, those estimates are wrong and this is the correction.")


if __name__ == "__main__":
    main()
