"""Maps public research corpora onto the judgements this companion actually makes.

Every model verdict in docs/SPECIALIST_MODELS.md is qualified by the same sentence: the corpus is
synthetic and one person wrote it. That was treated as a fact about the world for several sessions.
It was a fact about nobody having looked — most of these judgements have names in the literature,
and several have annotated corpora that match far more precisely than anything a template generator
produces.

Each adapter here is a PURE FUNCTION from one dataset's rows to this repo's training-row shape, so
it can be tested offline against a fixture without downloading anything. `fetch.py` does the
downloading and calls these. The split is deliberate: a mapping bug and a network failure should
not look the same.

--------------------------------------------------------------------------------------------
THE GROUP KEY IS THE PART TO GET RIGHT

This project has now been wrong about leakage twice, one level apart. First a row-level split put
"I'm behind on the shed roof" in training and "I'm behind on the migration" in test and called the
result generalisation. Then a family-level split fixed that and left the sample size untouched, so
a ten-family draw reported a coin flip to three decimal places.

Real corpora bring their own version of the same trap, and it is worse because it is invisible: the
same persona sentence appears in hundreds of DialogueNLI pairs, and the same CLINC intent has a
hundred paraphrases of one request. Splitting either by row measures memorisation.

So every adapter returns an explicit `family`, and `family` means "the thing that must not appear on
both sides of a split". It is not decoration and it is not the label — it is the unit the
family-macro metric and the grouped cross-validation both key on.
"""
from __future__ import annotations


def _row(text, label, decision, family, source, generator, difficulty=0):
    """The shape `crossval.py` reads, identical to the generated and captured corpora.

    `heuristic` is deliberately absent rather than null-filled: the incumbent's answer is stamped in
    by the C# generator on rows it generated, and inventing one here in Python is exactly the
    duplication that was removed for drifting. Scoring the incumbent on borrowed data means running
    the real detector over it, which is a job for the eval tool, not for an adapter.
    """
    return {
        "text": " ".join(str(text).split()),
        "label": bool(label),
        "decision": decision,
        "family": family,
        "difficulty": difficulty,
        "source": source,
        "generator": generator,
    }


def require(columns, needed, name):
    """Fails loudly when a dataset's schema is not what an adapter was written against.

    These adapters were written from published descriptions rather than against the files, and a
    renamed column is the likeliest way they break. The failure mode matters: a silently missing
    label field produces a corpus that is all-negative and trains a model that says no to
    everything, which scores 97 % accuracy and is discovered weeks later. Raising here makes a
    schema change look like a schema change.
    """
    missing = [c for c in needed if c not in columns]
    if missing:
        raise ValueError(
            f"{name}: expected column(s) {missing}, got {sorted(columns)}. "
            f"The adapter was written against a published description of this dataset; "
            f"if the schema has changed, fix the mapping rather than the check.")


# --------------------------------------------------------------------------------------------
# DialogueNLI -> memory supersession
#
# The single most precise match found, and it answers the exact question an off-the-shelf MNLI
# model was measured getting wrong in Phase 4. MNLI asks whether two sentences describe the same
# scene; memory asks whether both can be true OF ONE PERSON. DialogueNLI is built from PersonaChat
# persona sentences and annotated for the second question — "I have a dog" against "I have a cat"
# is labelled by someone who was asked whether one person could have both.
#
# Phase 4's recorded failures are literally this dataset's subject matter:
#     corgi called Kanga / cat called Mim     needs coexist, MNLI said contradiction 1.00
#     plays cello / plays piano               needs coexist, MNLI said contradiction 1.00
#
# The rows to use are the (persona, persona) pairs. Utterance-persona pairs are a different
# question — "does this thing someone said follow from their profile" — which is closer to
# extraction than to supersession.
# --------------------------------------------------------------------------------------------
def dialogue_nli(rows, label_names=None):
    """Accepts either column naming, because the distributions disagree and both are in use.

    The canonical release from the author's site uses `sentence1`/`sentence2`; the Hub mirrors
    expose the same rows as `premise`/`hypothesis`. An adapter written against one of them fails on
    the other with a schema error that looks like a broken download.

    Integer labels are NOT decoded by assumption. Which integer means entailment is a convention,
    conventions differ between mirrors, and guessing one is precisely the mistake the CommitmentBank
    adapter made — SuperGLUE's 0 meaning entailment read as a Likert 0.0 meaning undecided, which
    inverted the label on the rows that mattered most. If the labels are ints, the dataset's own
    feature names are read; if those are absent, this raises rather than picks.
    """
    out = []
    for r in rows:
        keys = r.keys()
        if "sentence1" in keys and "sentence2" in keys:
            first, second = r["sentence1"], r["sentence2"]
        elif "premise" in keys and "hypothesis" in keys:
            first, second = r["premise"], r["hypothesis"]
        else:
            raise ValueError(
                f"dialogue_nli: expected sentence1/sentence2 (the author's JSON) or "
                f"premise/hypothesis (the Hub mirrors), got {sorted(keys)}.")
        require(keys, ["label"], "dialogue_nli")

        raw = r["label"]
        if isinstance(raw, bool) or not isinstance(raw, int):
            label = str(raw).lower()
        elif label_names:
            label = str(label_names[raw]).lower()
        else:
            raise ValueError(
                f"dialogue_nli: label {raw!r} is an integer and no label names were supplied, so "
                f"which class it means is unknown. fetch.py reads them off the dataset's own "
                f"features; for a hand-downloaded file, pass label_names=[...] in the order the "
                f"file documents. It is not guessed — a mirror that orders them differently would "
                f"silently invert entailment and contradiction.")

        if label not in ("entailment", "neutral", "contradiction"):
            continue

        # A contradiction is a supersession CANDIDATE; entailment and neutral are not. Collapsed to
        # binary because that is the decision the memory pipeline makes — and it makes it
        # ADVISORILY: no model deletes anything, MemoryCurator does, with a revision record.
        out.append(_row(
            text=f"{first} </s> {second}",
            label=label == "contradiction",
            decision="memory.supersession",
            # Group on the premise. The same persona sentence appears in hundreds of pairs, so a
            # row split trains on "I have a dog / I have a cat" and tests on "I have a dog / I have
            # a hamster" and calls that generalisation.
            family=f"premise:{first}",
            source="research_corpus",
            generator="dialogue-nli"))
    return out


# --------------------------------------------------------------------------------------------
# CommitmentBank -> AssertionGuard
#
# 1,200 naturally occurring discourses whose final sentence puts a clause-embedding predicate under
# an ENTAILMENT-CANCELLING OPERATOR: a question, a modal, a negation, or the antecedent of a
# conditional. Annotators rated, on -3..+3, how committed the speaker is to the embedded clause.
#
# That is AssertionGuard's problem, itemised by someone else. The three failures recorded in Phase 4
# are three of those four environments:
#
#     "Did I ever tell you what timber I bought?"     question       -> NLI wrongly entailed at 0.97
#     "If I bought cedar, would it last longer?"      conditional    -> wrongly entailed at 0.68
#     "I wouldn't say I've bought the timber yet"     negation       -> the case mood cannot reach
#
# The Likert scale is the useful part and gets thresholded, not discarded: the guard is a VETO, and
# a veto wants "is the speaker clearly committed", not "which of three classes is this".
# --------------------------------------------------------------------------------------------
def commitment_bank(rows, encoding, committed_at=2.0):
    """`encoding` is required, and the first version of this tried to infer it and was wrong.

    SuperGLUE ships CommitmentBank as 3-class NLI (0 entailment, 1 contradiction, 2 neutral); the
    original release keeps the mean Likert rating from -3 to +3. Which you get depends on where you
    downloaded it, and the obvious trick — "a number in [-3, 3] is a Likert rating" — inverts the
    label on the single most important case: SuperGLUE's `0` means *entailment*, i.e. fully
    committed, and reads as Likert 0.0, i.e. undecided. The offline test caught it immediately,
    which is the entire reason the mapping is separated from the download.

    A bare number cannot be disambiguated, so it is not guessed. The caller knows which file it
    opened; this asks.
    """
    if encoding not in ("likert", "superglue"):
        raise ValueError(
            f"commitment_bank: encoding must be 'likert' (original release, mean rating -3..+3) "
            f"or 'superglue' (3-class NLI ids), got {encoding!r}. It is not inferable: SuperGLUE's "
            f"0 means entailment and is indistinguishable from a Likert rating of 0, which means "
            f"the opposite.")

    out = []
    for r in rows:
        require(r.keys(), ["premise", "hypothesis", "label"], "commitment_bank")
        raw = r["label"]
        if encoding == "likert":
            committed = float(raw) >= committed_at
        else:
            committed = str(raw).strip().lower() in ("0", "entailment")

        out.append(_row(
            text=f"{r['premise']} </s> {r['hypothesis']}",
            label=committed,
            decision="memory.assertion",
            family=f"cb:{r['premise'][:80]}",
            source="research_corpus",
            generator="commitment-bank"))
    return out


# --------------------------------------------------------------------------------------------
# CLINC150 -> tool.capability
#
# 150 intents over 10 domains, with a genuine out-of-scope class — 22.5k utterances, which is three
# orders of magnitude more than the nineteen rows the generator produces for this decision. The
# positives are the intents about the assistant's own faculties.
#
# The intent names below are the mapping's one soft spot: they were not verified against the file.
# `fetch.py` reports which of them actually matched and refuses to write a corpus where none did,
# because an unmatched name list produces an all-negative dataset that looks like a working one.
# --------------------------------------------------------------------------------------------
CAPABILITY_INTENTS = frozenset({
    "what_can_i_ask_you", "how_old_are_you", "what_is_your_name", "who_made_you",
    "are_you_a_bot", "meaning_of_life", "do_you_have_pets", "fun_fact",
})

# The ones that matter most: asking what she can do, rather than asking her to do it.
CAPABILITY_CORE = frozenset({"what_can_i_ask_you", "are_you_a_bot", "who_made_you", "what_is_your_name"})


def clinc_capability(rows, capability_intents=CAPABILITY_CORE, label_names=None):
    out = []
    for r in rows:
        require(r.keys(), ["text", "intent"], "clinc_capability")
        intent = r["intent"]
        if label_names is not None and isinstance(intent, int):
            intent = label_names[intent]
        intent = str(intent)
        out.append(_row(
            text=r["text"],
            label=intent in capability_intents,
            decision="tool.capability",
            # Group on the intent: a hundred paraphrases of one request are one phenomenon, and
            # splitting them across train and test measures paraphrase memorisation.
            family=f"intent:{intent}",
            source="research_corpus",
            generator="clinc150"))
    return out


# --------------------------------------------------------------------------------------------
# DailyDialog -> companion.commitment (the detection half only)
#
# 13,118 dialogues with per-utterance acts: inform, question, directive, COMMISSIVE. Commissive is
# the speaker committing to a course of action, which is the detection half of what
# CommitmentDetector does.
#
# It is only the half. CommitmentDetector no longer answers "is this a commitment?" — it answers
# "is this a promise she is CAPABLE OF KEEPING?", and the capability allow-list exists because the
# model produced "I'll have some space set aside for experimental varieties" about a garden she does
# not have, and that became a durable open loop. DailyDialog will label that commissive, correctly,
# and it would still be wrong to store. So this trains the classifier and the gate stays code —
# which is §3.4's argument, arriving from a different direction.
#
# LICENCE: CC BY-NC-SA 4.0. Non-commercial, and ShareAlike propagates to derivatives — which for a
# fine-tuned model plausibly means the weights. Fine for a personal companion; check before any of
# this is ever distributed.
# --------------------------------------------------------------------------------------------
COMMISSIVE = 4  # DailyDialog act ids: 1 inform, 2 question, 3 directive, 4 commissive


def daily_dialog_commitment(rows, commissive=COMMISSIVE):
    out = []
    for index, r in enumerate(rows):
        require(r.keys(), ["dialog", "act"], "daily_dialog_commitment")
        for turn, (utterance, act) in enumerate(zip(r["dialog"], r["act"])):
            text = utterance.strip()
            if len(text) < 3:
                continue
            out.append(_row(
                text=text,
                label=int(act) == commissive,
                decision="companion.commitment",
                # One dialogue is one family: turns within it share speakers, topic and phrasing,
                # so splitting inside a conversation leaks its vocabulary across the boundary.
                family=f"dialog:{index}",
                source="research_corpus",
                generator="daily-dialog"))
    return out


ADAPTERS = {
    "dialogue-nli": dialogue_nli,
    "commitment-bank": commitment_bank,
    "clinc150": clinc_capability,
    "daily-dialog": daily_dialog_commitment,
}
