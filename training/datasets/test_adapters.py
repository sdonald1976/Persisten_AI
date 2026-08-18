"""Offline tests for the mapping half of the borrowed corpora.

    python training/datasets/test_adapters.py

No network and no `datasets` install. That division is the point: these adapters were written from
published descriptions rather than against the files, so the download is unverified and the mapping
does not have to be. A mapping bug and a network failure should never look the same.

What is checked is mostly the label polarity and the group key, because those are the two things
that fail silently. An inverted label trains a model to say the opposite and still reports a
plausible F1; a wrong group key leaks and reports a flattering one.
"""
import sys

from adapters import (clinc_capability, commitment_bank, daily_dialog_commitment,
                      dialogue_nli, require)

failures = []


def check(name, condition, detail=""):
    if condition:
        print(f"  ok    {name}")
    else:
        print(f"  FAIL  {name}  {detail}")
        failures.append(name)


# The labels in this fixture are INVENTED to exercise the mapping. They are not claims about how
# DialogueNLI labels these sentences — that question is open and `fetch.py --audit` settles it
# against the real file. A test fixture that doubles as evidence about a corpus is how an
# unverified belief gets laundered into a fact.
print("dialogue-nli — contradiction is the supersession candidate, and the premise is the group")
rows = dialogue_nli([
    {"sentence1": "i have a dog", "sentence2": "i have no pets", "label": "contradiction"},
    {"sentence1": "i have a dog", "sentence2": "i love animals", "label": "entailment"},
    {"sentence1": "i play cello", "sentence2": "i dislike olives", "label": "neutral"},
    {"sentence1": "x", "sentence2": "y", "label": "-"},   # unlabelled rows are dropped, not guessed
])
check("three usable rows, the unlabelled one dropped", len(rows) == 3, [r["text"] for r in rows])
check("contradiction -> true", rows[0]["label"] is True)
check("entailment -> false", rows[1]["label"] is False)
check("neutral -> false", rows[2]["label"] is False)
check("premise is the family, so one persona cannot straddle a split",
      rows[0]["family"] == rows[1]["family"] and rows[0]["family"] != rows[2]["family"])
check("decision key matches the pipeline", rows[0]["decision"] == "memory.supersession")

print()
print("dialogue-nli — the Hub mirrors name the columns differently from the author's JSON")
hub = dialogue_nli([{"premise": "i have a dog", "hypothesis": "i have no pets",
                     "label": "contradiction"}])
check("premise/hypothesis works as well as sentence1/sentence2", hub[0]["label"] is True)
check("and produces the same text and family",
      hub[0]["text"] == "i have a dog </s> i have no pets"
      and hub[0]["family"] == "premise:i have a dog")

try:
    dialogue_nli([{"a": 1, "b": 2, "label": "contradiction"}])
    check("neither naming raises, naming both", False, "no error raised")
except ValueError as e:
    check("neither naming raises, naming both",
          "sentence1" in str(e) and "premise" in str(e))

print()
print("dialogue-nli — original_label wins, because the int column carries no schema to read")
both = dialogue_nli([{"sentence1": "a", "sentence2": "b", "label": 0,
                      "original_label": "contradiction"}])
check("original_label is preferred over the raw id", both[0]["label"] is True)

print()
print("dialogue-nli — an integer label is not decoded by guesswork")
coded = dialogue_nli([{"premise": "a", "hypothesis": "b", "label": 2}],
                     label_names=["entailment", "neutral", "contradiction"])
check("with names supplied, 2 -> contradiction", coded[0]["label"] is True)
try:
    dialogue_nli([{"premise": "a", "hypothesis": "b", "label": 2}])
    check("without names, it refuses rather than assuming an order", False, "no error raised")
except ValueError as e:
    check("without names, it refuses rather than assuming an order", "not guessed" in str(e))

print()
print("commitment-bank — both label encodings, because which you get depends where you downloaded it")
likert = commitment_bank([
    {"premise": "Did I ever tell you what timber I bought?", "hypothesis": "The speaker bought timber.", "label": 2.5},
    {"premise": "If I bought cedar, would it last?", "hypothesis": "The speaker bought cedar.", "label": -1.0},
], encoding="likert")
check("Likert +2.5 -> committed", likert[0]["label"] is True)
check("Likert -1.0 -> not committed", likert[1]["label"] is False)

# This is the case that caught the bug. The first version inferred the encoding from the value, so
# SuperGLUE's 0 (entailment, fully committed) was read as a Likert 0.0 (undecided) and the label
# came out inverted on the most important row in the set.
superglue = commitment_bank([
    {"premise": "a", "hypothesis": "b", "label": 0},              # entailment
    {"premise": "c", "hypothesis": "d", "label": "contradiction"},
], encoding="superglue")
check("SuperGLUE entailment -> committed", superglue[0]["label"] is True)
check("SuperGLUE contradiction -> not committed", superglue[1]["label"] is False)

try:
    commitment_bank([{"premise": "a", "hypothesis": "b", "label": 0}], encoding="guess")
    check("an unknown encoding is refused rather than guessed", False, "no error raised")
except ValueError as e:
    check("an unknown encoding is refused rather than guessed", "not inferable" in str(e))

print()
print("clinc150 — capability intents are the positives, and the intent is the group")
clinc = clinc_capability([
    {"text": "what can i ask you", "intent": "what_can_i_ask_you"},
    {"text": "so what are you able to help me with", "intent": "what_can_i_ask_you"},
    {"text": "are you able to come tomorrow", "intent": "schedule_meeting"},
    {"text": "what can you see from your window", "intent": "oos"},
])
check("capability intent -> true", clinc[0]["label"] is True and clinc[1]["label"] is True)
check("a request that merely starts 'are you able to' -> false", clinc[2]["label"] is False)
check("out of scope -> false", clinc[3]["label"] is False)
check("paraphrases of one intent share a family",
      clinc[0]["family"] == clinc[1]["family"] and clinc[0]["family"] != clinc[2]["family"])

# The two rows the shipped ToolNudge regex is recorded getting wrong are exactly this shape, which
# is the reason this dataset is worth borrowing rather than generating more templates.
check("integer labels resolve through the name list",
      clinc_capability([{"text": "hi", "intent": 3}], label_names=["a", "b", "c", "what_can_i_ask_you"])[0]["label"]
      is True)

print()
print("daily-dialog — commissive is the positive, one dialogue is one family")
dd = daily_dialog_commitment([
    {"dialog": ["Can you send it?", "Sure, I'll send it tonight.", ""], "act": [3, 4, 1]},
    {"dialog": ["It is raining."], "act": [1]},
])
check("two dialogues, empty utterance dropped", len(dd) == 3, [r["text"] for r in dd])
check("commissive -> true", dd[1]["label"] is True)
check("directive -> false", dd[0]["label"] is False)
check("inform -> false", dd[2]["label"] is False)
check("turns of one dialogue share a family",
      dd[0]["family"] == dd[1]["family"] and dd[0]["family"] != dd[2]["family"])

print()
print("schema drift fails loudly rather than producing an all-negative corpus")
# Not premise/hypothesis — those became valid when the Hub mirrors turned out to use them, and this
# assertion caught its own staleness on the next run, which is the behaviour wanted from it.
try:
    dialogue_nli([{"text_a": "a", "text_b": "b", "label": "contradiction"}])
    check("unrecognised columns raise, naming both accepted shapes", False, "no error raised")
except ValueError as e:
    check("unrecognised columns raise, naming both accepted shapes",
          "sentence1" in str(e) and "premise" in str(e), str(e)[:80])

try:
    daily_dialog_commitment([{"utterances": ["hi"], "acts": [1]}])
    check("a renamed column in another adapter raises too", False, "no error raised")
except ValueError as e:
    check("a renamed column in another adapter raises too", "dialog" in str(e))

print()
print("no row carries a heuristic verdict — the incumbent is scored by running it, not by guessing")
check("heuristic absent", all("heuristic" not in r for r in rows + likert + clinc + dd))

print()
print("fetch.audit — the bucketing, which is where three wrong verdicts came from")
# `fetch` imports only the standard library and `adapters` at module level, so these run with no
# network and no `datasets` install, exactly like the mapping tests above.
import fetch

# Every fixture below is INVENTED to exercise the arithmetic. None of it is a claim about how
# DialogueNLI labels anything — that question is settled by running --audit over the real file, and
# a fixture that doubles as evidence is how an unverified belief gets laundered into a fact.
def triple(relation, value):
    return ["i", relation, value]


def bucket(relation1, value1, relation2, value2):
    placed = fetch.bucket_of(triple(relation1, value1), triple(relation2, value2))
    return None if placed is None else placed[0]


check("same relation, same value is its own bucket",
      bucket("have_pet", "cat", "have_pet", "cat") == fetch.SAME_VALUE)
check("same relation, different kind of value is the decisive bucket",
      bucket("have_pet", "dog", "have_pet", "cat") == fetch.KIND)
check("same relation, different count of one value is arithmetic, not cardinality",
      bucket("have_pet", "2 dog", "have_pet", "5 dog") == fetch.COUNT)
check("a count against a bare value is still a count question",
      bucket("have_pet", "dog", "have_pet", "3 dog") == fetch.COUNT)
check("two unrelated relations are the control",
      bucket("have_pet", "dog", "has_profession", "teacher") == fetch.CONTROL)
check("a relation against its own negation is separated from the control",
      bucket("have_pet", "dog", "not_have", "pet") == fetch.ANTONYM)
check("dislike against a liking relation is separated too",
      bucket("dislike", "cat", "like_animal", "cat") == fetch.ANTONYM)

# The <none> case is what diluted the control: an unannotated sentence arrives looking like a
# relation called "<none>", so every one of them counted as a relation swap.
check("an unannotated side belongs in no bucket at all",
      fetch.bucket_of(triple("<none>", "<none>"), triple("have_pet", "cat")) is None)
check("a malformed triple belongs in no bucket either",
      fetch.bucket_of(["i", "have_pet"], triple("have_pet", "cat")) is None)

check("a leading integer is split off the value",
      fetch.leading_count("2 dog") == ("2", "dog") and fetch.leading_count("dog") == (None, "dog"))
check("a value that is only a number is left alone",
      fetch.leading_count("22") == (None, "22"))

print()
print("fetch.derive_label_names — the mirror's spelling is not consulted, only its construction")
import collections


def buckets_from(same_value, control, other=None):
    made = collections.defaultdict(collections.Counter)
    made[fetch.SAME_VALUE].update(same_value)
    made[fetch.CONTROL].update(control)
    made[fetch.KIND].update(other or {})
    return made


derived = fetch.derive_label_names(buckets_from(
    same_value={"positive": 100}, control={"neutral": 95, "negative": 5},
    other={"negative": 80, "neutral": 20}))
check("positive/negative/neutral is decoded without being named",
      derived == {"positive": "entailment", "neutral": "neutral", "negative": "contradiction"},
      derived)

# The failure this replaces: comparing against a literal string that the mirror never uses, so the
# count is structurally zero and the verdict cannot come out the other way.
canonical = fetch.derive_label_names(buckets_from(
    same_value={"entailment": 100}, control={"neutral": 95},
    other={"contradiction": 80}))
check("a mirror that already uses the canonical names maps to itself",
      canonical == {"entailment": "entailment", "neutral": "neutral",
                    "contradiction": "contradiction"})

check("an anchor that is not dominant refuses rather than picking a winner",
      fetch.derive_label_names(buckets_from(
          same_value={"positive": 55, "negative": 45}, control={"neutral": 95},
          other={"negative": 80})) is None)
check("two anchors landing on one label refuses",
      fetch.derive_label_names(buckets_from(
          same_value={"neutral": 100}, control={"neutral": 95}, other={"negative": 80})) is None)
check("a fourth label refuses, because elimination no longer names one class",
      fetch.derive_label_names(buckets_from(
          same_value={"positive": 100}, control={"neutral": 95},
          other={"negative": 40, "unknown": 40})) is None)
# If the strings say one thing and the construction says another, something is being misread. That
# is a failure, not a preference between two authorities.
check("canonical names that contradict the derivation refuse",
      fetch.derive_label_names(buckets_from(
          same_value={"contradiction": 100}, control={"neutral": 95},
          other={"entailment": 80})) is None)

print()
print("fetch.audit — every verdict branch, on fixtures built to reach it")
import contextlib, io


def rows_for(spec, anchors=True):
    """spec: {relation: {"neutral": n, "negative": n}} of same-relation/different-KIND pairs."""
    made = []
    for relation, counts in spec.items():
        for label, n in counts.items():
            for i in range(n):
                made.append({"triple1": triple(relation, f"kind{i}a"),
                             "triple2": triple(relation, f"kind{i}b"),
                             "original_label": label})
    if anchors:
        made += [{"triple1": triple("has_profession", "teacher"),
                  "triple2": triple("has_profession", "teacher"),
                  "original_label": "positive"} for _ in range(200)]
        made += [{"triple1": triple("have_pet", "cat"),
                  "triple2": triple("has_hobby", "chess"),
                  "original_label": "neutral"} for _ in range(200)]
        # A negated pair, so all three labels are actually observed. Derivation names the third
        # class by elimination, which it cannot do from a fixture that never uses it — and a real
        # corpus always does, in the negation-derived rows if nowhere else.
        made += [{"triple1": triple("have_pet", "dog"),
                  "triple2": triple("not_have", "pet"),
                  "original_label": "negative"} for _ in range(200)]
    return made


def verdict(spec, **kwargs):
    out = io.StringIO()
    with contextlib.redirect_stdout(out):
        fetch.audit("fixture", rows_for(spec, **kwargs))
    return out.getvalue()


bimodal = verdict({"has_profession": {"negative": 150},
                   "have_pet": {"neutral": 150}})
check("relations at both extremes and none between reads as cardinality",
      "BIMODAL, CLEANLY" in bimodal, bimodal[-200:])
check("and it names which side each relation fell on",
      "many-valued here:   have_pet" in bimodal and "single-valued here: has_profession" in bimodal)

# Elimination needs all three classes to appear somewhere. A corpus carrying only two cannot have
# the third named, and saying so is better than assuming which one is missing.
check("a corpus where one class never appears refuses",
      fetch.derive_label_names(buckets_from(
          same_value={"positive": 100}, control={"neutral": 95}, other={"neutral": 80})) is None)

coexist = verdict({"have_pet": {"neutral": 150}, "have_sibling": {"neutral": 150}})
check("everything neutral reads as usable whole", "Use it whole" in coexist, coexist[-200:])

conflict = verdict({"has_profession": {"negative": 150}, "gender": {"negative": 150}})
check("everything contradiction reads as sharing MNLI's problem",
      "shares MNLI's problem" in conflict, conflict[-200:])

muddled = verdict({"have_pet": {"neutral": 75, "negative": 75},
                   "misc": {"neutral": 75, "negative": 75}})
check("relations sitting in the middle read as the third and worst answer",
      "the third answer" in muddled, muddled[-200:])

# The control is the trust test: if unrelated relations are not neutral, the triples are being read
# wrong and no other number in the output means anything.
broken = io.StringIO()
with contextlib.redirect_stdout(broken):
    fetch.audit("fixture", [
        {"triple1": triple("has_profession", "teacher"), "triple2": triple("has_profession", "teacher"),
         "original_label": "positive"} for _ in range(200)] + [
        {"triple1": triple("have_pet", "cat"), "triple2": triple("has_hobby", "chess"),
         "original_label": "neutral" if i % 2 else "negative"} for i in range(200)])
check("a control that is not overwhelmingly neutral withholds everything",
      "THE CONTROL FAILED" in broken.getvalue() or "LABELS COULD NOT BE DERIVED" in broken.getvalue(),
      broken.getvalue()[-200:])

undecodable = io.StringIO()
with contextlib.redirect_stdout(undecodable):
    fetch.audit("fixture", [
        {"triple1": triple("have_pet", "dog"), "triple2": triple("have_pet", "cat"), "label": 2}
        for _ in range(50)])
check("integers with no names and no anchors withhold the verdict",
      "LABELS COULD NOT BE DERIVED" in undecodable.getvalue(), undecodable.getvalue()[-200:])

empty = io.StringIO()
with contextlib.redirect_stdout(empty):
    fetch.audit("fixture", [{"sentence1": "a", "sentence2": "b", "original_label": "positive"}])
check("a mirror with no triples says so instead of computing on nothing",
      "no usable triple annotations" in empty.getvalue())

print()
if failures:
    sys.exit(f"{len(failures)} failed: {failures}")
print(f"all good")
