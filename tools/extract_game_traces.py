"""Extracts a complete diagnosis bundle for one conversation from a companion.db.

Run ON THE MACHINE THAT PLAYED THE GAME:

  python tools/extract_game_traces.py --db src/Companion.Api/companion.db --like "%moving parts%"

Finds the conversation containing the matched text and writes
game-trace-bundle.json beside the db: every message in order, every
renderer.plan2 shadow/canary row in the conversation's time window (exact
serialized plan, both replies, Applied, violations, latency), every ToolCall,
every TurnRecord/Decision row, and any extracted memories in the window.

Personal identifiers are NOT stripped here — the bundle stays on your machines,
inside the same privacy boundary as the database itself. Anonymization for the
regression fixture happens at fixture-authoring time, by hand, per the existing
rules (docs/RENDERER_SHADOW.md §2).
"""
import argparse
import json
import sqlite3
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument("--db", required=True)
parser.add_argument("--like", required=True, help="SQL LIKE pattern matching any message in the game")
parser.add_argument("--pad-ticks", type=int, default=10_000_000_000, help="time padding around the conversation window")
args = parser.parse_args()

db = sqlite3.connect(f"file:{args.db}?mode=ro", uri=True)
db.row_factory = sqlite3.Row

hit = db.execute("SELECT ConversationId FROM Messages WHERE Content LIKE ? LIMIT 1", (args.like,)).fetchone()
if not hit:
    raise SystemExit(f"no message matches {args.like!r}")
cid = hit["ConversationId"]

msgs = [dict(r) for r in db.execute(
    "SELECT * FROM Messages WHERE ConversationId = ? ORDER BY Timestamp", (cid,))]
t0, t1 = msgs[0]["Timestamp"] - args.pad_ticks, msgs[-1]["Timestamp"] + args.pad_ticks

def grab(table, timecol="Timestamp"):
    try:
        return [dict(r) for r in db.execute(
            f"SELECT * FROM {table} WHERE {timecol} BETWEEN ? AND ? ORDER BY {timecol}", (t0, t1))]
    except sqlite3.OperationalError as e:
        return [{"_error": str(e)}]

bundle = {
    "conversationId": cid,
    "messages": msgs,
    "rendererRows": [dict(r) for r in db.execute(
        "SELECT * FROM ShadowComparisons WHERE Subject='renderer.plan2' AND Timestamp BETWEEN ? AND ? ORDER BY Timestamp",
        (t0, t1))],
    "toolCalls": grab("ToolCalls"),
    "turnRecords": grab("TurnRecords"),
    "decisions": grab("Decisions"),
    "modelCalls": grab("ModelCalls"),
    "memoriesExtracted": grab("SemanticMemories", "CreatedAt"),
    "procedures": [dict(r) for r in db.execute("SELECT * FROM Procedures")],
}

out = Path(args.db).parent / "game-trace-bundle.json"
out.write_text(json.dumps(bundle, indent=1, default=str), encoding="utf-8")
counts = {k: len(v) for k, v in bundle.items() if isinstance(v, list)}
print(f"wrote {out} — {counts}")
