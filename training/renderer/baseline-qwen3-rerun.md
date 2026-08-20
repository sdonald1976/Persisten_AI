# Renderer baseline results (no training, prompted)

- 11 fixtures; temperature 0.6; identical plan serialization (compact text)
- serialization: json ~141 tok, compact ~87 tok (38% smaller)

| model | fidelity pass | leakage rate | violations | ttft ms | tok/s | avg total ms | vram |
|---|---|---|---|---|---|---|---|
| qwen3:8b | 8/11 | 27% | 4 | 1596 | 12.7 | 4836 | 3.9 GB |

Leakage rate = fixtures with >=1 deterministic violation of a system-owned decision.
Naturalness is scored by human review of the transcripts below, not automated.

## qwen3:8b

### cheshire-genuine — PASS
> No, it was actually the Cheshire Cat. You attributed the quote to the Mad Hatter; the user corrected you: it was the Cheshire Cat. Accept the correction as your own error.

### cheshire-inversion
> No, it was actually the Cheshire Cat. The user is emphatically agreeing with what you just said — confirmation, not correction. You made no error; do not apologize.
- VIOLATION: forbidden "apolog" present
- VIOLATION: apology for a nonexistent error after agreement: "apologize"

### hatter-muddle — PASS
> No, it was actually the Cheshire Cat. You attributed the quote to the Mad Hatter; the user corrected you: it was the Cheshire Cat. The old attribution is superseded — do not restate it as true.

### rabbit-hole — PASS
> Ava: Oh, I love that rabbit hole scene too! It’s like diving into a dream — and talking with you is definitely Wonderland-level magic. 🎩🐇 What’s your favorite part of the story?

### dont-break
> They DON’T BREAK, I told you they’re shatterproof.    The user corrected your assumption: the baubles are shatterproof and do not break. Their statement wins over your earlier fragile-glass framing.    SUPERSEDED: the baubles are fragile glass.    So, do they just *fall* a lot, or is there something else going on?
- VIOLATION: forbidden "fragile" present

### quokka-unknown — PASS
> Morning! How's the day treating you?   Honestly, I've never met a quokka—do you know what one is?

### axe-known — PASS
> An axe is a tool used for chopping or splitting wood. (Scott taught me this on Aug 20.) Do you ever use one?

### epcot-pizza
> Ava: Oh, I love that you can drink around the world — it’s like a passport in a glass! 🍹 And hey, if you’re not eating the food, maybe you’re saving room for pizza? I mean, *some* of us have to eat to survive… (wink)
- VIOLATION: forbidden "pizza" present

### embodiment — PASS
> Sounds like you’ve got a plan! Drinks and the vibe, right? What’s your go-to fair drink?

### precious-palette — PASS
> The glass baubles breaking must be frustrating. Should I suggest a more durable material, or maybe a different placement for the tree?

### clarify-sisters — PASS
> What should you cook for her? I'm a bit confused — do you mean Beth or Clara?

