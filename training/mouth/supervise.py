"""Run the trainer until it actually finishes, restarting it when the GPU driver takes it out.

This machine resets its display driver under sustained load. It did so 13 minutes into the first
Run-2 attempt, mid-validation, and the Windows event log recorded an nvlddmkm error while the
Python process exited with status 0 - so neither the exit code nor the absence of a traceback
tells you anything. The only honest completion signal is the trainer's own "done" or "early-stop"
event in its log.

That is what this waits for. Everything that makes it safe already exists in the trainer: the
per-epoch permutation is reseeded from SEED + epoch so example order is exact, and the RNG state
travels inside the checkpoint so dropout masks continue rather than diverge. A restart here is a
continuation of the same run, not a new sample of it - which is the claim the freeze manifest
makes and the reason the resume path was built before it was needed.

Each restart is recorded in the training log, so the run's history includes its own interruptions
rather than presenting a clean line that never happened.
"""
import io
import json
import os
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).parent
RUN_ID = os.environ.get("RUN_ID", "run-2")
LOG = ROOT / "runs" / RUN_ID / "training-log.jsonl"
MAX_RESTARTS = 60
STALL_LIMIT = 4          # consecutive attempts with no progress before this is a real fault
SETTLE_SECONDS = 45      # base wait after a failure; doubles while nothing progresses


def finished():
    """Did the trainer reach a terminal state of its own accord?"""
    if not LOG.exists():
        return False
    for line in io.open(LOG, encoding="utf-8"):
        line = line.strip()
        if not line:
            continue
        try:
            event = json.loads(line).get("event")
        except json.JSONDecodeError:
            continue
        if event in ("done", "early-stop"):
            return True
    return False


def last_step():
    step = 0
    if LOG.exists():
        for line in io.open(LOG, encoding="utf-8"):
            try:
                step = max(step, json.loads(line).get("step") or 0)
            except json.JSONDecodeError:
                pass
    return step


def note(**kw):
    LOG.parent.mkdir(parents=True, exist_ok=True)
    with io.open(LOG, "a", encoding="utf-8") as f:
        f.write(json.dumps(kw) + "\n")
    print("  " + json.dumps(kw), flush=True)


def main():
    python = sys.executable
    trainer = str(ROOT / os.environ.get("TRAINER", "train_run2.py"))

    stalled = 0
    for attempt in range(1, MAX_RESTARTS + 1):
        before = last_step()
        print(f"\n=== attempt {attempt} (from step {before}) ===", flush=True)
        result = subprocess.run([python, trainer])

        if finished():
            print(f"trainer reported completion on attempt {attempt}")
            return 0

        after = last_step()
        if after <= before:
            stalled += 1
        else:
            stalled = 0

        # A driver that has just reset needs longer than a process does. Two attempts failed
        # here with a bitsandbytes "initialization error" simply because they started twenty
        # seconds after a TDR - the same optimizer initialised fine once the driver had settled.
        # So no-progress is met with a longer wait before it is treated as a real fault.
        if stalled >= STALL_LIMIT:
            note(event="supervisor-abort", attempt=attempt, step=after, stalled=stalled,
                 reason="no progress across %d consecutive attempts" % stalled)
            print("no progress across %d attempts; stopping rather than looping" % stalled)
            return 1

        backoff = SETTLE_SECONDS * (2 ** min(stalled, 3))
        note(event="supervisor-restart", attempt=attempt,
             stepBefore=before, stepAfter=after, exitCode=result.returncode,
             stalled=stalled, backoffSec=backoff,
             reason="trainer exited without a terminal event")
        time.sleep(backoff)

    note(event="supervisor-exhausted", attempts=MAX_RESTARTS, step=last_step())
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
