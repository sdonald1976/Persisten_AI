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

## Order of work (original, for the record)

1. Pair capture (§7) — it gates the base rate, the adjudication queue, and the real test set.
2. The generator for the six labels, with the regression set frozen as held-out gold.
3. MSC audit (adapter + fixtures + label-derivation audit, same discipline as DialogueNLI).
4. Relabel DialogueNLI through the cardinality mapping into weak `coexist`/`duplicate`/`unrelated`.
5. Train, calibrate, CV. 6. Shadow with the full taxonomy subject. 7. Adjudicate, retrain, decide.
