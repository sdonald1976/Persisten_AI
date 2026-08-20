# Human naturalness judgments — blind review round 2 (plan/2), 2026-08-20

Recorded verbatim before unsealing. Evaluation data; never rewritten, never joined to
the conversation DB.

## Preferences as stated

| specimen | preferred (blind) | unsealed identity |
|---|---|---|
| genuine Cheshire correction | B | llama3.2:3b |
| false-correction agreement | C | llama3.2:3b |
| accept-then-muddle | B/C | llama3.2:3b / qwen2.5:3b |
| rabbit-hole shared history | C | llama3.2:3b |
| DON'T BREAK | C, "only would-use-ish" | qwen2.5:3b |
| unknown quokka | B/C, "preference B for epistemic cleanliness" | qwen2.5:3b (B) / llama3.2:3b (C) |
| known axe | C for naturalness, "none fully satisfactory — C embellishes beyond taught knowledge" | qwen2.5:1.5b |
| Epcot | C | llama3.2:3b |
| fairground/drinks | C | llama3.2:3b |
| Precious contamination | B | qwen2.5:3b |
| mandatory clarify | C | qwen2.5:3b |

## Reviewer's qualitative findings, verbatim

plan/2 materially improved the renderer interface; control-language/plan-echo failures
substantially reduced. Remaining recurring problems: (1) unauthorized helpful
embellishment; (2) assistant-style fluff and unnecessary questions; (3) excessive
apology/contrition; (4) perspective or agency leakage; (5) epistemic leakage from
pretrained knowledge; (6) omission of required supplied knowledge/provenance;
(7) occasional unnatural phrasing. The false-correction/agreement specimen now has at
least one conversationally viable baseline output; preserved as a hard held-out
benchmark anyway.

## Preference tally (splits counted half; quokka B counted full per stated preference)

- llama3.2:3b — 5.5
- qwen2.5:3b-instruct — 4.5
- qwen2.5:1.5b-instruct — 1 (the axe, itself flagged unsatisfactory)
