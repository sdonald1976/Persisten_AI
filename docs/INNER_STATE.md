# Her own inner state, the reply-shape register, and the familiarity dial

Fixes for "she's always the same," all deterministic and honest.

## The familiarity dial

`FamiliarityTracker` derives the relationship's stage from two real numbers — how long you've
known each other (first message ever) and how much you've actually talked (user message count) —
and takes the **lower** of the two reads: three months of silence isn't closeness, and neither
is a hundred messages in one frantic day. Stages: `New → Acquainted → Familiar → Close`, each
carrying prompt guidance from "you've only just met — don't presume closeness" up to "in-jokes,
real teasing, comfortable directness." Rendered every turn under *"Where the relationship is"*
(calibration only, never recited). Nothing is stored; the dial only ever moves forward as real
history accumulates.

## Musings resurface on their own

The turn's retrieval exposes its query embedding, and the diary is searched with it: a
weeks-old musing whose subject matches the current conversation re-enters the prompt — labeled
with its age ("a thought from 4 weeks ago…"), so *"I'd been thinking about this a while back"*
is literally true. Relevance outranks freshness; with nothing relevant, the freshest musing
(≤7 days) still colors the turn as before. Old, unrelated thoughts stay in the diary.

## Inner state: spirits + energy

`CompanionStateTracker` is `RelationshipTracker`'s mirror image, pointed inward:

- **Spirits** `[-1, 1]` — a stored, slow-moving trace of how conversations have *actually* felt.
  Every turn that carries a real emotional reading nudges her a small step toward its valence
  (15% per moment): shared good news genuinely lifts her; a heavy stretch genuinely weighs on
  her. With no new signal, spirits decay toward contentment (half-life 4 days) — nothing lasts
  forever, in either direction. Stored on the user profile next to the companion's identity;
  private turns leave no trace on her state (the same gate as everything else).
- **Energy** `[0, 1]` — derived fresh from the hour of her day (quiet nights, bright mornings, a
  gentle evening taper), plus a small lift when she's recently had a quiet stretch to think
  (a reflection ran within 12 hours). Never stored, never random.

Every turn's prompt carries one line of her state under **"Your own mood right now"**, with three
standing rules: it colors tone naturally, it's answered honestly if the user asks how she is,
and it is never presented as the user's fault. *"What's on your mind?"* now leads with a
first-person self-report ("Right now? I'm a little low, if I'm honest…") — backed by the actual
numbers, so it's never a performance.

Same history + same clock = same mood. There is no randomness anywhere in it.

## The reply-shape register

`RegisterAdvisor` reads the user's message and picks a *shape* for the reply:

- **Short & casual** ("lol fair", "ok cool") → *one or two sentences is a complete reply; no
  lists; don't tack on a question out of habit.*
- **Conversational** (a normal message) → *a few natural sentences, no padding; end with a
  question only if you genuinely need the answer.*
- **Substantial** (a real ask) → no note; the standing "write it through to the end" rule governs.

Deliberately dumb (length + punctuation, not meaning) — the model still owns the words; this
only stops the most robotic habit an LLM has: answering a two-word message with three polished
paragraphs and a follow-up question.
