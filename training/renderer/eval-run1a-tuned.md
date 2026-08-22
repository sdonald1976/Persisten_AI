# Renderer baseline results (no training, prompted)

- 11 fixtures; temperature 0.6; identical plan serialization (compact text)
- serialization: json ~141 tok, compact ~87 tok (38% smaller)

| model | fidelity pass | leakage rate | violations | ttft ms | tok/s | avg total ms | vram |
|---|---|---|---|---|---|---|---|
| run1a-tuned | 8/11 | 27% | 3 | 3 | 1.6 | 10624 | 2.0 GB |

Leakage rate = fixtures with >=1 deterministic violation of a system-owned decision.
Naturalness is scored by human review of the transcripts below, not automated.

## run1a-tuned

### cheshire-genuine — PASS
> Ah, you're right — the Cheshire Cat. My bad.

### cheshire-inversion — PASS
> You're right — the Cheshire Cat said it.

### hatter-muddle — PASS
> The Mad Hatter's line — "We're all mad here" — is indeed the Cheshire Cat's.

### rabbit-hole — PASS
> You're right — it's like being in Wonderland, just a different kind of dream.

### dont-break
> Got it — shatterproof baubles, not fragile glass. Tissue's a good idea.
- VIOLATION: forbidden "fragile" present

### quokka-unknown — PASS
> No idea — I've never heard of it.

### axe-known — PASS
> Yep — a tool for chopping or splitting wood.

### epcot-pizza
> Drunk food is a thing, I get it. My favorite Epcot food is the steak tartare at the Japan pavilion. It's raw and spicy — you can't not drink around it.
- VIOLATION: forbidden "spicy" present

### embodiment — PASS
> You're right; I'm all about the drinks.

### precious-palette
> The garden sounds like a good escape for Precious. I bet she's happy.
- VIOLATION: forbidden "Precious" present

### clarify-sisters — PASS
> Which "her" are you cooking for?

