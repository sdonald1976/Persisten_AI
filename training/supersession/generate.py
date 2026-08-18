"""Generates the purpose-built supersession pair corpus: seven labels, hard negatives on purpose.

    python training/supersession/generate.py          # -> training/corpus/memory.supersession.pair.synthetic.jsonl

Then stamp the incumbent over it with the real code, not a transcription:

    dotnet run --project tools/Companion.Eval -- --only corpus --out training/corpus

Design rules, each paid for elsewhere in this repo:

* A FAMILY is one scenario template; fillers only vary the nouns. Splits are drawn on families.
* Hard negatives are written so the tempting surface cue points at the wrong label — replacement
  markers on COEXIST rows ("actually, I also keep bees"), no markers on SUPERSEDES rows ("I'm on
  decaf"), corrections that share every content word with what they correct.
* UNCERTAIN is a real label, not a dumping ground: its rows are written to be genuinely ambiguous
  (hedged futures, paused work), and nothing ambiguous is forced into another class to inflate n.
* The generator's coverage is itself a claim, so the per-label family counts are printed and the
  known gaps are printed with them.
"""
import itertools, json, pathlib, sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from taxonomy import LABELS, is_valid, render  # noqa: E402

for _stream in (sys.stdout, sys.stderr):
    if hasattr(_stream, "reconfigure"):
        _stream.reconfigure(encoding="utf-8", errors="replace")

CORPUS = pathlib.Path(__file__).resolve().parents[1] / "corpus"

DRINKS = [("black coffee", "oat milk lattes"), ("builder's tea", "green tea"),
          ("full-fat milk", "oat milk"), ("lager", "alcohol-free beer")]
CITIES = [("Norwich", "Cambridge"), ("Leeds", "York"), ("Bristol", "Bath")]
EMPLOYERS = [("the university", "a startup"), ("the council", "a design agency"),
             ("the hospital trust", "a medtech firm")]
HOBBIES = [("running", "swimming"), ("oil painting", "watercolours"), ("chess", "go")]
PROJECTS = [("the greenhouse irrigation rebuild", "the raised-bed build"),
            ("the shed roof", "the fence line"), ("the soil-chemistry talk", "the compost survey")]
DISLIKES = [("coriander", "olives"), ("marzipan", "liquorice"), ("crowds", "queues")]
PETS = [("a corgi called Kanga", "a cat called Mim"), ("a lurcher called Bess", "two hens")]
LANGS = [("French", "Spanish"), ("German", "Welsh")]
NAMES = [("Scott", "Scott Donald"), ("Kate", "Katherine Mair"), ("Dan", "Daniel Okafor")]
ALLERGENS = [("penicillin", "amoxicillin"), ("peanuts", "cashews")]
FOODS = [("a peanut satay", "peanuts"), ("a prawn curry", "shellfish")]


def _rows():
    def row(label, family, utterance, in_fact, in_value, predicate, ex_fact, ex_value,
            ex_predicate=None, age=90, single=False, same_slot=True, hard=0):
        return {
            "decision": "memory.supersession.pair",
            "label": label,
            "family": f"{label}:{family}",
            "difficulty": hard,
            "source": "synthetic",
            "generator": "pair-gen-1",
            "split": "develop",
            "incoming": {"fact": in_fact, "value": in_value, "predicate": predicate,
                         "utterance": utterance},
            "existing": {"fact": ex_fact, "value": ex_value,
                         "predicate": ex_predicate or predicate,
                         "age_days": age, "confirmed_days": min(age, 30)},
            "pair": {"same_slot": same_slot, "single_valued": single},
        }

    # ---------------- SUPERSEDES: marked changes -------------------------------------------------
    for old, new in DRINKS:
        yield row("SUPERSEDES", "gone-off", f"Actually I've gone off {old}. It's {new} for me now.",
                  f"The user prefers {new}.", new, "likes",
                  f"The user likes {old}.", old, age=400)
        yield row("SUPERSEDES", "switched-to", f"I've switched from {old} to {new}.",
                  f"The user drinks {new}.", new, "likes",
                  f"The user drinks {old}.", old, age=200)
        yield row("SUPERSEDES", "no-longer", f"I don't drink {old} any more.",
                  f"The user no longer drinks {old}.", new, "likes",
                  f"The user drinks {old}.", old, age=500)
    for old, new in HOBBIES:
        yield row("SUPERSEDES", "used-to", f"I used to do a lot of {old}, but these days it's {new}.",
                  f"The user does {new}.", new, "likes",
                  f"The user does {old} regularly.", old, age=700)
        yield row("SUPERSEDES", "given-up", f"I've given up {old} — my knees couldn't take it. {new.capitalize()} instead.",
                  f"The user does {new} instead of {old}.", new, "likes",
                  f"The user does {old} every week.", old, age=300)
    for old, new in CITIES:
        yield row("SUPERSEDES", "moved-city", f"We've moved — {new} now.",
                  f"The user lives in {new}.", new, "lives_in",
                  f"The user lives in {old}.", old, age=800, single=True)
    for old, new in EMPLOYERS:
        yield row("SUPERSEDES", "left-employer", f"I left {old}; I'm at {new} now.",
                  f"The user works at {new}.", new, "employer",
                  f"The user works at {old}.", old, age=600, single=True)

    # ---------------- SUPERSEDES: unmarked (hard) ------------------------------------------------
    for old, new in DRINKS:
        yield row("SUPERSEDES", "unmarked-drink", f"I'm on {new}.",
                  f"The user drinks {new}.", new, "likes",
                  f"The user drinks {old}.", old, age=300, hard=1)
    yield row("SUPERSEDES", "unmarked-routine", "My commute is by bike.",
              "The user commutes by bike.", "by bike", "routine",
              "The user commutes by train.", "by train", age=400, hard=1)
    yield row("SUPERSEDES", "unmarked-routine", "The commute's on the bus these days.",
              "The user commutes by bus.", "by bus", "routine",
              "The user drives to work.", "by car", age=400, hard=1)

    # ---------------- CORRECTS -------------------------------------------------------------------
    for wrong, right in NAMES:
        yield row("CORRECTS", "misheard-name", f"No — it's {right}, not {wrong}.",
                  f"The user's name is {right}.", right, "name",
                  f"The user's name is {wrong}.", wrong, age=1, single=True)
    for old, new in CITIES:
        yield row("CORRECTS", "wrong-place", f"You've got that wrong — I never lived in {old}. It's {new}.",
                  f"The user lives in {new}.", new, "lives_in",
                  f"The user lives in {old}.", old, age=2, single=True)
    for wrong, right in ALLERGENS:
        yield row("CORRECTS", "allergy-detail", f"I meant {right}, not {wrong} — I mixed them up.",
                  f"The user is allergic to {right}.", right, "health",
                  f"The user is allergic to {wrong}.", wrong, age=1)
    yield row("CORRECTS", "typo", "That's a typo on my part — the plot is at Marsh Lane, not March Lane.",
              "The user's allotment plot is at Marsh Lane.", "Marsh Lane", "other",
              "The user's allotment plot is at March Lane.", "March Lane", age=0)
    yield row("CORRECTS", "never-said", "Correction: my birthday's in March, not May.",
              "The user's birthday is in March.", "March", "birthday",
              "The user's birthday is in May.", "May", age=3, single=True)
    # hard: a correction long after the fact — the age says change, the words say error
    yield row("CORRECTS", "late-correction", "By the way, you've had my sister's name wrong all along — she's Freya, not Flora.",
              "The user's sister is called Freya.", "sister Freya", "relationship",
              "The user's sister is called Flora.", "sister Flora", age=400, hard=1)

    # ---------------- REFINES --------------------------------------------------------------------
    for short, full in NAMES:
        yield row("REFINES", "full-name", f"It's {full}, formally.",
                  f"The user's name is {full}.", full, "name",
                  f"The user's name is {short}.", short, age=60, single=True)
    yield row("REFINES", "pet-detail", "The dog's a corgi, name of Kanga.",
              "The user has a corgi called Kanga.", "a corgi called Kanga", "has_pet",
              "The user has a dog.", "a dog", age=30)
    yield row("REFINES", "place-detail", "North Cambridge, specifically — near the science park.",
              "The user lives in north Cambridge near the science park.", "north Cambridge", "lives_in",
              "The user lives in Cambridge.", "Cambridge", age=90, single=True)
    yield row("REFINES", "job-detail", "Specifically I'm the data engineer on the platform team.",
              "The user is a data engineer on the platform team.", "data engineer", "occupation",
              "The user works in tech.", "in tech", age=120, single=True)
    yield row("REFINES", "project-detail", "The raised beds are cedar, by the way — 4x8s.",
              "The user's raised beds are cedar 4x8s.", "cedar raised beds", "works_on",
              "The user is building raised beds.", "raised beds", age=20)
    yield row("REFINES", "diet-detail", "More precisely it's lacto-vegetarian.",
              "The user follows a lacto-vegetarian diet.", "lacto-vegetarian", "diet",
              "The user is vegetarian.", "vegetarian", age=60, single=True)

    # ---------------- DUPLICATE ------------------------------------------------------------------
    for old, _ in DRINKS:
        yield row("DUPLICATE", "restated", f"Yeah — still on the {old}, as ever.",
                  f"The user drinks {old}.", old, "likes",
                  f"The user drinks {old}.", old, age=100)
    for a, _ in DISLIKES:
        yield row("DUPLICATE", "paraphrase", f"As I've said before, {a} is not for me.",
                  f"The user dislikes {a}.", a, "dislikes",
                  f"The user dislikes {a}.", a, age=200)
    yield row("DUPLICATE", "same-value-new-words", "Coffee-wise I'm a black-coffee person, always have been.",
              "The user drinks black coffee.", "black coffee", "likes",
              "The user drinks their coffee black.", "black coffee", age=300)
    yield row("DUPLICATE", "same-value-new-words", "Still plugging away at the irrigation rebuild.",
              "The user is working on the greenhouse irrigation rebuild.", "the greenhouse irrigation rebuild",
              "works_on", "The user is rebuilding the greenhouse irrigation.",
              "the greenhouse irrigation rebuild", age=15)

    # ---------------- COEXIST --------------------------------------------------------------------
    for a, b in DISLIKES:
        yield row("COEXIST", "another-dislike", f"I don't like {b} either.",
                  f"The user dislikes {b}.", b, "dislikes",
                  f"The user dislikes {a}.", a, age=200)
    for a, b in PROJECTS:
        yield row("COEXIST", "second-project", f"I've started a second thing too — {b}.",
                  f"The user is working on {b}.", b, "works_on",
                  f"The user is working on {a}.", a, age=40)
    for a, b in PETS:
        yield row("COEXIST", "second-pet", f"We got {b} as well.",
                  f"The user has {b}.", b, "has_pet",
                  f"The user has {a}.", a, age=300)
    for a, b in LANGS:
        yield row("COEXIST", "another-language", f"Besides {a}, I speak {b}.",
                  f"The user speaks {b}.", b, "speaks_language",
                  f"The user speaks {a}.", a, age=500)
    # hard: replacement markers on rows that must NOT replace
    yield row("COEXIST", "marker-but-adds", "Actually, I also keep bees.",
              "The user keeps bees.", "bees", "has_pet",
              "The user has a corgi called Kanga.", "a corgi called Kanga", age=300, hard=1)
    yield row("COEXIST", "marker-but-adds", "I used to think I'd hate pottery, but I love it — that's a new one for the list.",
              "The user enjoys pottery.", "pottery", "likes",
              "The user enjoys hillwalking.", "hillwalking", age=400, hard=1)
    yield row("COEXIST", "marker-but-adds", "Scratch Sundays — the choir now meets on Tuesdays, so I do both walks and choir.",
              "The user sings in a choir on Tuesdays.", "choir on Tuesdays", "routine",
              "The user walks on Saturdays.", "walks on Saturdays", age=200, hard=1)
    for a, b in HOBBIES:
        yield row("COEXIST", "taken-up-too", f"I've taken up {b} too.",
                  f"The user does {b}.", b, "likes",
                  f"The user does {a}.", a, age=250, hard=1)

    # ---------------- CONTRADICTS ----------------------------------------------------------------
    for allergen, _ in ALLERGENS:
        yield row("CONTRADICTS", "acts-against-allergy",
                  f"Had {allergen} last week for the chest thing, no problem at all.",
                  f"The user took {allergen} recently without a reaction.", allergen, "health",
                  f"The user is allergic to {allergen}.", allergen, age=300)
    for dish, kind in FOODS:
        yield row("CONTRADICTS", "acts-against-diet", f"I ordered {dish} last night — gorgeous.",
                  f"The user ate {dish}.", dish, "other",
                  f"The user is allergic to {kind}.", kind, "health", age=200, same_slot=False)
    yield row("CONTRADICTS", "acts-against-abstinence", "We split a bottle of red with dinner.",
              "The user drank wine at dinner.", "wine", "other",
              "The user does not drink alcohol.", "no alcohol", "belief", age=400, same_slot=False)
    yield row("CONTRADICTS", "been-there", "When I was in Lisbon I saw the tram museum.",
              "The user has visited Lisbon.", "Lisbon", "other",
              "The user has never been abroad.", "never abroad", "other", age=500)
    yield row("CONTRADICTS", "age-mismatch", "Well, I'm 44, so that tracks.",
              "The user is 44.", "44", "age",
              "The user is 39.", "39", age=100, single=True, hard=1)

    # ---------------- UNCERTAIN ------------------------------------------------------------------
    for _, new in CITIES:
        yield row("UNCERTAIN", "hedged-move", f"We might end up moving to {new}, nothing decided.",
                  f"The user may move to {new}.", new, "lives_in",
                  f"The user lives in Norwich.", "Norwich", age=400, single=True)
    for a, _ in PROJECTS:
        yield row("UNCERTAIN", "paused-work", f"{a.capitalize()} is on hold for now.",
                  f"The user has paused {a}.", a, "works_on",
                  f"The user is working on {a}.", a, age=60)
    yield row("UNCERTAIN", "wavering-taste", "Not sure I still like marzipan, honestly.",
              "The user may no longer like marzipan.", "marzipan", "likes",
              "The user likes marzipan.", "marzipan", age=300)
    yield row("UNCERTAIN", "thinking-of-quitting", "I'm half thinking of giving up the plot next year.",
              "The user is considering giving up their allotment.", "the allotment", "works_on",
              "The user has an allotment plot.", "the allotment", age=600)
    yield row("UNCERTAIN", "ambiguous-referent", "That one's done with, anyway.",
              "The user has finished something.", None, "works_on",
              "The user is working on the shed roof.", "the shed roof", age=30, hard=1)


def main():
    rows = [dict(r, text=render(r)) for r in _rows()]
    bad = [r for r in rows if not is_valid(r)]
    if bad:
        sys.exit(f"generator produced {len(bad)} invalid rows; first: {bad[0]['family']}")

    CORPUS.mkdir(parents=True, exist_ok=True)
    path = CORPUS / "memory.supersession.pair.synthetic.jsonl"
    with path.open("w", encoding="utf-8") as out:
        for r in rows:
            out.write(json.dumps(r, ensure_ascii=False) + "\n")

    import collections
    families = collections.defaultdict(set)
    for r in rows:
        families[r["label"]].add(r["family"])
    print(f"{len(rows)} rows -> {path.name}")
    for label in LABELS:
        fams = families.get(label, set())
        print(f"   {label:<12} {sum(1 for r in rows if r['label'] == label):>4} rows  {len(fams):>2} families")
    print()
    print("Known gaps, so nobody mistakes coverage for completeness: no multi-referent turns (the")
    print("schema carries one existing memory), no non-English, and every phrasing is one author's.")
    print("Next: dotnet run --project tools/Companion.Eval -- --only corpus --out training/corpus")
    print("      (stamps the incumbent over these rows with the shipped rules)")


if __name__ == "__main__":
    main()
