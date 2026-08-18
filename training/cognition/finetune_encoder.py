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
    from torch.utils.data import DataLoader, Dataset
    from transformers import AutoModelForSequenceClassification, AutoTokenizer

    tokenizer = AutoTokenizer.from_pretrained(args.base)
    model = AutoModelForSequenceClassification.from_pretrained(args.base, num_labels=2)

    class Rows(Dataset):
        def __init__(self, items): self.items = items
        def __len__(self): return len(self.items)
        def __getitem__(self, i):
            r = self.items[i]
            # " </s> " is the pair separator the adapters emit for the two-sentence decisions
            # (supersession, assertion). Split here so a pair is encoded as a pair rather than as
            # one long sentence with a stray token in the middle.
            parts = r["text"].split(" </s> ", 1)
            enc = tokenizer(*parts, truncation=True, max_length=args.max_len,
                            padding="max_length", return_tensors="pt")
            item = {k: v.squeeze(0) for k, v in enc.items()}
            item["labels"] = torch.tensor(int(r["label"]))
            return item

    device = "cuda" if torch.cuda.is_available() else "cpu"
    print(f"training on {device}")
    model.to(device).train()

    # Weighted, because these corpora are nowhere near balanced and an unweighted loss on a 3 %
    # positive set learns to say no. The generated corpus is the opposite problem at 68 % positive;
    # either way the weight is computed rather than assumed.
    positives = sum(r["label"] for r in train)
    weight = torch.tensor(
        [len(train) / (2 * max(1, len(train) - positives)), len(train) / (2 * max(1, positives))],
        device=device, dtype=torch.float)
    print(f"class weights {weight.tolist()} from {positives}/{len(train)} positive")

    loader = DataLoader(Rows(train), batch_size=args.batch, shuffle=True)
    optimiser = torch.optim.AdamW(model.parameters(), lr=args.lr)
    loss_fn = torch.nn.CrossEntropyLoss(weight=weight)

    steps = int(len(loader) * args.epochs)
    step = 0
    while step < steps:
        for batch in loader:
            if step >= steps:
                break
            labels = batch.pop("labels").to(device)
            out = model(**{k: v.to(device) for k, v in batch.items()})
            loss = loss_fn(out.logits, labels)
            loss.backward()
            optimiser.step()
            optimiser.zero_grad()
            step += 1
            if step % 25 == 0:
                print(f"   step {step}/{steps}  loss {loss.item():.4f}")

    # ---- the held-out families, by family, which is the metric everything else here uses --------
    model.eval()
    by_family = collections.defaultdict(list)
    with torch.no_grad():
        for batch_start in range(0, len(test), 64):
            chunk = test[batch_start:batch_start + 64]
            pairs = [r["text"].split(" </s> ", 1) for r in chunk]
            enc = tokenizer([p[0] for p in pairs],
                            [p[1] if len(p) > 1 else None for p in pairs] if any(len(p) > 1 for p in pairs) else None,
                            truncation=True, max_length=args.max_len, padding=True, return_tensors="pt")
            logits = model(**{k: v.to(device) for k, v in enc.items()}).logits
            for r, p in zip(chunk, logits.softmax(-1)[:, 1].tolist()):
                by_family[r["family"]].append((r["label"], p >= 0.5))

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
    MODELS.mkdir(parents=True, exist_ok=True)
    out = pathlib.Path(args.out) if args.out else MODELS / f"{args.decision}.onnx"
    sample = tokenizer("a sentence", "another", truncation=True, max_length=args.max_len,
                       padding="max_length", return_tensors="pt")
    inputs = tuple(sample[k].to(device) for k in ("input_ids", "attention_mask", "token_type_ids")
                   if k in sample)
    names = [k for k in ("input_ids", "attention_mask", "token_type_ids") if k in sample]
    torch.onnx.export(
        model, inputs, str(out),
        input_names=names, output_names=["logits"],
        dynamic_axes={n: {0: "batch", 1: "sequence"} for n in names} | {"logits": {0: "batch"}},
        opset_version=14)

    # The vocabulary travels with the model. The C# tokenizers (BertLikeTokenizer,
    # RobertaLikeTokenizer) read it, and a model whose vocab is a different file's is a model that
    # scores plausible nonsense rather than failing.
    tokenizer.save_vocabulary(str(out.parent), filename_prefix=args.decision)
    print(f"exported {out} ({out.stat().st_size / 1e6:.1f} MB) and its vocabulary")
    print()
    print("Enable it in appsettings.json under CognitiveModels:Classifier, and leave ShadowMode on "
          "until the disagreements have been read. A model that is present is not a model that has "
          "earned the decision.")


if __name__ == "__main__":
    main()
