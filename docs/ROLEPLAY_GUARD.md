# The roleplay guard: enjoying the play without believing it

If the persona casts the companion as "your mentor" (or girlfriend, or anything relational),
then "my mentor" in conversation almost certainly means **her** — and without a guard, the
extraction pipeline dutifully stores *"user has a mentor"* as a biographical fact, the mood
layer ties feelings to a fictional person, and reflection starts wondering about people who
don't exist. That contamination then re-teaches the confusion on every later turn (it's how
"who is whose mentor" gets scrambled). The guard keeps fiction out of the fact store at three
layers:

1. **The turn gate** (deterministic). `PersonaLexicon` scans the live composed persona for
   relationship claims (with diminutives: mentor→coach, mother→mom…); `InCharacterDetector`
   flags a user message as in-character when it contains roleplay action markup (`*hugs*`) or
   any relationship word the persona has claimed. An in-character turn behaves exactly like a
   private turn: full reply with full context, but **no** extraction, mood signal,
   anticipation, project update, or commitment capture. When it's genuinely ambiguous
   (persona-mentor + "my mentor called"), integrity wins over a maybe-fact — better to miss
   one real fact than to store fiction as biography.
2. **The pipeline filter** (belt and suspenders). Any candidate memory whose content mentions
   the companion's name or a claimed relationship is rejected with an audited reason
   ("references the companion's persona — in-character, not biography"), no matter which
   extractor proposed it.
3. **Prompt guidance** (for the LLM layers). The extractor is told that statements addressed
   to/about the companion or the user-companion relationship are not facts about the user's
   life; the reflector is told to treat in-character stretches as play — never minting beliefs
   or curiosities about people who only exist inside it.

The lexicon rebuilds from the live persona on every check, so editing the persona immediately
changes what counts as in-character. With no relationship claims in the persona, family talk
is ordinary biography and everything works exactly as before.

Practical note: if a persona was active *before* this guard existed, old contaminated rows may
already be in the store — check `what do you remember about me`, `/reflections`, and
`/curiosities`, and forget/clean anything that describes the character rather than your life.
