"""Gate 7, plan/4: pick the held-out combination STRUCTURALLY.

Replaces `select_combination.py`, which detected primitives by substring-matching
plan/2 PROSE — literal phrases like "Ava made an error; Scott corrected her" and
"PALETTE". CompactV4 has no such prose to match. Its primitives are typed:
expression policies, render categories, register dimensions, source ids, frame
transitions. Matching text would silently find nothing and select a combination
that meant something else, which is worse than failing.

The selection rule is unchanged and is the point: enumerate every pair of
primitives the corpus contains INDIVIDUALLY but never TOGETHER, sort them, and
pick by the sha256 of the freeze manifest — a value committed before any
evaluation exists. The curator has seen the failures; discretion is therefore
removed rather than trusted.

Input is the native rows themselves (shadow envelopes or plan/4 JSON), never
their rendered text.
"""
import hashlib
import json
import sys
from pathlib import Path

ROOT = Path(__file__).parents[1]


def primitives(plan: dict) -> set[str]:
    """Every typed primitive one plan exhibits. Structure only — no text is read."""
    have: set[str] = set()

    for item in plan.get("items", []):
        # Expression policy and render category are closed sets, and they are what the
        # mouth is actually being asked to obey.
        if policy := item.get("policy"):
            have.add(f"policy:{policy}")
        if category := item.get("category"):
            have.add(f"category:{category}")
        if source := item.get("source"):
            have.add(f"source:{source}")
        # Privacy shape, which is a primitive in its own right.
        if item.get("disclosure") == "restricted":
            have.add("privacy:restricted")
        if (retention := item.get("retention")) and retention != "full":
            have.add(f"retention:{retention}")
        if item.get("supersededBy") or item.get("supersedes"):
            have.add("epistemic:supersession")

    if (question := plan.get("question", {}).get("policy")) is not None:
        have.add(f"question:{question}")

    # Register: a dimension counts as a primitive only when it is NOT at its canonical
    # default, because a default is the absence of a decision.
    defaults = {
        "warmth": "plain", "bluntness": "plain", "playfulness": "off", "teasing": "off",
        "skepticism": "off", "intensity": "even", "verbosity": "conversational",
        "profanity": "neutral", "mirror": False,
    }
    for dimension, default in defaults.items():
        value = plan.get("register", {}).get(dimension)
        if value is not None and value != default:
            have.add(f"register:{dimension}")

    # plan/4: the frame is a primitive family of its own.
    if frame := plan.get("frame"):
        have.add(f"frame:{frame.get('transition')}")
        have.add(f"frame-narration:{frame.get('narration', 'forbidden')}")
        if frame.get("boundaries"):
            have.add("frame:boundary")
        if (narrator := frame.get("narrator", {}).get("kind")) is not None:
            have.add(f"frame-narrator:{narrator}")

    # Composition depth: one source is a different task from four.
    sources = {item.get("source") for item in plan.get("items", []) if item.get("source")}
    if len(sources) >= 2:
        have.add(f"composition:{min(len(sources), 4)}")

    return have


def load(path: Path) -> list[dict]:
    """Accepts plan/4 JSON per line, or shadow envelopes carrying one."""
    plans = []
    for line in path.read_text(encoding="utf-8-sig").splitlines():
        line = line.strip()
        if not line:
            continue
        row = json.loads(line)
        plan = row.get("plan") or row.get("native") or row
        if isinstance(plan, dict) and "items" in plan:
            plans.append(plan)
    return plans


def select(plans: list[dict], manifest_sha: str) -> tuple[str, str]:
    """The pair the corpus never combines, chosen by a hash committed in advance."""
    seen_alone: set[str] = set()
    seen_together: set[tuple[str, str]] = set()

    for plan in plans:
        have = sorted(primitives(plan))
        seen_alone.update(have)
        for i, a in enumerate(have):
            for b in have[i + 1:]:
                seen_together.add((a, b))

    candidates = sorted(
        (a, b)
        for i, a in enumerate(sorted(seen_alone))
        for b in sorted(seen_alone)[i + 1:]
        if (a, b) not in seen_together
    )
    if not candidates:
        raise SystemExit("no unseen combination exists: every pair already co-occurs")

    index = int(hashlib.sha256(manifest_sha.encode()).hexdigest(), 16) % len(candidates)
    return candidates[index]


def main() -> None:
    if len(sys.argv) < 3:
        raise SystemExit(
            "usage: select_combination_v4.py <plan4-rows.jsonl> <freeze-manifest.json>")

    plans = load(Path(sys.argv[1]))
    if not plans:
        raise SystemExit("no plans found — cannot select a holdout from an empty corpus")

    manifest = Path(sys.argv[2]).read_bytes()
    manifest_sha = hashlib.sha256(manifest).hexdigest()

    a, b = select(plans, manifest_sha)
    print(json.dumps({
        "plans": len(plans),
        "manifestSha256": manifest_sha,
        "heldOutCombination": [a, b],
        "rule": "structural pair present individually, never together; index = sha256(manifest) % candidates",
    }, indent=2))


if __name__ == "__main__":
    main()
