#!/usr/bin/env python3
"""
Build a fine-tuning dataset straight from the companion's SQLite database.

No separate "export" step to think about — point this at your companion.db and it emits
chat-format JSONL ready for finetune.py.

The recommended dataset is `extraction`: your pipeline already produced validated,
evidence-backed (message -> memory) pairs as a byproduct of normal use, so this teaches a
small model to reproduce your exact extraction JSON. No thumbs-up feedback needed, low risk.

Guarantees:
  * Deleted memories are never included (respecting /forget).
  * Candidate (unreviewed) and Consolidated (system-generated) memories are excluded.
  * Only the user's own messages become training inputs.

Usage:
  python build_dataset.py --db /path/to/companion.db --out data/extraction.jsonl
  python build_dataset.py --db companion.db --dataset chat-sft --out data/chat_sft.jsonl
"""
import argparse
import json
import sqlite3
from collections import defaultdict

# Mirrors LlmMemoryExtractor's system prompt so the fine-tune targets the same task shape.
EXTRACTION_SYSTEM_PROMPT = (
    "You extract durable memories from a conversation. Return ONLY a JSON array. Each item: "
    '{"kind":"semantic"|"episodic", "subject":string?, "predicate":string?, "value":string?, '
    '"content":string, "validity":"Current"|"Temporary"|"Historical"?, '
    '"episodeStatus":"Occurred"|"Planned"|"InProgress"|"Resolved"?, "relatedProject":string?, '
    '"importance":0..1, "confidence":0..1, "excerpt":"the exact user words that support this"}. '
    "Only include things the user actually stated. Do not invent. If nothing is worth remembering, return []."
)

EXCLUDED_STATUSES = ("Deleted", "Candidate")


def build_extraction(conn):
    """One example per source message: (user text) -> JSON array of the candidates it produced."""
    messages = {
        row["Id"]: row
        for row in conn.execute("SELECT Id, Content, Role FROM Messages")
    }

    by_message = defaultdict(list)

    # Semantic memories (skip deleted/candidate, and consolidated system-generated ones).
    sem = conn.execute(
        "SELECT Id, Subject, Predicate, Value, NormalizedFact, Validity, Importance, Confidence, RelatedProject "
        "FROM SemanticMemories "
        "WHERE Status NOT IN (?, ?) AND Origin != 'Consolidated'",
        EXCLUDED_STATUSES,
    )
    for m in sem:
        msg_id, excerpt = source_of(conn, m["Id"])
        if msg_id is None:
            continue
        by_message[msg_id].append({
            "kind": "semantic",
            "subject": m["Subject"],
            "predicate": m["Predicate"],
            "value": m["Value"],
            "content": m["NormalizedFact"],
            "validity": m["Validity"],
            "relatedProject": m["RelatedProject"],
            "importance": round(m["Importance"], 2),
            "confidence": round(m["Confidence"], 2),
            "excerpt": excerpt,
        })

    # Episodic memories.
    epi = conn.execute(
        "SELECT Id, Description, EpisodeStatus, Importance, Confidence, RelatedProject "
        "FROM EpisodicMemories WHERE Status NOT IN (?, ?)",
        EXCLUDED_STATUSES,
    )
    for m in epi:
        msg_id, excerpt = source_of(conn, m["Id"])
        if msg_id is None:
            continue
        by_message[msg_id].append({
            "kind": "episodic",
            "content": m["Description"],
            "episodeStatus": m["EpisodeStatus"],
            "relatedProject": m["RelatedProject"],
            "importance": round(m["Importance"], 2),
            "confidence": round(m["Confidence"], 2),
            "excerpt": excerpt,
        })

    examples = []
    for msg_id, candidates in by_message.items():
        msg = messages.get(msg_id)
        if msg is None or msg["Role"] != "User":
            continue
        # Drop null fields to keep targets clean.
        clean = [{k: v for k, v in c.items() if v is not None} for c in candidates]
        examples.append({
            "messages": [
                {"role": "system", "content": EXTRACTION_SYSTEM_PROMPT},
                {"role": "user", "content": msg["Content"]},
                {"role": "assistant", "content": json.dumps(clean, ensure_ascii=False)},
            ]
        })
    return examples


def source_of(conn, memory_id):
    """The first (message id, excerpt) that supports a memory, or (None, None)."""
    row = conn.execute(
        "SELECT MessageId, Excerpt FROM Evidence WHERE MemoryId = ? LIMIT 1", (memory_id,)
    ).fetchone()
    return (row["MessageId"], row["Excerpt"]) if row else (None, None)


def build_chat_sft(conn):
    """
    EXPERIMENTAL: raw (user -> assistant) pairs. Lower quality than extraction because the
    context packet that was actually sent to the model is not stored, and replies aren't
    quality-filtered (no thumbs-up feedback). Prefer extraction until you capture ratings.
    """
    system = (
        "You are a persistent AI companion that remembers the user across conversations. "
        "Reply helpfully and concisely, with continuity."
    )
    rows = list(conn.execute(
        "SELECT ConversationId, Role, Content, Timestamp FROM Messages ORDER BY ConversationId, Timestamp, Id"
    ))
    examples = []
    for i in range(len(rows) - 1):
        a, b = rows[i], rows[i + 1]
        if a["ConversationId"] == b["ConversationId"] and a["Role"] == "User" and b["Role"] == "Assistant":
            examples.append({
                "messages": [
                    {"role": "system", "content": system},
                    {"role": "user", "content": a["Content"]},
                    {"role": "assistant", "content": b["Content"]},
                ]
            })
    return examples


def main():
    parser = argparse.ArgumentParser(description="Build a fine-tuning dataset from companion.db")
    parser.add_argument("--db", default="companion.db", help="Path to the companion SQLite database")
    parser.add_argument("--dataset", choices=["extraction", "chat-sft"], default="extraction")
    parser.add_argument("--out", default="data/dataset.jsonl", help="Output JSONL path")
    args = parser.parse_args()

    conn = sqlite3.connect(args.db)
    conn.row_factory = sqlite3.Row

    examples = build_extraction(conn) if args.dataset == "extraction" else build_chat_sft(conn)

    import os
    os.makedirs(os.path.dirname(args.out) or ".", exist_ok=True)
    with open(args.out, "w", encoding="utf-8") as f:
        for ex in examples:
            f.write(json.dumps(ex, ensure_ascii=False) + "\n")

    print(f"Wrote {len(examples)} examples to {args.out} (dataset={args.dataset})")
    if args.dataset == "chat-sft":
        print("Note: chat-sft is experimental — no stored context packet and no quality filter.")


if __name__ == "__main__":
    main()
