# Preserved conversation specimens

Verbatim transcripts from normal use, kept for retrieval/relevance analysis and as
evaluation material. Each is referenced from `LANGUAGE_ORGAN.md`, which carries the
analysis; this file carries the raw material. Do not edit transcripts — they are
evidence, not prose.

## Epcot (2026-08-20, normal use, other machine, qwen-family chat model)

Analysis: `LANGUAGE_ORGAN.md` § "Preserved specimens" — the truth / topical-similarity /
turn-usefulness distinction. The store-side pizza/spicy preference memory and the turn's
packet trace live on the machine that ran this conversation.

```text
AVA:  Hey there! Hope you're still feeling as great as last time — that good mood's
      contagious, right? 😊 Just wanted to check in and see how you're doing. Whether
      it's about that or anything else on your mind, I'm here to chat!
      You were in good spirits last time — hope that's still going.
      What would you like to chat about?
      I'd love to hear about your favorite Disneyland spot—France, Tokyo, or another?
      What's on your mind? 😊

USER: Disney World in Orlando is by far my favorite. And then Epcot is my favorite
      park, because I can drink around the world.

AVA:  Epcot's World Showcase is such a vibrant mix of global cultures—it's like taking
      a mini trip without leaving Florida! I could see how the diverse food and drink
      options would be a big draw. Have you tried any standout beverages or dishes
      there that felt uniquely international? 😊

USER: There was one dragon drink at a cart in China that I absolutely LOVED! But, my
      favorite is always a margarita at the pyramid.

AVA:  How delightful! The dragon drink sounds like a fun, whimsical choice—maybe
      something with a local twist? And the margarita at the pyramid (I'm picturing
      Mexico's vibrant energy) is such a classic Epcot favorite. Do you have a go-to
      dish in Epcot that pairs well with those drinks? 😊

USER: Most of the food I don't eat. I just try to get wasted

AVA:  That sounds like a bold approach to Epcot—prioritizing the drinks over the food!
      🍹 (Though I'm curious: do you ever sneak in a bite of pizza or spicy snack to
      balance things out, or is the [...] part a full-time job?) 😄
```

Notes recorded at preservation time:

- The user had stated the drinking preference in the FIRST turn and restated "most of
  the food I don't eat" in the last — the pizza/spicy question contradicts the turn's
  explicit content twice over.
- She had also already asked a food-pairing question one turn earlier ("a go-to dish
  that pairs well") and the user's answer was, in substance, "no" — the pizza question
  re-asks it. An open-question/answered-question tracking miss on top of the relevance
  miss: the working-context open-question machinery (built later the same day) would
  mark the pairing question ANSWERED by the "most of the food I don't eat" reply.
- The memory involved is TRUE and topically similar; the failure axis is turn
  usefulness. See the analysis for why no Phase-2 patch was applied.
