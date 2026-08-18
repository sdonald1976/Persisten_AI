"""Fine-tunes a small sentence encoder for one cognitive decision, and exports it to ONNX.

    pip install -r training/requirements-encoder.txt
    python training/cognition/finetune_encoder.py memory.unfinished
    python training/cognition/finetune_encoder.py memory.unfinished --base sentence-transformers/all-MiniLM-L6-v2

Produces models/<decision>.onnx and models/<decision>.vocab.txt, which is what
CognitiveModelOptions already expects — the C# side loads these through Microsoft.ML.OnnxRuntime
with no Python at runtime, exactly as the Phase 3 cross-encoder does.

--------------------------------------------------------------------------------------------
WHY THIS EXISTS

The classifier measured in docs/SPECIALIST_MODELS.md is 1,113 parameters: character n-grams and a
linear boundary. It is blind to the thing these judgements turn on —

    cosine("I have to do the roof", "the roof I have to do") = 1.000

word order is not down-weighted, it is not represented. So every error it makes is the same error:

    said yes - I thought I'd have to do the roof but I didn't
    said yes - we cancelled the migration
    said yes - would I need to do the wiring first

An encoder reads those. That is the entire hypothesis, and it is a hypothesis rather than a claim
until this has been run: a 22M-parameter model on a few hundred families can overfit spectacularly,
and the honest expectation is that it wins on memory.unfinished and is still short of data
everywhere else until the borrowed corpora in training/datasets are in.

RUN crossval.py AFTERWARDS, NOT INSTEAD. This script fits and exports. It does not decide anything.
The comparison that matters is the same grouped cross-validation and paired bootstrap the linear
model was held to, on the same families, and a model that skips that is a model adopted for being
newer.

NOTHING HERE HAS BEEN EXECUTED. The session that wrote it had no route to Hugging Face, so the
training loop and the ONNX export are both unverified. Treat the first run as debugging.
"""
import argparse, collections, json, pathlib, sys

# Windows consoles default to cp1252, and these scripts (and torch's own exporter) print em-dashes
# and the odd emoji. Encoding is not cosmetic here: on the first real run torch.onnx.export
# captured the graph successfully and then died with UnicodeEncodeError writing its own success
# message, which reads exactly like a failed export. Reconfigured rather than left to the caller
# to set PYTHONIOENCODING, because the failure names the wrong culprit.
for _stream in (sys.stdout, sys.stderr):
    if hasattr(_stream, "reconfigure"):
        _stream.reconfigure(encoding="utf-8", errors="replace")

CORPUS = pathlib.Path(__file__).resolve().parents[1] / "corpus"
MODELS = pathlib.Path(__file__).resolve().parents[2] / "models"

# 22M parameters, Apache-2.0, the same family as the cross-encoder already measured at ~25 ms per
# call on CPU in the live app. Small on purpose: the GPU this project runs on is a 6 GB 1660 already
# thrashing between generative models, and specialist inference belongs on the CPU beside them.
DEFAULT_BASE = "sentence-transformers/all-MiniLM-L6-v2"


def load(decision):
    """Every row for one decision, from all four sources, each keeping its provenance.

    Generated, borrowed and human-reviewed rows are trained on together and grouped by family, which
    is the only reason they can be: a family is "the thing that must not appear on both sides of a
    split", and it means the same thing whether it came from a template, a research corpus or a real
    conversation.
    """
    rows, seen = [], collections.Counter()
    for suffix in ("train", "validation", "test", "borrowed", "reviewed"):
        path = CORPUS / f"{decision}.{suffix}.jsonl"
        if not path.exists():
            continue
        for line in path.open(encoding="utf-8"):
            if not line.strip():
                continue
            row = json.loads(line)
            if row.get("label") is None:
                continue          # captured-but-unlabelled rows are a review queue, not training data
            rows.append(row)
            seen[row.get("source", "unknown")] += 1
    return rows, seen


def fit(train, base=DEFAULT_BASE, epochs=4, batch=16, lr=2e-5, max_len=128, seed=1, quiet=False,
        classes=None):
    """Fine-tunes `base` on training rows and returns (model, tokenizer, device).

    Lifted out of main() so that crossval.py can fit the same model per fold instead of carrying a
    second copy of this loop. Two implementations of the thing under test is the failure this repo
    already removed once, in the other direction: the incumbent's verdict is stamped into the data
    rather than transcribed into Python, because a baseline that drifts from the code it claims to
    measure silently stops being a comparison. An encoder scored by a training loop that has drifted
    from the one that produced the shipped weights is the same mistake wearing different clothes.
    """
    import torch
    from torch.utils.data import DataLoader, Dataset
    from transformers import AutoModelForSequenceClassification, AutoTokenizer

    torch.manual_seed(seed)
    tokenizer = AutoTokenizer.from_pretrained(base)

    # `classes` widens the same loop to a multi-class task (the supersession pair taxonomy) instead
    # of a second trainer growing beside this one. Binary callers change nothing: labels stay the
    # row's boolean. With classes, the row's label is the class NAME and the id is its index — the
    # order is the caller's contract, and the caller writes it into the artifact manifest.
    num_labels = len(classes) if classes else 2
    label_id = (lambda r: classes.index(r["label"])) if classes else (lambda r: int(r["label"]))
    model = AutoModelForSequenceClassification.from_pretrained(base, num_labels=num_labels)

    class Rows(Dataset):
        def __init__(self, items): self.items = items
        def __len__(self): return len(self.items)
        def __getitem__(self, i):
            r = self.items[i]
            # " </s> " is the pair separator the adapters emit for the two-sentence decisions
            # (supersession, assertion). Split here so a pair is encoded as a pair rather than as
            # one long sentence with a stray token in the middle.
            parts = r["text"].split(" </s> ", 1)
            enc = tokenizer(*parts, truncation=True, max_length=max_len,
                            padding="max_length", return_tensors="pt")
            item = {k: v.squeeze(0) for k, v in enc.items()}
            item["labels"] = torch.tensor(label_id(r))
            return item

    # CPU on purpose. The GPU here is a 6 GB 1660 already thrashing between generative models, and
    # §3.5 of the design document says specialist inference belongs beside them on the CPU rather
    # than competing for VRAM with the conversation.
    device = "cuda" if torch.cuda.is_available() else "cpu"
    if not quiet:
        print(f"training on {device}")
    model.to(device).train()

    # Weighted, because these corpora are nowhere near balanced and an unweighted loss on a 3 %
    # positive set learns to say no. The generated corpus is the opposite problem at 68 % positive;
    # either way the weight is computed rather than assumed.
    counts = [0] * num_labels
    for r in train:
        counts[label_id(r)] += 1
    weight = torch.tensor(
        [len(train) / (num_labels * max(1, c)) for c in counts], device=device, dtype=torch.float)
    if not quiet:
        print(f"class weights {[round(w, 2) for w in weight.tolist()]} from counts {counts}")

    loader = DataLoader(Rows(train), batch_size=batch, shuffle=True)
    optimiser = torch.optim.AdamW(model.parameters(), lr=lr)
    loss_fn = torch.nn.CrossEntropyLoss(weight=weight)

    steps = int(len(loader) * epochs)
    step = 0
    while step < steps:
        for batch_in in loader:
            if step >= steps:
                break
            labels = batch_in.pop("labels").to(device)
            out = model(**{k: v.to(device) for k, v in batch_in.items()})
            loss = loss_fn(out.logits, labels)
            loss.backward()
            optimiser.step()
            optimiser.zero_grad()
            step += 1
            if step % 25 == 0 and not quiet:
                print(f"   step {step}/{steps}  loss {loss.item():.4f}")
    return model, tokenizer, device


def predict(model, tokenizer, rows, max_len=128, device=None, chunk_size=64, full=False):
    """P(label=1) per row — or, with full=True, the whole probability vector per row.

    Same encoding as fit, for the same anti-drift reason. `full` exists for the multi-class
    callers; binary callers keep receiving the positive-class scalar they always did."""
    import torch

    device = device or next(model.parameters()).device
    model.eval()
    probabilities = []
    with torch.no_grad():
        for start in range(0, len(rows), chunk_size):
            chunk = rows[start:start + chunk_size]
            pairs = [r["text"].split(" </s> ", 1) for r in chunk]
            paired = all(len(p) > 1 for p in pairs)
            enc = tokenizer([p[0] for p in pairs],
                            [p[1] for p in pairs] if paired else None,
                            truncation=True, max_length=max_len, padding="max_length",
                            return_tensors="pt")
            logits = model(**{k: v.to(device) for k, v in enc.items()}).logits
            soft = logits.softmax(-1)
            probabilities.extend(soft.tolist() if full else soft[:, 1].tolist())
    return probabilities


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("decision", help="e.g. memory.unfinished")
    parser.add_argument("--base", default=DEFAULT_BASE)
    parser.add_argument("--epochs", type=float, default=4)
    parser.add_argument("--batch", type=int, default=16)
    parser.add_argument("--lr", type=float, default=2e-5)
    parser.add_argument("--max-len", type=int, default=128)
    parser.add_argument("--holdout-families", type=float, default=0.2)
    parser.add_argument("--seed", type=int, default=1)
    parser.add_argument("--out", default=None)
    args = parser.parse_args()

    rows, sources = load(args.decision)
    if not rows:
        sys.exit(f"no rows for {args.decision} in {CORPUS}. Generate: "
                 f"dotnet run --project tools/Companion.Eval -- --only corpus --out training/corpus")

    families = sorted({r["family"] for r in rows})
    print(f"{len(rows)} rows / {len(families)} families")
    for source, n in sources.most_common():
        print(f"   {n:>7} {source}")
    if sources.get("synthetic", 0) == sum(sources.values()):
        print("   ALL SYNTHETIC — a win here is a win against one person's templates. See "
              "training/datasets/fetch.py and training/cognition/harvest.py")

    # Held out BY FAMILY, seeded, before anything is loaded. Splitting by row would put one filler
    # of a template in training and another in test and score memorisation; this project has already
    # published one conclusion that way and had to retract it.
    import random
    rng = random.Random(args.seed)
    shuffled = families[:]
    rng.shuffle(shuffled)
    cut = max(1, int(len(shuffled) * args.holdout_families))
    heldout = set(shuffled[:cut])
    train = [r for r in rows if r["family"] not in heldout]
    test = [r for r in rows if r["family"] in heldout]
    print(f"train {len(train)} rows / {len(families) - len(heldout)} families   "
          f"held out {len(test)} rows / {len(heldout)} families")
    if len(heldout) < 10:
        print("   fewer than ten held-out families: the score will be dominated by which ones "
              "landed there. Read it as a smoke test, not a result.")

    import numpy as np
    import torch

    model, tokenizer, device = fit(
        train, base=args.base, epochs=args.epochs, batch=args.batch, lr=args.lr,
        max_len=args.max_len, seed=args.seed)

    # ---- the held-out families, by family, which is the metric everything else here uses --------
    by_family = collections.defaultdict(list)
    for row, probability in zip(test, predict(model, tokenizer, test, args.max_len, device)):
        by_family[row["family"]].append((row["label"], probability >= 0.5))

    tp = fp = fn = 0
    for calls in by_family.values():
        truth = calls[0][0]
        called = sum(c for _, c in calls) * 2 >= len(calls)
        tp += truth and called
        fp += called and not truth
        fn += truth and not called
    precision = tp / (tp + fp) if tp + fp else 0.0
    recall = tp / (tp + fn) if tp + fn else 0.0
    f1 = 2 * precision * recall / (precision + recall) if precision + recall else 0.0
    print()
    print(f"held-out families: P={precision:.3f} R={recall:.3f} F1={f1:.3f} "
          f"over {len(by_family)} families")
    print("This is ONE draw. Run crossval.py for the interval — a single split is what produced "
          "the retracted +0.322 headline.")

    # ---- ONNX, which is what production actually loads ------------------------------------------
    #
    # Three things went wrong here on the first real run, and all three produced a file that looked
    # like a successful export:
    #
    #   * the weights went to a 90 MB `.onnx.data` sidecar and the graph came out at 0.8 MB. A
    #     22M-parameter model is ~90 MB; a 0.8 MB one is a graph with the numbers missing. It would
    #     have loaded here, beside its sidecar, and failed the moment the .onnx was copied alone.
    #     external_data=False keeps it in one file, which is what "ships a model" should mean.
    #   * transformers 5 wrote `<decision>-tokenizer.model` where the C# BertLikeTokenizer reads
    #     `vocab.txt` beside the model, so the model would have reported itself unavailable at
    #     startup with the file sitting right there. Written explicitly below, in id order, which is
    #     the format that side parses.
    #   * nothing checked that the exported graph answers the same as the model that was trained.
    #     requirements-encoder.txt lists onnxruntime "for verifying the export before trusting it in
    #     C#" and nothing verified anything.
    MODELS.mkdir(parents=True, exist_ok=True)
    out = pathlib.Path(args.out) if args.out else MODELS / f"{args.decision}.onnx"
    sample = tokenizer("a sentence", "another", truncation=True, max_length=args.max_len,
                       padding="max_length", return_tensors="pt")
    names = [k for k in ("input_ids", "attention_mask", "token_type_ids") if k in sample]
    inputs = tuple(sample[k].to(device) for k in names)
    torch.onnx.export(
        model, inputs, str(out),
        input_names=names, output_names=["logits"],
        dynamic_axes={n: {0: "batch", 1: "sequence"} for n in names} | {"logits": {0: "batch"}},
        opset_version=14, external_data=False)

    # The vocabulary travels with the model, under the name the loader looks for. One token per
    # line, line number is the id — a model whose vocabulary is a different file's scores plausible
    # nonsense rather than failing, so this is written from the tokenizer that just trained.
    vocab = tokenizer.get_vocab()
    vocab_path = out.with_name("vocab.txt")
    with vocab_path.open("w", encoding="utf-8") as handle:
        for token, _ in sorted(vocab.items(), key=lambda pair: pair[1]):
            print(token, file=handle)

    print(f"exported {out} ({out.stat().st_size / 1e6:.1f} MB) and {vocab_path.name} "
          f"({len(vocab)} tokens)")

    # ---- does the exported graph agree with the model that was trained? -------------------------
    #
    # Compared on real held-out rows rather than on the dummy sample, because a graph can be right
    # about one input and wrong about padding, token types or sequence length. Any disagreement is
    # fatal: the whole point of ONNX here is that C# gets the same answers Python measured, and a
    # model that quietly disagrees would be adopted on a score it does not reproduce.
    import onnxruntime

    session = onnxruntime.InferenceSession(str(out), providers=["CPUExecutionProvider"])
    probe = [r["text"] for r in (test or train)[:32]]
    pairs = [t.split(" </s> ", 1) for t in probe]
    enc = tokenizer([p[0] for p in pairs],
                    [p[1] for p in pairs] if all(len(p) > 1 for p in pairs) else None,
                    truncation=True, max_length=args.max_len, padding="max_length",
                    return_tensors="pt")
    with torch.no_grad():
        expected = model(**{k: v.to(device) for k, v in enc.items()}).logits.cpu().numpy()
    actual = session.run(["logits"], {n: enc[n].cpu().numpy() for n in names})[0]

    drift = float(np.abs(expected - actual).max())
    agree = int((expected.argmax(-1) == actual.argmax(-1)).sum())
    print(f"onnxruntime vs torch on {len(probe)} held-out rows: "
          f"max logit difference {drift:.2e}, same answer on {agree}/{len(probe)}")
    if agree != len(probe) or drift > 1e-3:
        sys.exit(
            "THE EXPORT DOES NOT REPRODUCE THE MODEL. The scores measured above are not the scores "
            "C# would get, so this file must not be enabled. Check the input names, the padding and "
            "the opset before trusting anything downstream.")

    print()
    print("Enable it in appsettings.json under CognitiveModels:Classifier, and leave ShadowMode on "
          "until the disagreements have been read. A model that is present is not a model that has "
          "earned the decision.")


if __name__ == "__main__":
    main()
