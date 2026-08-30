"""Drive real plan/4 turns through the served endpoint and measure what a turn costs.

The generations produced during evaluation were made in-process; a turn in the live path goes
over HTTP to serve_run2.py and pays for tokenization, transport and the server's own lock. This
measures that path, because that is the one a user waits on.

    python latency_probe.py --split test --n 60
"""
import argparse
import io
import json
import statistics
import time
import urllib.request
from pathlib import Path

ROOT = Path(__file__).parent
DATASET = ROOT / "dataset"


def post(endpoint, payload, timeout):
    body = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(
        endpoint + "/api/chat", data=body,
        headers={"Content-Type": "application/json"}, method="POST")
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.loads(r.read())


def get(endpoint, path):
    with urllib.request.urlopen(endpoint + path, timeout=30) as r:
        return json.loads(r.read())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--endpoint", default="http://127.0.0.1:11436")
    ap.add_argument("--split", default="test")
    ap.add_argument("--n", type=int, default=0, help="0 = every row")
    ap.add_argument("--timeout", type=int, default=180)
    ap.add_argument("--out", default=None)
    args = ap.parse_args()

    rows = [json.loads(l) for l
            in io.open(DATASET / f"mouth-v2-{args.split}.jsonl", encoding="utf-8-sig") if l.strip()]
    if args.n:
        rows = rows[:args.n]

    identity = get(args.endpoint, "/api/identity")
    print(f"endpoint adapter {identity['adapterSha256'][:16]}...  "
          f"cold start {identity.get('coldStartSec')}s")

    # One warm-up call, excluded from the statistics. The first request after load pays for
    # CUDA graph capture and allocator growth that no later turn pays again, and folding it into
    # p50 would describe a cost users meet once.
    post(args.endpoint, {"model": "run-2", "stream": False,
                         "options": {"temperature": 0.0, "num_predict": 8},
                         "messages": [{"role": "system", "content": rows[0]["system"]},
                                      {"role": "user", "content": rows[0]["input"]}]}, args.timeout)

    latencies, generations, failures = [], [], 0
    started = time.perf_counter()
    for i, r in enumerate(rows):
        payload = {
            "model": "run-2", "stream": False,
            "options": {"temperature": 0.0, "num_predict": 220},
            "messages": [{"role": "system", "content": r["system"]},
                         {"role": "user", "content": r["input"]}],
        }
        t0 = time.perf_counter()
        try:
            resp = post(args.endpoint, payload, args.timeout)
        except Exception as e:
            failures += 1
            print(f"  {i + 1}: FAILED {type(e).__name__}")
            continue
        latencies.append((time.perf_counter() - t0) * 1000)
        generations.append({"id": r["id"], "target": resp["message"]["content"].strip()})
        if (i + 1) % 25 == 0:
            print(f"  {i + 1}/{len(rows)}  p50 so far "
                  f"{statistics.median(latencies):.0f}ms", flush=True)

    ps = get(args.endpoint, "/api/ps")["models"][0]
    latencies.sort()
    q = lambda k: latencies[min(len(latencies) - 1, int(len(latencies) * k))]
    result = {
        "split": args.split,
        "calls": len(latencies),
        "failures": failures,
        "coldStartSec": identity.get("coldStartSec"),
        "latencyMs": {
            "min": round(latencies[0]),
            "p50": round(q(0.50)),
            "p95": round(q(0.95)),
            "max": round(latencies[-1]),
            "mean": round(statistics.fmean(latencies)),
        },
        "vram": {
            "currentBytes": ps.get("size_vram"),
            "peakBytes": ps.get("peak_vram"),
            "peakGiB": round((ps.get("peak_vram") or 0) / 2**30, 2),
        },
        "wallClockSec": round(time.perf_counter() - started, 1),
        "adapterSha256": identity["adapterSha256"],
    }

    out = Path(args.out or (ROOT / "evaluation" / f"latency-{args.split}.json"))
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")

    gen = out.parent / f"gen-run-2-served-{args.split}.jsonl"
    with io.open(gen, "w", encoding="utf-8", newline="\n") as f:
        for g in generations:
            f.write(json.dumps(g) + "\n")

    print(json.dumps(result, indent=2))
    print(f"-> {out}\n-> {gen}")


if __name__ == "__main__":
    main()
