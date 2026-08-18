"""The supersession task's label space, action mapping and input rendering.

One module owns these because three scripts (generate, assemble, train) and one review workflow
all have to agree on them exactly, and "SUPERSEDES" in one file against "supersedes" in another
would silently produce two datasets — the same near-miss the capture subjects are tested against.

The taxonomy is the decision as approved in docs/SUPERSESSION_TASK.md, not the incumbent's
vocabulary. The incumbent can only ever say DUPLICATE, SUPERSEDES or COEXIST; the four labels it
cannot express are the part of the decision that is currently not being made at all.
"""

# Order is the class-id order everywhere: generator, trainer, exported model. Appending is safe;
# reordering breaks every trained artifact, which is why the trainer writes the list into the
# model's manifest rather than assuming this file never changes.
LABELS = (
    "COEXIST",       # both true of this person at once -> store alongside
    "SUPERSEDES",    # was true, no longer is; the user's state changed -> archive old as history
    "CORRECTS",      # was never true; the record was wrong -> supersede/dispute, marked erroneous
    "REFINES",       # adds specificity without invalidating -> update value in place
    "DUPLICATE",     # same fact restated -> confirm, refresh recency
    "CONTRADICTS",   # cannot both hold and the turn does not resolve which way -> review queue
    "UNCERTAIN",     # genuinely ambiguous; forcing a label here would be labelling noise
)

LABEL_TO_ID = {label: i for i, label in enumerate(LABELS)}

# The production action each label proposes. Deployment metrics are computed at this level,
# because this is where a wrong answer has a cost: a wrong supersede-action buries a true fact,
# a wrong confirm-action silently suppresses a new one, a wrong add-action merely clutters.
SUPERSEDE_ACTIONS = frozenset({"SUPERSEDES", "CORRECTS"})
CONFIRM_ACTIONS = frozenset({"DUPLICATE"})
REVIEW_ACTIONS = frozenset({"CONTRADICTS", "UNCERTAIN"})


def age_bucket(days):
    """Coarse buckets rather than raw integers, so the encoder reads a token it has seen before.

    The boundary that matters is the first one: a conflict within a day or two of the original
    reads as a correction, a conflict years later reads as a change, and everything in between is
    genuinely less informative.
    """
    if days is None:
        return "age-unknown"
    if days < 2:
        return "age-same-day"
    if days < 14:
        return "age-days"
    if days < 90:
        return "age-weeks"
    if days < 365:
        return "age-months"
    return "age-years"


def render(row):
    """One pair row -> the text the encoder sees. The single place this mapping exists.

    Segment A carries the user's side — the utterance and the incoming fact — and segment B (after
    the " </s> " marker the tokenizer splits on) carries the existing memory plus the structured
    tags. The utterance leads because it is where replacement signals live; the tags trail because
    they are context, not content. Similarity is deliberately not rendered: it was measured unable
    to order these cases, and a feature that misleads is worse than none.
    """
    incoming = row["incoming"]
    existing = row["existing"]
    pair = row["pair"]
    utterance = (incoming.get("utterance") or "").strip()
    tags = " ".join((
        f"slot={incoming.get('predicate') or 'unknown'}",
        "one-value" if pair.get("single_valued") else "many-valued",
        "same-slot" if pair.get("same_slot") else "cross-slot",
        age_bucket(existing.get("age_days")),
    ))
    left = f"{utterance} => {incoming['fact']}" if utterance else incoming["fact"]
    return f"{left} </s> {existing['fact']} [{tags}]"


def is_valid(row):
    """The invariants every pair row must hold, whatever produced it."""
    return (
        row.get("decision") == "memory.supersession.pair"
        and (row.get("label") is None or row["label"] in LABELS)
        and isinstance(row.get("incoming"), dict) and row["incoming"].get("fact")
        and isinstance(row.get("existing"), dict) and row["existing"].get("fact")
        and isinstance(row.get("pair"), dict)
        and bool(row.get("family"))
    )
