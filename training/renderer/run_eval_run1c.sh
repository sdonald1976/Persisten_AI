#!/bin/bash
# Four-arm run-1c evaluation: base / run-1a / run-1b / run-1c through the same
# serve_tuned.py stack, scored by the unchanged eval scripts and C# bench.
set -u
cd /c/Source/Persisten_AI/training/renderer
PY=../.venv-train/Scripts/python.exe
OUT=runs/run-1c
mkdir -p "$OUT"

run_arm () {
  local name="$1"; shift
  local adapter="$1"; shift
  echo "=== ARM $name ==="
  if [ -n "$adapter" ]; then
    $PY serve_tuned.py --adapter "$adapter" --port 11435 > "$OUT/server-$name.log" 2>&1 &
  else
    $PY serve_tuned.py --port 11435 > "$OUT/server-$name.log" 2>&1 &
  fi
  local srv=$!
  # wait for the server (model load takes ~10-15 min)
  local n=0
  until curl -s -o /dev/null http://localhost:11435/api/ps; do
    sleep 15; n=$((n+1))
    if [ $n -gt 100 ]; then echo "ARM $name: server never came up"; kill $srv 2>/dev/null; return 1; fi
    if ! kill -0 $srv 2>/dev/null; then echo "ARM $name: server died during load"; return 1; fi
  done
  echo "ARM $name: server ready after $((n*15))s"
  $PY eval_val.py    --ollama http://localhost:11435 --out "$OUT/val-$name.jsonl"    > "$OUT/val-$name.summary.txt" 2>&1
  echo "ARM $name: val done"
  $PY eval_unseen.py --ollama http://localhost:11435 --out "$OUT/unseen-$name.jsonl" > "$OUT/unseen-$name.summary.txt" 2>&1
  echo "ARM $name: unseen done"
  ( cd /c/Source/Persisten_AI && dotnet run --project tools/Companion.RendererBench -c Release -- --ollama http://localhost:11435 ) > "$OUT/fixtures-$name.txt" 2>&1
  echo "ARM $name: fixtures done"
  kill $srv 2>/dev/null
  wait $srv 2>/dev/null
  sleep 10
}

run_arm base   ""
run_arm 1a     runs/run-1a/adapter-final
run_arm 1b     runs/run-1b/adapter-final
run_arm 1c     runs/run-1c/adapter-final
echo "=== ALL ARMS COMPLETE ==="
