#!/bin/bash
# Regenerate fixture transcripts for base/1a/1b arms (baseline-results.md was
# overwritten per-arm during the four-arm eval; stdout kept only pass/fail).
set -u
cd /c/Source/Persisten_AI/training/renderer
PY=../.venv-train/Scripts/python.exe
OUT=runs/run-1c

for spec in "base:" "1a:runs/run-1a/adapter-final" "1b:runs/run-1b/adapter-final"; do
  name="${spec%%:*}"; adapter="${spec#*:}"
  echo "=== BENCH $name ==="
  if [ -n "$adapter" ]; then
    $PY serve_tuned.py --adapter "$adapter" --port 11435 > /dev/null 2>&1 &
  else
    $PY serve_tuned.py --port 11435 > /dev/null 2>&1 &
  fi
  srv=$!
  n=0
  until curl -s -o /dev/null http://localhost:11435/api/ps; do
    sleep 15; n=$((n+1))
    [ $n -gt 100 ] && { echo "BENCH $name: server never up"; kill $srv 2>/dev/null; exit 1; }
    kill -0 $srv 2>/dev/null || { echo "BENCH $name: server died"; exit 1; }
  done
  ( cd /c/Source/Persisten_AI && dotnet run --project tools/Companion.RendererBench -c Release -- --ollama http://localhost:11435 ) > /dev/null 2>&1
  cp baseline-results.md "$OUT/bench-$name.md"
  git -C /c/Source/Persisten_AI checkout -- training/renderer/baseline-results.md
  kill $srv 2>/dev/null; wait $srv 2>/dev/null; sleep 5
  echo "BENCH $name: done"
done
echo "=== REBENCH COMPLETE ==="
