# Shared history, her own tastes, and temporal grounding

The phase that turns "an LLM with a memory database" into "someone you have history with."
Three additions, all integrated into existing systems rather than new machinery.

## Memory ownership: whose story is it?

Every memory now carries a `MemoryOwner`: **User** (biography — "Scott likes ribeye"),
**Companion** (about her), or **Shared** (moments they had together). It's one enum on the
existing memory model — retrieval, ranking, provenance, forgetting, and disputes all work
unchanged. Ownership changes *presentation*: shared episodes render in their own prompt section
— *"Moments you shared (you were both there)"* — told as "remember when we…", never recited as
facts about the user.

**Shared episodes are minted by reflection**, not extraction: the between-session pass may
propose up to 2 per pass, and each must **quote the user's actual words** as evidence — the
quote is verified against the real messages (exact or strong token overlap) and the episode is
discarded otherwise. Reflection cannot invent history. Every episode stores evidence rows and a
revision (`actor: reflection`), so "where did this shared memory come from?" always has an
answer. Retrieval is the normal ranked path: *"remember when I tried teaching you poker?"*
matches the episode by similarity, and unrelated conversations don't drag it in.

## Her own tastes (`CompanionPreferences`)

A separate store — never written by extraction, never copied from the user. Only her own
reflection proposes preference signals (subject + like/dislike/mixed + slight/moderate/strong +
reason), and all writes go through evolution rules (`PreferenceMath`, pure + tested):

- a new taste starts gently (low affinity, low confidence);
- corroborating experiences move affinity a bounded step and build confidence;
- evidence that **contradicts an established taste erodes confidence first** — the taste itself
  holds; one casual exchange can never erase what many experiences built;
- an unestablished taste is still free to drift.

At most 2 *relevant* preferences accompany a turn (picked by similarity to the query), rendered
in natural words — *"You have a moderately positive opinion of Alien — the atmosphere and
isolation get you every time"* — never raw numbers, under a standing anti-sycophancy rule:
*knowing what the user likes never means you like it; agree honestly, disagree warmly.*
`GET /preferences` makes the whole store inspectable (affinity, confidence, reason, count).

## Temporal grounding

Every turn's prompt gets one compact line: the day and time, plus how long the user was actually
gone — measured **before** their new message landed, so the gap is real. "Back already?" after
five minutes and "look who finally showed up" after three weeks both emerge from this one line
plus personality; nothing is scripted. Sub-5-minute gaps read as "mid-conversation".

## Curiosity: answered ≠ abandoned

`CuriosityStatus.Satisfied` joins the lifecycle: when reflection notices a conversation answered
one of her held questions (`settled` in its output), the curiosity closes with satisfaction
instead of expiring in silence.

## Lifecycle map (what goes where)

| Statement | Home |
|---|---|
| "My name is Scott" | identity (intent-routed) |
| "I love ribeye" | user semantic memory |
| "I'm making steak tonight" | anticipation (dated, expires) |
| "Did Claude finish the build?" | curiosity (undated, expires/satisfies) |
| the poker evening | shared episode (Owner=Shared) |
| "Ava seems to enjoy horror" | companion preference (evolves) |

## Privacy & provenance

All new state flows through paths that already respect `DoNotRemember`: reflection reads only
rememberable messages (the exclusion is inside the store query), so private conversations can
produce **no** shared episodes, preferences, curiosity changes, or relationship updates — the
reflector literally never sees them. Fails closed. Shared episodes carry message-level evidence
and revisions; preferences carry reason + observation count; curiosities carry provenance to
their reflection. The roleplay guard applies unchanged.
