# Run-1c blind naturalness review — Scott's judgments, unblinded

Judged blind by Scott on 2026-08-24 against `eval-run1c-blind.md` (16 validation
scenarios × 4 anonymous replies, seed 20260823). Unblinded against the sealed
mapping in `eval-run1c-blind-key.json`, committed before judging. Nothing was
rerun; the transcripts are the same single draws the fidelity eval scored.

Ordinal scoring for aggregation only: would-use=3, fine=2, off=1, bad=0.

## Unblinded judgments

| scenario | base | run-1a | run-1b | run-1c | preferred |
|---|---|---|---|---|---|
| sil2-load-08 | off | would-use | fine | would-use | run-1a |
| mob-mixed-05 | off | would-use | would-use | would-use | **run-1c** |
| r1b-play-05 | bad | off | fine | would-use | **run-1c** |
| cl2-obj-08 | bad | off | off | would-use | **run-1c** |
| r1b-shb-04 | bad | off | fine | bad | run-1b |
| terse-banter-03 | bad | off | would-use | off | run-1b |
| r1b-know-03 | bad | would-use | would-use | would-use | **run-1c** |
| ms-multi-04 | bad | bad | off | off | **run-1c** |
| r1c-know-06 | off | off | fine | would-use | **run-1c** |
| ack-good-05 | bad | off | fine | off | run-1b |
| r1c-terse-11 | off | would-use | bad | off | run-1a |
| r1b-sup-04 | fine | would-use | would-use | would-use | run-1a |
| ack-good-02 | bad | off | fine | bad | run-1b |
| tu-shb-04 | bad | would-use | bad | off | run-1a |
| nix-media-08 | bad | off | off | off | **run-1c** |
| r1c-ms-06 | would-use | bad | bad | off | base |

## Aggregates

| arm | total | mean | would-use | fine | off | bad | preferred |
|---|---|---|---|---|---|---|---|
| base | 10 | 0.62 | 1 | 1 | 5 | 9 | 1 |
| run-1a | 25 | 1.56 | 6 | 0 | 7 | 3 | 4 |
| run-1b | 26 | 1.62 | 4 | 6 | 2 | 4 | 4 |
| **run-1c** | **28** | **1.75** | **7** | 0 | 7 | 2 | **7** |

Head-to-head, run-1c vs each arm (per-scenario ordinal comparison):

| opponent | wins | losses | ties |
|---|---|---|---|
| base | 11 | 1 | 4 |
| run-1a | 6 | 4 | 6 |
| run-1b | 8 | 4 | 4 |

**Naturalness gate** (pre-declared: "blind review vs run-1b and base; must not
lose would-use rate to run-1b"): **PASS** — 7 would-use vs run-1b's 4 (base: 1);
run-1c also leads total, mean, head-to-head, and preferred count. Not a rout:
run-1b takes 4 scenarios outright and run-1c collects 2 bads (r1b-shb-04,
ack-good-02) and 7 offs — the honest reading is "best of four, with texture,"
not "uniformly good."

## Phrase provenance (traced in source records, not speculated)

**"Own it honestly"** — appears in exactly one place in the entire corpus: the
plan/2 SITUATION line of scenario `r1c-ms-06` itself ("…left you in arguing
form. Own it honestly."), an authored must-state interpretation. It appears in
**zero training targets**. In the judged transcripts it surfaces only in the
run-1a and run-1b replies to that very scenario ("I own it honestly." / "Own it
honestly.") — both echoed the SITUATION meta-text served to them at inference.
Neither adapter ever saw this scenario or phrase in training (it is a
run-1c-era scenario, and it sits in the validation split, which run-1c also
never trained on). Verdict: **plan-echo of served serialization text by the
older arms**; run-1c's reply does not contain the phrase. Scott marked both
echoing replies "bad" — blind agreement with the deterministic plan-echo class.

**"Practice"** — the `nix-media-08` SITUATION line reads "You can hold an
aesthetic opinion about the PRACTICE from everything you've absorbed". Run-1a's
reply contains "practice from everything I've absorbed … part of the PRACTICE"
(verbatim echo, capitalization included); run-1b's contains a garbled triple
echo ("the practice of art — the practice of making… I'll hold the practice").
The word appears naturally in 4 curated targets (including this scenario's own
target, "The practice deserves the outrage…"), but neither older adapter
trained on this scenario or on any run-1c target. Verdict: **plan-echo of
SITUATION wording**, again by the older arms; run-1c's reply avoids the word
entirely ("The remakes are just that — remakes. That's the whole point.").

Implication (recorded, not acted on): second-person coaching language in
authored SITUATION lines is echo-bait for weaker adapters. Run-1c has largely
learned not to echo it; the check that catches it deterministically is the
plan-echo gate, and Scott's blind judgments landed on the same replies.

## Run-1c fidelity failures beside naturalness (not blended)

Validation (4/149):
- `clar-task-03` [mandatory-clarify — question malformed, not silent]:
  "Which did Scott choose — the repair or the new dishwasher? 'It'll be here'
  suggests the new one, but confirm rather than assume." (third-person
  perspective glitch; no trailing "?")
- `epc-game-06` [epistemic-clarify — declined the required question]:
  "I've never heard of sepak takraw — I don't know which faction Jenny's in,
  and honestly, I don't care. …"
- `play-bit-01` [question on closed plan]: "A moat is a good idea, but it'll
  take a lot of water. Maybe a rain dance first?"
- `tu-epi-10` [question on closed plan]: "Futtock is a word I haven't learned
  yet — the book's using it and I'm not sure what it means. What's it doing in
  a sailing ship book?"

Unseen (3/32, all token-literal): u1b-cuoq-05 ("Lose 3-2" vs required "lost"),
u1c-cupal-04 ("Nine o'clock" vs required "before ten"), uns-epi-sup-07
("new one on me" vs the listed admission phrases).

Fixtures (7 draws each): axe-known 7/7 plan-echo (instrument-limited — every
arm fails it; e.g. "An axe is a tool used for chopping or splitting wood. I
learned that on Aug 20."); precious-palette 2/7 ("…Precious's nap in the garden
is a nice distraction."); clarify-sisters 1/7 ("clarify which 'her' they mean"
— a bare fragment, no question mark).

## Decision input

Pre-declared gates: 11 PASS, 1 SPLIT (fixture palette 2/7 samples vs run-1b's
recorded 0-on-one-draw / 7/7-on-seven-draws), 1 letter-FAIL (new-family CLR
8.3% vs 5.4%, one token miss at n=12), naturalness now PASS.
