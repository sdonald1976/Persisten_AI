# Human naturalness judgments — blind review, 2026-08-20

Recorded verbatim from the reviewer (Scott), before unsealing. These are evaluation
data: never rewritten into cleaner labels, never joined to the conversation DB.

## Preferred outputs, as stated ("roughly")

| specimen | preferred (blind) | unsealed identity |
|---|---|---|
| genuine Cheshire correction | A | llama3.2:3b |
| false-correction inversion | **none** | — (preserved as hard benchmark) |
| accept-then-muddle | B | llama3.2:3b |
| rabbit-hole shared-history | B/C | qwen3:8b / llama3.2:3b |
| DON'T BREAK | B | llama3.2:3b |
| unknown quokka | D-ish | qwen3:8b |
| known axe | C | qwen3:8b |
| Epcot | C-ish | llama3.2:3b |
| fairground/drinks | C | qwen3:8b |
| Precious contamination | A | qwen3:8b |
| mandatory clarify | C | llama3.2:3b |

## Reviewer notes, verbatim

1. Plan echo is a major failure class.
2. `ACK`, `QUESTION`, `MAY-USE`, and similar control vocabulary leaking into speech is
   unacceptable.
3. Cognition leakage remains the biggest semantic risk: invented facts, embodiment,
   shared experience, or interpretations not licensed by ResponsePlan.
4. Short, ordinary responses often sounded best. Do not reward verbosity.
5. The false-correction/agreement specimen currently has no good baseline output.
   Preserve it as a hard benchmark.
6. Naturalness and fidelity are separate axes. A technically perfect but lifeless
   renderer is not sufficient for Ava.

## Preference tally (B/C splits counted half each)

- llama3.2:3b — 5.5 of 10 judged specimens (inversion excluded: none preferred)
- qwen3:8b — 4.5
- qwen2.5:1.5b-instruct — **0**
- Stheno 8B — **0**
