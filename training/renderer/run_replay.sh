#!/bin/bash
# Four-arm provenance replay of one turn. Swaps the 11435 server per arm and
# restores the run-1c collection server at the end.
set -u
cd /c/Source/Persisten_AI/training/renderer
PY=../.venv-train/Scripts/python.exe
PLAN2="C:\\T\\claude\\C--Source-Persisten-AI\\e04944ba-319a-421a-b2f8-8154b7914d80\\scratchpad\\smoke\\replay-plan2.txt"
MSG="Finally fixed the squeaky hinge on the back door today. Thirty seconds of oil after three months of complaining."
OUT="C:\\T\\claude\\C--Source-Persisten-AI\\e04944ba-319a-421a-b2f8-8154b7914d80\\scratchpad\\smoke\\replay-results.jsonl"
: > "$OUT"

stop_11435() {
  for pid in $(netstat -ano | grep ":11435" | grep LISTENING | awk '{print $NF}' | sort -u); do
    taskkill //F //PID "$pid" 2>/dev/null
  done
  sleep 3
}

run_arm() {
  local name="$1" adapter="$2"
  stop_11435
  if [ -n "$adapter" ]; then
    $PY serve_tuned.py --adapter "$adapter" --port 11435 > /dev/null 2>&1 &
  else
    $PY serve_tuned.py --port 11435 > /dev/null 2>&1 &
  fi
  local srv=$!
  local n=0
  until curl -s -o /dev/null http://localhost:11435/api/ps; do
    sleep 10; n=$((n+1)); [ $n -gt 90 ] && { echo "ARM $name never up"; return 1; }
    kill -0 $srv 2>/dev/null || { echo "ARM $name server died"; return 1; }
  done
  $PY replay_provenance.py --arm "$name" --plan2 "$PLAN2" --message "$MSG" >> "$OUT"
  echo "ARM $name replayed"
  kill $srv 2>/dev/null; wait $srv 2>/dev/null
}

run_arm base ""
run_arm 1a runs/run-1a/adapter-final
run_arm 1b runs/run-1b/adapter-final
run_arm 1c runs/run-1c/adapter-final

# Restore the collection server.
stop_11435
$PY serve_tuned.py --adapter runs/run-1c/adapter-final --port 11435 > runs/run-1c/serve-shadow.log 2>&1 &
echo "REPLAY COMPLETE; collection server restored (pid $!)"
