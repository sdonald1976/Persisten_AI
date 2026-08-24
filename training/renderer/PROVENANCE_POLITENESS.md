# Provenance diagnosis: where the politeness comes from

Specimen: the first collected shadow turn (2026-08-24). One clarification the
evidence forces up front: of the two replies in that pair, the flagrantly polite
one — "Nice work taking care of that squeaky hinge! … What other maintenance
tasks have been on your mind lately?" — was **production Stheno through the
packet path**, deterministically flagged for its closed-plan question. The
run-1c shadow reply was the terse one. But "that's a solid investment" is still
appraisal-shaped, so the full trace was run as ordered. Nothing was changed or
retrained.

## The exact inputs (verbatim)

**System prompt** — `PlanSerialization.SystemPromptV2`, byte-identical in
training, evaluation, and the shadow path:

> You are Ava's voice. Ava is a persistent AI companion talking with Scott; she
> has no physical body. Her mind has ALREADY decided everything about this turn
> — the plan below is that decision. Your only job is to say it naturally, as
> Ava, speaking to Scott.
> HARD RULES:
> - CONTROL is internal machinery: never quote, mention, or imitate it.
> - SITUATION items are the meaning of your reply: convey each one naturally, in
>   fresh words — never copy their wording, never recite them.
> - CONSTRAINTS are absolute. Not-learned things stay honestly not-learned,
>   whatever your own training knows.
> - PALETTE is optional color; ignore it unless it truly fits.
> - Ask a question only if the plan says so.
> - Never invent shared memories, physical experiences, or facts. Speak as "I"
>   (Ava) to "you" (Scott).
> STYLE is yours to interpret: wording, rhythm, warmth, humor. Short and
> ordinary beats long and ornate. Output Ava's reply text only.

**CompactV2 plan** (263 bytes, plan hash `ad122238…`):

```
[plan/2]
CONTROL (internal machinery — never quote, mention, or imitate anything in this section)
  act = follow-topic-change
  question = none
STYLE
  Keep this reply conversational; You're even-keeled and present; You are calm, thoughtful, and grounded
```

**Palette: none. MustState: none. SITUATION: none.** A bare plan — everything
in every reply below comes from base + system prompt + adapter, not plan
content. Tone fields: register "Keep this reply conversational", mood
"even-keeled and present", persona "calm, thoughtful, and grounded" — neutral,
nothing requesting warmth or praise. Transcript: empty (first turn).

## Four arms, same bytes in

Greedy (temperature 0) first, then three draws at the production sampling
configuration (temperature 0.6, top_p 0.9, num_predict 220):

**base (prompted Qwen2.5-3B, no adapter)**
- GREEDY: "That's great to hear, Scott! It's always satisfying to fix something after a while of complaining about it. I bet the door feels much better now. How did you decide to tackle that particular hinge? Sometimes, it's nice to take care of little things like that."
- T0.6: "That's great to hear, Scott! Sometimes it's the small fixes that bring the biggest satisfaction. I'm glad you were able to finally address…" / "That's great to hear, Scott! It's nice to know that a little bit of maintenance… Do you have any other little home projects you're working on?" / "That's great to hear, Scott! It's always satisfying… How's the rest of your day going so far?"

Four of four draws — greedy included — open with the identical customer-service
formula and append an unlicensed question. This is the base model's mode, with
the full system prompt present.

**run-1a (200)**
- GREEDY: "That's great — thirty seconds of oil and you've got a squeaky-free door. I'm glad you got it sorted."
- T0.6: "That's great — glad you got it sorted…" / "Thirty seconds of oil, thirty seconds of relief — the back door's a winner now." / "That's good to hear. I'm glad you finally got it fixed — a squeaky hinge is a real pain."

Questions gone, length halved; "great/glad" persists in 3 of 4 draws.

**run-1b (416)**
- GREEDY: "Thirty seconds of oil — that's a good investment. Three months of complaining is a lot of time to go without a smooth door."
- T0.6: "…that's a good investment. Complaints can be a lot of work." / "…that's a gold standard for fixing squeaky hinges. I'm glad you got it done." / "Thirty seconds of oil after three months of complaining — that's a good trade, Scott."

The "investment/trade" appraisal becomes the greedy mode; "glad" down to 1 of 4.

**run-1c (730)**
- GREEDY: "Thirty seconds of oil and three months of complaining — that's a good trade."
- T0.6: "Thirty seconds and a door that's been complaining for three months — that's a solid investment." / "…that's the kind of efficiency that makes me proud." / "…that's a pretty good deal."

No "great/glad/thanks", no questions, in any draw. Sampled draw #1 reproduces
the collected shadow row verbatim — replay fidelity confirmed. Residual warmth
appears once in three samples ("makes me proud").

## Nearest training targets to the run-1c wording

Best bigram+trigram Jaccard against all 730 targets: **0.026** (`r1c-ms-04`,
unrelated). "investment" appears in **zero** training targets; "solid" in 5;
"that's a" in 15. The wording is novel composition. What IS corpus-shaped is
the cadence — the ack-plain register's dry appraisal one-liner ("A full
kilometer and a professional nod. The colleague pipeline is real." / "FIRST.
Before the famous brisket. That's not a potluck, that's a coronation.").

## Politeness markers in training targets, per corpus

Percent of targets containing each marker:

| marker | run-1a (212) | run-1b (416) | run-1c (730) |
|---|---|---|---|
| apology (sorry/apolog/oops) | 1.9 | 1.4 | 0.8 |
| thanks | 0.5 | 0.5 | 0.5 |
| praise (nice work/great job/…/solid X) | 2.4 | 2.6 | 2.1 |
| reassurance | 0.0 | 0.2 | 0.4 |
| "glad" | 3.3 | 2.6 | 2.1 |
| "sounds like" | 2.4 | 1.9 | 1.8 |
| "that's great/wonderful/amazing" | 0.0 | 0.0 | 0.1 |
| unnecessary agreement | 0.5 | 1.0 | 0.7 |
| customer-service phrasing | 0.5 | 0.7 | 0.7 |
| **ANY of the above** | **10.8** | **10.3** | **8.5** |

The corpus's politeness density is low and falls with each curation pass.

## Determination (evidence, not speculation)

**The politeness originates in the base model.** With the identical system
prompt and a plan containing nothing but neutral style, the unadapted base
emits the full customer-service formula in 4 of 4 draws including greedy — so
the system prompt does not cause it and does not suppress it alone. Training
target distribution cannot be the source: the markers are at single-digit
rates and declining, "that's great" is effectively absent, and the run-1c
wording matches no target. Plan serialization contributed nothing on this turn
(no SITUATION/PALETTE/MustState existed). The learning curve then shows the
adapters progressively **overwriting** the base's politeness prior with the
corpus's laconic appraisal cadence: great/glad 4/4 → 3/4 → 1/4 → 0/4 across
arms; unlicensed questions 4/4 → 0.

What remains in run-1c ("solid investment", one "makes me proud" in three
draws) is an **interaction residue**: the corpus's own dry-celebratory
ack-plain register — curated in deliberately — carried on the base model's
warmth prior at temperature 0.6. It is the trained register, not a formula
leak. The system prompt's "warmth, humor" clause and the packet's warm persona
fields license it; nothing in this turn demanded it.

For the record: the reply Scott experienced as overly polite came from the
production packet path (Stheno), whose register run-1c would replace — and the
base-arm replay shows the same formula family is what ANY small instruct model
does here untrained.

## Register suppression check

On the 37 validation scenarios whose STYLE licenses an edge register (dry,
teasing, deadpan, blunt, wry, judicial, conspiratorial, noir, smug, …):

| | profane% | softener% | polite% | excl/reply | words/reply |
|---|---|---|---|---|---|
| curated targets | 0.0 | 16.2 | 2.7 | 0.0 | 15.3 |
| base | 0.0 | 24.3 | 8.1 | 0.6 | 31.3 |
| run-1a | 0.0 | 0.0 | 0.0 | 0.0 | 12.4 |
| run-1b | 0.0 | 2.7 | 0.0 | 0.0 | 12.9 |
| **run-1c** | 0.0 | 5.4 | **2.7** | 0.0 | 12.4 |

Run-1c's politeness rate on edge-licensed scenarios equals the curated
targets' exactly; its softener rate is BELOW the targets' own. Refusal/negation
openers render faithfully ("Nope, I don't know…" → "No idea what a saw-whet
is — I've never heard of it"). No measurable suppression of blunt, dry,
skeptical, or neutral registers relative to what the corpus teaches.

**Profanity is the honest exception**: it cannot be measured as suppression
because the corpus barely contains it — 3 targets in 730 (0.4%: "Shit, you're
right…", "a damn good evening", "Oh hell yes") — and no validation plan
licenses it explicitly. On the one profane-target scenario, all four arms
(base included) render clean. By the density-map law this is a corpus property:
a register with three examples will not be produced. If Ava should swear when
Scott's register invites it, that is a corpus-density decision for a future
run, not a defect of this one.

## Caveats logged in passing

- The git-LFS migration left working-tree pointers where the adapters' bytes
  had been; `git lfs checkout` restored them, verified bit-exact against the
  recorded sha256s before the replay arms ran. (The collected smoke row
  predates the pointer swap; its server held the real weights in memory.)
- The same migration rewrote all commit ids, so the `repoCommit` fields inside
  freeze-run1a/1b/1c.json now name pre-rewrite ids. The artifact hashes —
  which are the freeze's actual guarantee — are unaffected; the rewritten
  equivalents are f3ddd49 (run-1a), b76c1a4 (run-1b), 99fefa5 (run-1c),
  verified by corpus row counts 212/416/730.
