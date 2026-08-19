# The supersession task, designed from the decision rather than from the datasets

**Status: APPROVED 2026-08-18, with amendments recorded below. Implementation underway; §Built
says what exists.**

## The decisions, as made

1. **Taxonomy: seven labels, richer than proposed.** `COEXIST`, `SUPERSEDES` (was valid, the
   user's state changed), `CORRECTS` (was erroneous, the record is being fixed), `REFINES` (adds
   specificity without invalidating), `DUPLICATE`, `CONTRADICTS` (incompatible and the turn does
   not resolve which way), `UNCERTAIN`. UNCERTAIN is load-bearing: genuinely ambiguous examples
   are labelled as such, never forced into a class to obtain a label. The proposal's `unrelated`
   folds into COEXIST (the action is the same: store alongside); its `replaces_change` and
   `replaces_correction` became SUPERSEDES and CORRECTS.
2. **Pair capture: approved** under the existing capture gates, redaction and `/forget` purge,
   with provenance — stable ids, timestamps, predicate, source, incumbent verdict, and the
   specialised model's verdict/version/confidence once one runs — and no raw text beyond what
   training and evaluation need.
3. **MSC: excluded from production training until its licence is positively verified and
   compatible.** It may be evaluated experimentally in isolation; if its licence carries
   non-commercial/ShareAlike obligations, it is out. Dataset provenance and licensing metadata
   are maintained so it is always known which sources contributed to a trained artifact — the
   manifest in §Built is that mechanism.
4. **False-supersede budget: for full replacement the model's rate must be statistically LOWER
   than the incumbent's, not merely equal.** And full replacement is not the only useful outcome:
   the design target is confidence-calibrated progressive adoption — high-confidence predictions
   may own the decision, low-confidence falls back to the incumbent, disagreements feed
   adjudication. Both error rate and SAFE COVERAGE (how much of the surface the model owns while
   inside the budget) are measured.
5. **Order: pair capture first** — the production base rate is measured, not assumed — and the
   training/evaluation pipeline is built in parallel on verified data, synthetic and adversarial
   cases, regression cases and weak labels, folding captured disagreements in as they accumulate.

The architectural objective is unchanged: determine whether a purpose-built specialised model can
progressively take over this semantic decision. The incumbent is a baseline, fallback and
potential weak labeler — not the presumed permanent architecture.

This exists because of a correction worth writing at the top. Two model experiments failed against
the supersession and unfinished-work heuristics, and the document of record let those verdicts read
as evidence that the decisions should stay heuristic. They are not that. They are evidence that an
off-the-shelf MNLI model answers a different question, and that a lightly fine-tuned encoder on a
proxy corpus is short on precision — statements about *those experiments*, not about the ceiling of
learned models on this decision. The architectural hypothesis stands: semantic judgements should
ultimately be made by specialised models where practical, with deterministic code deciding what is
allowed to happen because of them. What follows designs the supersession task from first
principles: what the decision actually is, what a model should see, what it should say, where the
training signal comes from, and what would justify migrating.

---

## 1. The decision, precisely

At the moment `MemoryPipeline.ProcessSemanticAsync` runs, a new candidate fact about the user has
been extracted from this turn, and the store already holds facts. The decision is:

> **Given a new fact and the existing memory it most plausibly interacts with, what is the
> relationship between them — and therefore what is the store allowed to do?**

Today that one question is answered by four entangled signals: exact slot identity (duplicate),
predicate cardinality (single-valued slots displace), a replacement-phrase regex over the user's
words, and embedding similarity as a floor. Each was added after a real failure and each is a
partial view of the same underlying relation. The learned task is that relation, stated once.

What the decision is **not**: it is not "do these two sentences contradict" (MNLI's question — the
same scene), and it is not "could one person hold both" (DialogueNLI's question — coherence at a
single instant). Both were measured and both miss the axis this decision turns on, which is **time
and speaker intent**: a person whose coffee order changed is not incoherent, and a corpus built
from snapshots cannot contain them.

## 2. Ideal inputs, independent of the current implementation

What a competent human adjudicator would want on their desk, no more:

```jsonc
{
  // The new fact, with the words it came from. The utterance is not optional context —
  // the replacement signal usually lives THERE and not in the normalized fact. "Actually
  // I've gone off tea, coffee now" extracts a fact about coffee that mentions no change.
  "incoming": {
    "fact":       "The user prefers oat milk lattes.",
    "utterance":  "Actually I've gone off black coffee. I take oat milk lattes now.",
    "predicate":  "likes",              // from the closed vocabulary
    "value":      "oat milk lattes"
  },
  // The existing memory under consideration. Its age matters: "I live in Cambridge"
  // against a two-year-old address is a move; against one from this morning it is more
  // likely a correction of a mis-extraction.
  "existing": {
    "fact":       "The user drinks their coffee black without any sugar.",
    "predicate":  "likes",
    "value":      "black coffee without sugar",
    "age_days":   412,
    "last_confirmed_days": 90
  },
  // Deterministic facts about the pair that the model should not have to rediscover.
  "pair": {
    "same_slot":        true,
    "single_valued":    false            // PredicateVocabulary's answer, as an input feature
  }
}
```

Deliberately absent: embedding similarity (measured unable to order these cases — 0.763 for a
must-replace under 0.753 for a must-coexist), retrieval scores, and anything about *other*
memories. Candidate selection — *which* existing memories to ask about — stays deterministic code:
the model judges a pair it is handed, it does not choose the pair. That keeps the model out of the
business of scanning the store, which is where a wrong generalisation does the most damage.

Encoding for a cross-encoder is a rendering question, not a schema question:
`[utterance] fact [SEP] existing fact` with the structured fields as short text tags
(`slot=likes single_valued=no age=412d`). Small encoders read tags like these fine, and it keeps
one input pipeline for train and inference.

## 3. The label taxonomy (as approved)

Seven labels, each mapped to an action the curator already knows how to take:

| label | meaning | action (code disposes) |
|---|---|---|
| `COEXIST` | both true of this person at once (covers unrelated: same action) | store alongside |
| `SUPERSEDES` | was true, no longer is — the user's state or preference changed | `Superseded` — old kept as history, "true until…" |
| `CORRECTS` | was never true — the record was wrong and is being fixed | `Superseded`/`Disputed` — old marked erroneous, must not resurface in "you used to…" |
| `REFINES` | adds specificity without invalidating — "Scott" → "Scott Donald" | `Updated`/merge — value improves in place |
| `DUPLICATE` | same fact restated | `Confirmed` — refresh recency, no new memory |
| `CONTRADICTS` | cannot both hold, and the turn does not say which way to resolve | review queue — a human decides |
| `UNCERTAIN` | genuinely ambiguous | review queue; **never forced into another class to obtain a label** |

`CONTRADICTS` and `UNCERTAIN` both route to review and differ in what they claim: CONTRADICTS
asserts a real conflict with an unresolved direction ("had penicillin last week, no problem"
against a recorded allergy); UNCERTAIN declines to assert ("we might move to Leeds"). A model that
can say either is a model that does not have to bluff, and the training data treats them as first-
class answers rather than failures to answer.

The incumbent's vocabulary is a strict subset — it can express DUPLICATE, SUPERSEDES and COEXIST
and nothing else. The four labels it cannot say are the part of the decision currently not being
made at all. Scoring is therefore at the ACTION level, where both vocabularies are complete.

**The hard constraint is unchanged: the model proposes a label; `MemoryCurator` disposes.** No
label deletes anything; everything is reversible; low confidence degrades to review, never to a
silent write.

## 4. What the existing corpora actually contain — and do not

| distinction the task needs | DialogueNLI | CommitmentBank | generated templates | regression set (12 rows) |
|---|---|---|---|---|
| coexist vs conflict (cardinality axis) | **yes** — bimodal per relation, audited | no | no | 3 rows |
| change over time (`replaces_change`) | **no — structurally absent.** Persona snapshots; every same-slot "contradiction" is arithmetic ("2 dog" vs "5 dog") | no | no | 6 rows |
| correction vs change | no | no | no | 0 rows |
| refinement | no (same-triple pairs are entailment, but with no utterance and no action) | no | no | 2 rows |
| the utterance carrying the signal | **no — pairs of bare facts** | partially (discourse present) | no | **yes** |
| duplicates | yes (same-triple pairs) | no | no | 1 row |

The audit already established the headline (see `SPECIALIST_MODELS.md` §"The audit"): DialogueNLI
encodes relation cardinality, which makes it a *weak-supervision source for one axis* —
coexist-vs-conflict — and **ground truth for nothing else**. Training a supersession model on it
"whole" would teach a model that answers one-sixth of the taxonomy and have it scored on all six.
That is the proxy-dataset mistake, and this design treats every borrowed corpus as weak until a
mapping argument says otherwise, per label, in writing.

New since the audit, probe-level evidence only: **`MemGPT/MSC-Self-Instruct` resolves on the Hub
and carries `personas`, `personas_update1`, `personas_update2`** — Multi-Session Chat's persona
revisions across sessions. That is the one public structure found so far that plausibly contains
`replaces_change` by construction (a persona line revised between sessions is a persona line that
changed). Columns confirmed by streaming probe; mapping, label derivation and licence all
unverified. Same discipline as DialogueNLI: an adapter with offline fixtures, an audit that derives
what the update fields actually mean before anything trains on them, and a licence check before
anything trained on it leaves this machine.

## 5. Training data, by source and by role

Family key throughout: **the existing-fact's slot + scenario template** — the unit that must never
straddle a split. Same rule, same reason, third time.

| source | supplies | role | est. volume |
|---|---|---|---|
| **Regression set** (`tools/Companion.Eval/datasets/supersession.jsonl`) | every recorded production failure, with utterances | gold; frozen into the held-out set, never trained on | 12 now, grows |
| **Capture, extended to pairs** (§7) | real `(incoming, existing, heuristic-verdict)` triples from live turns, `label: null` | the adjudication queue; gold after review | rate unknown — measuring it is itself the point |
| **Purpose-built generator** | all six labels crossed with predicates × time-gaps × marker/no-marker phrasings, plus hard negatives (marker words in non-replacement contexts: "actually, I also…", "I used to think…", "no longer sure"; changes stated with no marker at all: "I'm on decaf") | gold-synthetic; the only source that covers `replaces_correction` and marker-free changes on day one | 60–100 families, ~1.5k rows |
| **DialogueNLI**, relabelled through the audit's cardinality mapping | `coexist` (many-valued kind-conflicts), `duplicate` (same-triple), `unrelated` (relation swaps) | **weak**, pretraining only, capped so it cannot dominate; never in the held-out set | ~20k sampled |
| **MSC persona updates** (after its own audit) | `replaces_change`, possibly `refines` | weak until the audit says otherwise | unknown |
| **Heuristic outputs as weak labels** | the shipped rule's verdict stamped on every row (`BorrowedStamp` already does this) | a feature for error analysis and a sort key for adjudication — **never a training target**; a corpus labelled by the rule it judges can only conclude the rule was right | free |
| **Adjudicated disagreements** (§8) | the cases where model and heuristic disagree on live traffic | gold; the highest-value rows per unit of labelling effort | grows with use |

## 6. Adjudication strategy

- **Queue**: `harvest.py` extended to the pair subject — writes
  `memory.supersession.captured.jsonl` with `label: null`, sorted by (heuristic ∧ model disagree,
  then model confidence closest to its threshold). Disagreement-first is active learning with no
  machinery: those rows move the boundary most per label.
- **Who**: you. Single-adjudicator bias is real and is bounded the same way the corpus bias was —
  by recording it (`source: human_reviewed`, `adjudicator: sdonald`) and by the frozen regression
  set, which the adjudicator does not get to edit after the fact.
- **Interface**: the JSONL review file, one row per line, fill in `label`. No tooling until the
  queue's size proves tooling is needed.
- **Ambiguity is a label**: rows where the right answer needs context the schema does not carry get
  `label: "undecidable"` and become schema feedback, not training rows. If `undecidable`
  accumulates around a missing field (e.g. conversation topic), that is the argument for adding
  the field — evidence first, schema second.
- **Drift check**: 10% of adjudicated rows re-presented blind after a month. Disagreement with
  yourself above ~15% means the taxonomy definitions need tightening before more labelling.

## 7. What has to exist before training: pair capture

The single biggest gap is that nothing records the *pairs*. Message-level capture exists; the
supersession decision is about `(incoming, existing)` and today only the binary shadow subject
records anything, only when NLI is loaded, and only the two fact strings.

Proposed: when capture is on and the pipeline reaches the supersession check with a plausible
`slotBest` or `nearest`, record subject `memory.supersession.pair` with the §2 schema as input text
and the heuristic's outcome (`duplicate`/`coexist`/`superseded`/`review`) as the weak verdict.
Same gate as all capture (extraction-eligible turns only), same `SecretDetector` redaction, same
`/forget` purge — the excerpt-matching purge already ships, and pair rows carry the same user text
so they get the same treatment.

This also produces the number every threshold below depends on and nothing has measured: **how
often a plausible supersession pair occurs per hundred turns, and what fraction resolve each way.**
The 3% figure in the docs is an assumption about a different decision; this one has no measured
base rate at all yet.

## 8. Model and calibration

- **Architecture**: cross-encoder over the rendered pair (§2), 6-way softmax head. Base:
  `all-MiniLM-L6-v2` (22M) first — same budget as everything already measured, ~25ms CPU, known to
  export and verify; `nli-deberta-v3-small` (142M) as the one permitted escalation if MiniLM
  plateaus, decided by the same CV, not by taste.
- **Schedule**: weak pretrain (DialogueNLI-relabelled + MSC if it passes audit) → gold fine-tune
  (generator + adjudicated + reviewed captures). Standard two-stage; the weak stage is dropped if
  it does not improve the gold-stage CV — weak data earns its place or leaves.
- **Calibration**: temperature scaling fitted on pooled out-of-fold predictions (never the held-out
  set). Report ECE per class; an uncalibrated 6-way softmax's "0.92" means nothing and thresholds
  set against it inherit the nothing.
- **Thresholds are per-action, set by cost, not one number**: a false `replaces_*` buries a true
  fact in history (user-visible lie, recoverable only by a human noticing); a false `coexist`
  leaves clutter; a false `duplicate` suppresses a real new fact (silent, the worst kind). So:
  `replaces_*` acts only above a high bar (~0.9 calibrated, tuned on CV to a false-supersede
  budget), mid-confidence degrades to the existing review queue, low confidence defers to
  deterministic fallback. `duplicate` gets the same treatment as `replaces` — its failure is
  silent. `coexist`/`unrelated` can afford lenient bars.
- Single-valued slots keep their deterministic displacement rule regardless of the model — that is
  §4-of-the-design-doc territory (cardinality is code), and the model's job there reduces to
  `refines`-vs-`replaces_correction`-vs-`duplicate`, which is where the errors actually were.

## 9. Metrics

Grouped 5-fold CV over families, paired bootstrap on every comparison, family-macro primary —
unchanged, the harness already enforces it, and the encoder path already runs through it.

Per-label, the ones that gate deployment:

1. **False-supersede rate**: P(model says `replaces_*` | truth is `coexist`/`duplicate`/`refines`),
   reported at the operating threshold, with its bootstrap interval. This is the "silently lose a
   project" number. Gate: model-at-threshold ≤ incumbent's measured rate on the same rows.
2. **False-duplicate rate**: P(says `duplicate` | truth is anything else) — the silent-suppression
   number, same treatment.
3. **Replace recall**: the incumbent's known weakness (wording signal R=0.500). This is where the
   model has to win for any of this to be worth it.
4. **Correction/change confusion**: reported, not gated initially — no incumbent even attempts it.
5. **Coverage**: fraction of live pairs above the acting threshold. A model that abstains on 60%
   of traffic is calibrated honesty, but it changes what "replaces the heuristic" means and gets
   reported next to every other number.

Precision-at-base-rate is computed against the **measured** pair rate from §7 once it exists;
until then both the assumed rate and the measured rate are printed side by side, because several
existing conclusions move if they differ.

## 10. Migration criteria

Three outcomes per §the-instruction, decided per label, not per model:

| outcome | criterion |
|---|---|
| **Model decides, heuristic retired** | on ≥200 adjudicated real pairs: beats incumbent on replace-recall with interval clear of zero, AND false-supersede ≤ incumbent's, AND ≥4 weeks shadow with every disagreement adjudicated and the model right in ≥70% of them |
| **Model decides, heuristic as fallback** | model wins as above but coverage < ~80%: model acts above threshold, deterministic rule handles the abstentions. The likely first landing point |
| **Heuristic decides, model as weak labeler/flag** | model fails the gates but its disagreements are adjudicated right often enough to keep sorting the queue. Also the interim state during shadow |

Shadow before adoption, two flags, never one — `Nli:Enabled`-style load flag and a separate
act flag, exactly as the reranker already does. And symmetric honesty per the instruction: a model
clearing an aggregate F1 bar does **not** migrate if it fails a per-label gate, and a heuristic is
**not** retained because round one went badly — round one used a proxy corpus and a binary label,
and this document exists because that was the wrong experiment to conclude anything from.

## Built, and the first run's numbers

Everything below exists, is tested, and ran on 2026-08-18. 951 tests green.

**Pair capture** (`SupersessionPairCapture`, `ICognitiveCapture.CapturePairAsync`,
`MemoryPipeline`): every semantic decision exit records the pair it judged under subject
`memory.supersession.pair` — including "coexist", because a corpus holding only the pairs that
superseded teaches a model that everything supersedes. Rows carry the §2 schema plus provenance
(existing-memory id, ages, slot, cardinality, similarity-as-provenance, incumbent outcome). Same
extraction gate, same `SecretDetector` redaction; `/forget` purges pair rows by the stored
memory's id as well as by excerpt. Six tests pin all of it.

**The incumbent, stamped by running it** (`PairStamp` in Companion.Eval): the shipped rules —
value-key identity, cardinality, `SignalsReplacement`, the mention check — run over every pair
row and write their verdict. On the corpus so far, at the action level: regression F1 0.800,
synthetic F1 0.815, false-supersede rate 0.086.

**The pipeline** (`training/supersession/`): `taxonomy.py` (labels, actions, one renderer),
`generate.py` (90 rows / 40 families across all seven labels, hard negatives with markers on
COEXIST rows and none on SUPERSEDES rows), `assemble.py` (the 12 regression incidents hand-mapped
to the seven labels and FROZEN as holdout; the provenance/licence manifest the trainer enforces;
optional DialogueNLI weak stage relabelled through the audited cardinality mapping only),
`train.py` (grouped 5-fold CV through the same imported training loop as the binary encoder,
per-label and action-level metrics, paired bootstrap against the stamped incumbent, temperature
calibration that refuses a fit that worsens ECE, and the safe-coverage table). `harvest.py` turns
captured pair rows into the adjudication queue with the incumbent's verdict as sort key.

First run, 90 synthetic rows, 16 epochs, all develop rows one author's templates:

```
supersede ACTION:  model P=0.655 R=0.463 F1=0.543   false-supersede 0.123
incumbent, same:         P=0.825 R=0.805 F1=0.815   false-supersede 0.086
model - incumbent: -0.272 [-0.556, +0.032]          indistinguishable
calibration:       fitted T=0.50 REJECTED (ECE 0.075 -> 0.207); T=1
safe coverage:     0% at every bar - the model owns nothing yet
holdout:           4/12 production incidents exact-label
```

Read it as the design says to: this is a pipeline being proven, not a model being judged. Ninety
rows over seven classes is far below what a 22M encoder needs, the model knows it (max softmax
0.31 on the holdout), and the calibration guard and the empty coverage table are the machinery
saying so honestly instead of flattering it. The per-label structure is already informative —
CORRECTS and CONTRADICTS, the two classes the incumbent cannot express at all, are the model's
best (F1 0.462, 0.444) — and the misses that matter are the regression incidents, printed one by
one above the table every run.

What moves these numbers, in order: captured real pairs (base rate + adjudication queue), more
generator families (forty is the number the binary work already showed is marginal), and only
then model-side work.

## The synthetic-life experiment (2026-08-18): representative structure, memorized surface

dev_automate's merged generator (`tools/Companion.Eval/SyntheticLife.cs`, 10,000 events over 1,000
simulated lives) was audited and adapted for this task — `training/supersession/
adapt_synthetic_life.py` holds the audit and its exclusions: 5,699 of 10,000 events survive.
Excluded with reasons: 1,963 pairless events, the `establish-fact` family (re-establishes seeded
values; 61 % duplicate-shaped, and it corrupts the generator's own canonical state after a
supersession), and 1,507 value-identical pairs under non-DUPLICATE labels — label noise sitting
exactly on the boundaries this model exists to learn. The shipped split artifacts are ungrouped
(995 of 1,000 lives straddle them) and were not used; folds group on the life.

The controlled experiment — same MiniLM, same gates, same frozen 12-incident holdout, only the
data changed:

| | 90-row corpus | synthetic-life (5,699 rows) |
|---|---|---|
| CV action F1 | 0.543 | **1.000** |
| CV false-supersede | 0.123 | 0.000 |
| vs incumbent (same rows) | −0.272 [−0.556, +0.032] | +0.412 [+0.264, +0.611] "beats" |
| safe coverage | 0 % | "93 % at every bar, 0 FS" |
| **production holdout** | **4/12**, misses at 0.22–0.31 | **3/12**, misses at 0.68–0.99 |

A perfect score with a zero-error confusion matrix on five thousand rows is not a model learning a
decision; it is a model recognising 41 template families, and life-grouped folds cannot prevent
that because every life renders the same templates. The holdout is the tell: transfer went DOWN
while confidence went UP — "I left the university, I'm at a startup now" is called DUPLICATE at
0.96, which in production is a real change silently suppressed. Every in-corpus number above the
holdout row is template recognition wearing the metrics of competence.

What the corpus is genuinely worth: the structural coverage is real and new — the first
change-over-time data (temporary → permanent, expiry, return-to-previous), corrections of
corrections, delayed clarifications — and the action mapping is a clean surjection onto this
task's action groups. The binding constraint is linguistic diversity, not volume or structure.
The generator already contains the remedy unbuilt into these artifacts: `LlmSyntheticVerbalizer`
with validation/quarantine and `verbalizationGroupId` grouping. Structure from the simulator,
surface from a verbalizer, folds on the verbalization group.

The template-grouped run (the generator's own third split axis, 31 template groups) closes the
diagnosis and adds the one insight worth keeping. Same corpus, folds on unseen verbalizations:

| class | life-grouped F1 | template-grouped F1 | what that says |
|---|---|---|---|
| DUPLICATE | 1.000 | **1.000** | value identity is IN THE PAIR — a structural cue that genuinely generalizes |
| CONTRADICTS | 1.000 | **1.000** | antonym structure, though only one scenario family carries it |
| SUPERSEDES | 1.000 | 0.911 (R=1.000, P=0.837) | partially structural; over-predicted on unseen phrasing |
| CORRECTS | 1.000 | 0.893 | mostly survives |
| UNCERTAIN | 1.000 | 0.601 | half surface |
| REFINES | 1.000 | 0.480 (R=0.316) | mostly surface |
| **COEXIST** | 1.000 | **0.000** | never once right on an unseen template — 382 rows called SUPERSEDES, 152 UNCERTAIN |

Action level on unseen templates: F1 0.906, false-supersede **0.076 against the incumbent's 0.000
on this corpus** — and 382 of those false supersedes are COEXIST rows, the "second project
silently buried" failure by name. Calibration guard rejected the fit; holdout unchanged at 3/12.

So the split is clean: classes whose evidence lives in the PAIR STRUCTURE (value identity,
antonymy) transfer to unseen phrasings; classes whose evidence lives in the UTTERANCE (additive
wording, hedges, specificity) collapse without linguistic diversity. The model can read structure
when structure is there. What it cannot do is read English it has never seen 41 templates' worth
of — which is the precise argument for running the simulator's structures through the
verbalization layer that shipped alongside it, and folding on `verbalizationGroupId`.

Generator defects recorded for the dev_automate line, not fixed here: the `establish-fact` state
bug; new values chosen independent of state (source of the 1,507 inconsistent rows); shipped
split files ungrouped; `synthetic-life-1000.jsonl` and `synthetic-life-phase3-1000.jsonl` are
content-identical.

## The naturalization experiment (2026-08-19): diversity moves the holdout, and the control proves it

The phase-4 corpus exists in two forms that differ in exactly one thing. Both come from the same
fixed simulator (Codex's semantic fixes: state-aware values, genuinely-new-slot establishments,
generation-time validity throws — commit 5839105); one keeps the deterministic template surface
(747 pair rows after the audit's rejections), the other runs 120 boundary-priority events through
`LlmSyntheticVerbalizer` + the conservative validator (149 pair rows surviving quarantine and the
same rejections). Same MiniLM, same trainer, same gates, same frozen holdout as 1229ad2. Corpus
construction was committed before any of these numbers were read.

| | phase-3 (1229ad2) | phase-4 structured (control) | phase-4 naturalized |
|---|---|---|---|
| rows / surfaces | 5,699 / 41 templates | 747 / 41 templates | 149 / LLM paraphrases |
| life-grouped action F1, FS | 1.000, 0.000 | 1.000, 0.000 | 1.000, 0.000 |
| life-grouped calibration | rejected | **T=0.25 accepted, ECE 0.000** | T=0.45 accepted, ECE 0.091 |
| template-grouped action F1, FS | 0.906, 0.076 | 0.808, **0.171** | 0.890, 0.129 |
| template-grouped vs incumbent | "beats" | +0.311 [−0.069, +0.633] indist. | +0.316 [−0.001, +0.531] indist. |
| **production holdout** | **3/12** (misses 0.68–0.99) | **2/12** (misses 0.49–0.97) | **5/12** (misses 0.23–0.66) |

**The control is the loudest sentence in the table.** With every semantic defect fixed and the
surface still deterministic, the model scores a perfect 1.000 on every one of seven classes,
calibration *sharpens* to ECE 0.000, safe coverage reads 94 % at every bar — and the holdout
falls to 2/12, the worst transfer ever measured on this task, at up to 0.97 confidence. Every
in-corpus number a deterministic-surface corpus produces is template recognition, now proven
under fixed semantics rather than argued.

**The naturalized corpus, 5× smaller, beats both on the only real data in the room**: 5/12, and
its misses sit at 0.23–0.66 — the first run whose confidence on wrong answers is honestly low,
and the first whose temperature fit was accepted rather than refused. Linguistic diversity did
not make the model much better in-corpus; it made the model's *knowledge transferable* and its
*doubt honest*.

Per-label on unseen templates, the watch-list:

| class | phase-3 | structured control | naturalized | reading |
|---|---|---|---|---|
| COEXIST | 0.000 | 0.385 (R=0.238; 112/147 → SUPERSEDES) | **0.492** (18/43 → SUPERSEDES) | recovers; half the recovery is the semantic fixes, the rest is surface diversity — and the coexist-buried-as-change error is still the dominant one |
| REFINES | 0.480 | 0.545 | **0.792** | the clearest diversity win |
| UNCERTAIN | 0.601 | 0.606 | **0.000** | eliminated by the validator, not the model: 10 surviving rows, never once predicted |
| DUPLICATE | 1.000 | 1.000 | — (0 rows) | validator's all-words rule killed every paraphrase |
| CONTRADICTS | 1.000 | 1.000 | — (0 rows) | same |
| SUPERSEDES | 0.911 | 0.818 | 0.877 | |
| CORRECTS | 0.893 | 0.779 | 1.000 in-corpus — and **over-predicted on the holdout** | 5 of 7 naturalized holdout misses say CORRECTS ("I live in Cambridge now" → CORRECTS); 9 marker-saturated training rows taught "statement of change = correction" |

**What is NOT supported**: adoption, exactly as before. False-supersede on unseen surfaces is
0.129–0.171 against incumbents at 0.000–0.058 — the budget gate fails everywhere; UNCERTAIN is
dead in the naturalized corpus; two classes are absent from it entirely; and both incumbent
comparisons straddle zero. The verdict is about the *data direction*, not the model: surface
diversity transfers, template volume does not, and the next corpus needs the fixes recorded at
the phase-4 checkpoint (values without label markers, a value-preservation check that accepts
natural paraphrase, an UNCERTAIN/CONTRADICTS/DUPLICATE path through quarantine, a 7B-class
verbalizer) plus more than 120 events. Those findings came from the pre-training audit and the
holdout — none of them is a patch applied to this corpus after seeing its scores.

Reproduction: `training/supersession/reject_invalid.py` over the committed artifacts, then
`adapt_synthetic_life.py <artifact>`, the eval tool's `--only corpus` stamp, and
`train.py --develop synthlife --fold-on corpus|template`. The naturalized artifact itself is
unseeded LLM output; `artifacts/` is gitignored for storage reasons, so it exists only on this
machine. Holdout fits are identical across
fold modes by construction, and matched exactly — a consistency check the runs passed.

## Order of work (original, for the record)

1. Pair capture (§7) — it gates the base rate, the adjudication queue, and the real test set.
2. The generator for the six labels, with the regression set frozen as held-out gold.
3. MSC audit (adapter + fixtures + label-derivation audit, same discipline as DialogueNLI).
4. Relabel DialogueNLI through the cardinality mapping into weak `coexist`/`duplicate`/`unrelated`.
5. Train, calibrate, CV. 6. Shadow with the full taxonomy subject. 7. Adjudicate, retrain, decide.
