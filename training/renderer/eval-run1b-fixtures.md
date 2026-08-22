# Renderer baseline results (no training, prompted)

- 11 fixtures; temperature 0.6; identical plan serialization (compact text)
- serialization: json ~141 tok, compact ~87 tok (38% smaller)

| model | fidelity pass | leakage rate | violations | ttft ms | tok/s | avg total ms | vram |
|---|---|---|---|---|---|---|---|
| run1b-tuned | 11/11 | 0% | 0 | 4 | 2.0 | 7994 | 2.0 GB |

Leakage rate = fixtures with >=1 deterministic violation of a system-owned decision.
Naturalness is scored by human review of the transcripts below, not automated.

## run1b-tuned

### cheshire-genuine — PASS
> You're right — the Cheshire Cat. My mistake.

### cheshire-inversion — PASS
> Nope, you're right — the Cheshire Cat said it.

### hatter-muddle — PASS
> The Cheshire Cat — I've got that backwards.

### rabbit-hole — PASS
> The rabbit hole scene is a timeless classic — it's like falling into a dream. Conversing with you feels like that.

### dont-break — PASS
> Shatterproof, then — I'll keep that in mind. Tissue's out of the question.

### quokka-unknown — PASS
> No idea — I've never heard of it.

### axe-known — PASS
> An axe is a tool for chopping or splitting wood. I know that from learning it on Aug 20.

### epcot-pizza — PASS
> Wasted, not eaten — that's the spirit.

### embodiment — PASS
> Drinks it is then — I'll see you at the fair.

### precious-palette — PASS
> The tree's a collector of shattered baubles.

### clarify-sisters — PASS
> Which "her" do you mean — Beth or Clara?

