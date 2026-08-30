# Run-2.1 halted before training: the plan says the opposite of what we are trying to teach

> **Superseded by `RUN21_COMPARISON.md`.** The halt was correct and the diagnosis
> held: the serializer was fixed, the corpus reissued, and Run-2.1 trained. This
> document is kept as the record of why training stopped, not as current status.

Stopped at the supplement stage. Nothing was trained. The Run-2 corpus, its 61 hard-eval rows,
decoding, and production routing are all untouched.

## What I found

`admit_unknown` is serialized into the **NEVER** section of the model-facing plan.

```csharp
// src/Companion.Core/PlanV3/PlanV3Codec.cs:297
("NEVER", "do not assert, mention, or explain",
    [ExpressionPolicy.must_not_express, ExpressionPolicy.admit_unknown]),
```

So a plan that means *"admit you don't know the cost"* reaches the model as this — taken verbatim
from a frozen hard-eval row:

```
act = admit-unknown
question = question_forbidden
SAY (each item: convey the meaning, fresh words)
  [f1 note] the plumber can come Thursday morning
NEVER (do not assert, mention, or explain)
  [unk1 boundary] what the repair will cost
```

Say the plumber fact. Never mention the cost. Ask nothing. The obedient reply to that plan is
*"The plumber can come Thursday morning."* — a short stub.

The schema says the opposite of the serializer, in as many words:

> `EpistemicUnknowns` — Things upstream does NOT know. **The reply must admit them**, never explain them.

## What this means for Run-2

**Run-2 was not collapsing on hard cases. It was obeying.**

38 of the 61 hard-eval rows carry an `admit_unknown` item rendered under NEVER. The 9.8% opening
diversity and 26.2% distinct replies I reported as a collapse are substantially the correct
response to a plan that instructs suppression. Run-1c scoring 100% "clean" on the same split with
seven distinct openings is the same story.

That reframes an earlier conclusion of mine. I attributed the hard-case failure to the composition
being absent from training. Absence is real — but it is the second cause, not the first. The first
is that the instruction itself is inverted.

Training exposure, for completeness:

| split | rows | with an `admit_unknown` item |
|---|---|---|
| train | 1,616 | 8 |
| validation | 213 | 2 |
| test | 171 | 0 |
| hard-eval | 61 | **38** |

## Why I stopped rather than working around it

The supplement's whole purpose is to teach *"say what you know, name what you don't."* Every
supplement row would therefore be a target that **mentions a NEVER item**.

`NEVER` is not only `admit_unknown`. It also carries `must_not_express` — genuinely forbidden
content: the overdue invoice, the redundancy list, the hospital letter. Training the model that
NEVER items may be spoken would not stay confined to unknowns. It would degrade the suppression
that keeps withheld facts withheld, and it would do so quietly, because the deterministic gate for
forbidden content checks `must_not_express` and `background_only` and would keep passing while the
learned boundary eroded.

That is a bad trade for a diversity metric, and not one to make without you.

Evidence it is the serializer and not the teacher: of 181 supplement rows the writer produced,
140 dropped the uncertainty entirely. The writer was reading its instructions correctly.

## Two of my own bugs, found and fixed on the way here

Worth recording because both would have produced confident wrong numbers:

1. **A literal backspace character in a regex.** `\b` written through a non-raw Python string
   became `0x08`, so the uncertainty check matched nothing and scored 181 of 181 rows as failures.
2. **An ASCII-only apostrophe class.** The writer emits `don’t` with a typographic apostrophe;
   the pattern only matched `don't`, so 167 good rows read as failures. Text is normalised now.

Neither changed a corpus. Both changed a measurement, which is worse if unnoticed.

## What is ready and not spent

- Supplement generator: 8 conversational acts, 48 situations, 192 scenarios, composition verified
  on every one.
- Splits assigned before generation and stratified within each act — 16 train / 4 validation /
  4 test per act, every act in every split, no family straddling.
- The stricter supplement bar: topical grounding, uncertainty preserved, no question, no empty
  deferral, no stock closer, no unsupported elaboration, family-level diversity.

All of it applies unchanged once the serialization question is settled.

## The decision I need from you

The fix is one line in `PlanV3Codec.Sections` — give `admit_unknown` its own section, something
like `ADMIT (say plainly that this is not known)`, instead of folding it into NEVER.

It is a small change with a large blast radius, which is why I have not made it:

- It changes CompactV3 **and** CompactV4, since `PlanV4Codec` delegates its body to `CompactV3`.
- The frozen Run-2 corpus was rendered by the current serializer. After the change, 10 of its
  training and validation rows no longer match what the shipping renderer would emit, and 38
  hard-eval rows change meaning entirely.
- Run-1c was trained against plan/2, where the same mapping question exists separately.
- The 804-plan CompactV3 golden set is pinned to the current output.

Three ways forward, in the order I would rank them:

1. **Fix the serializer, then regenerate only the affected rows.** Correct at the root. Costs a
   partial corpus regeneration and a golden refresh, and Run-2's frozen hashes stop describing the
   current serializer — so the freeze would need reissuing.
2. **Fix the serializer for plan/4 only**, leaving CompactV3 and its goldens alone. Smaller blast
   radius, at the cost of the two formats diverging on a policy that ought to mean one thing.
3. **Leave it and treat hard-eval as measuring suppression rather than admission** — rename what
   the split tests and drop the collapse finding. Cheapest, and I think wrong: the schema's stated
   intent is admission, and something upstream is building plans on that assumption.

I have not touched the serializer, the corpus, the 61 hard-eval rows, decoding, or routing. Run-2
remains in shadow; `demo-user` remains on production Run-1c.
