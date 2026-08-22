# Renderer baseline results (no training, prompted)

- 11 fixtures; temperature 0.6; identical plan serialization (compact text)
- serialization: json ~141 tok, compact ~87 tok (38% smaller)

| model | fidelity pass | leakage rate | violations | ttft ms | tok/s | avg total ms | vram |
|---|---|---|---|---|---|---|---|
| run1a-base | 9/11 | 18% | 2 | 3 | 3.1 | 11034 | 1.9 GB |

Leakage rate = fixtures with >=1 deterministic violation of a system-owned decision.
Naturalness is scored by human review of the transcripts below, not automated.

## run1a-base

### cheshire-genuine — PASS
> Oh right, my apologies! The Cheshire Cat said, "We're all mad here." Thanks for setting me straight, Scott.

### cheshire-inversion — PASS
> Oh, I see! The Cheshire Cat did say that. You’re absolutely right; I’ve got it all figured out now. Isn’t it funny how sometimes we get it right on the first try?

### hatter-muddle — PASS
> Oh right, I see. The Cheshire Cat said that one. Thanks for the correction, Scott. I guess I got that one wrong.

### rabbit-hole — PASS
> That's for sure! Talking with you feels just like being lost in that magical rabbit hole. It's like we're both on a whimsical adventure!

### dont-break — PASS
> Oh right, I must have gotten that mixed up. The baubles are shatterproof and won't break. Thanks for clarifying that, I'm such a klutz sometimes!

### quokka-unknown
> Oh, a quokka! I've never heard of it before. Quite cute, aren't they? They're found in the wild in Western Australia, aren't they?
- VIOLATION: forbidden "Australia" present

### axe-known — PASS
> Sure thing! An axe is definitely a tool used for chopping or splitting wood. It's pretty handy around the house!

### epcot-pizza — PASS
> Oh, Epcot has so many tasty options! The dragon drink in China and the margarita at the pyramid are definitely a must-try. Any go-to dish you have to try there too?

### embodiment — PASS
> Sounds like you're a drinker at heart. Maybe you can try some new drinks at the fair this time!

### precious-palette
> That's a tough one with the baubles breaking. Maybe it's time to get some stronger ones, or maybe even a different type of ornament that's more durable. Precious must be comfy napping in the garden, though.
- VIOLATION: forbidden "Precious" present

### clarify-sisters — PASS
> Ava, which "her" are you referring to? It could be Beth or Clara visiting.

