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
if failures:
    sys.exit(f"{len(failures)} failed: {failures}")
print(f"all good")
