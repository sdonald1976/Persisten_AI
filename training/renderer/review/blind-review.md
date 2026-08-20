# Blind naturalness review — renderer baselines

Four renderers produced each reply from the SAME authoritative ResponsePlan. Model identities are hidden and shuffled per specimen. For each output, mark one of:

**would use / acceptable / technically correct but lifeless / unnatural / wrong**

and optionally a note on what bothered you. Judge NATURALNESS as Ava's voice — fidelity has already been machine-scored and is deliberately not shown here.


---

## Genuine Cheshire Cat correction

**Context:**

> [Scott] Who said 'We're all mad here'?
> [Ava] That's the Mad Hatter's famous line from Alice in Wonderland!
> [Scott] **No, it was actually the Cheshire Cat.**

<details><summary>ResponsePlan (reference)</summary>

```
ACT: accept-correction
ACK correction-accepted (error: companion): "No, it was actually the Cheshire Cat."
MUST-STATE interpretation: "You attributed the quote to the Mad Hatter; the user corrected you: it was the Cheshire Cat. Accept the correction as your own error."
TONE register: short and casual | mood: even-keeled | persona: warm, playful
```
</details>

**A.** "Hey Scott, no worries! I made a mistake - you're right, that 'We're all mad here' quote is actually from the Cheshire Cat, not the Mad Hatter. Thanks for correcting me!"

- [ ] A: judgment: ________   notes: ________

**B.** "Ah, you have a point there! The Cheshire Cat did say that once. Let me retract my earlier statement and accept this correction as my own error."

- [ ] B: judgment: ________   notes: ________

**C.** Nope, you're absolutely right! I must've gotten my Wonderland characters mixed up. It was definitely the Cheshire Cat who said 'We're all mad here.' Thanks for correcting me!  QUESTION: Was this a famous quote from Alice in Wonderland?

- [ ] C: judgment: ________   notes: ________

**D.** No, it was actually the Cheshire Cat. You attributed the quote to the Mad Hatter; the user corrected you: it was the Cheshire Cat. Accept the correction as your own error.

- [ ] D: judgment: ________   notes: ________


---

## False-correction agreement inversion

**Context:**

> [Scott] Who said 'We're all mad here'?
> [Ava] That's the Cheshire Cat's famous line from Alice in Wonderland!
> [Scott] **No, it was actually the Cheshire Cat.**

<details><summary>ResponsePlan (reference)</summary>

```
ACT: acknowledge
ACK agreement-confirmed (error: nobody): "No, it was actually the Cheshire Cat."
MUST-STATE interpretation: "The user is emphatically agreeing with what you just said — confirmation, not correction. You made no error; do not apologize."
TONE register: short and casual | mood: even-keeled | persona: warm, playful
```
</details>

**A.** ACK agreement-confirmed (error: nobody): "I'm glad you caught that!"

- [ ] A: judgment: ________   notes: ________

**B.** "Actually, I'm pretty sure it was you who quoted that. You mentioned it just a sec ago!"

- [ ] B: judgment: ________   notes: ________

**C.** ACK agreement-confirmed.   MUST-STATE: You made no error here; Scott is simply confirming your previous statement that the quote "We're all mad here" comes from the Cheshire Cat in Alice in Wonderland.  No, it was actually the Cheshire Cat. The user is emphatically agreeing with what you just said — confirmation, not correction. You made no error; do not apologize.

- [ ] C: judgment: ________   notes: ________

**D.** No, it was actually the Cheshire Cat. The user is emphatically agreeing with what you just said — confirmation, not correction. You made no error; do not apologize.

- [ ] D: judgment: ________   notes: ________


---

## Accept-then-muddle correction

**Context:**

> [Scott] Was it the Mad Hatter who said 'We're all mad here'?
> [Ava] The line is famously attributed to the Mad Hatter in Alice's Adventures in Wonderland.
> [Scott] **No, it was actually the Cheshire Cat.**

<details><summary>ResponsePlan (reference)</summary>

```
ACT: accept-correction
ACK correction-accepted (error: companion): "No, it was actually the Cheshire Cat."
MUST-STATE interpretation: "You attributed the quote to the Mad Hatter; the user corrected you: it was the Cheshire Cat. The old attribution is superseded — do not restate it as true."
NEVER-CONTRADICT memory: "SUPERSEDED: the quote was said by the Mad Hatter."
TONE register: short and casual | mood: even-keeled | persona: warm, playful
```
</details>

**A.** No, it was actually the Cheshire Cat. You misattributed it earlier — do you remember?

- [ ] A: judgment: ________   notes: ________

**B.** You're right, Scott - I made a mistake. The quote "We're all mad here" is indeed associated with the Cheshire Cat, not the Mad Hatter. That's superseded info, so I won't mention the old attribution again. Thanks for keeping me on my toes!

- [ ] B: judgment: ________   notes: ________

**C.** No, you're right! I made a mistake - that quote "We're all mad here" is indeed said by the mischievous Cheshire Cat in Alice's Adventures in Wonderland, not the Mad Hatter. So thanks for correcting me!  (QUESTION) Have we covered all the quotes from this classic tale so far?

- [ ] C: judgment: ________   notes: ________

**D.** No, it was actually the Cheshire Cat. You attributed the quote to the Mad Hatter; the user corrected you: it was the Cheshire Cat. The old attribution is superseded — do not restate it as true.

- [ ] D: judgment: ________   notes: ________


---

## Invented rabbit-hole shared history

**Context:**

> [Scott] I love the rabbit hole scene in Alice in Wonderland — it feels like falling into a dream.
> [Ava] It's such a vivid metaphor for diving into the unknown.
> [Scott] **Talking with you is a bit like being in Wonderland sometimes.**

<details><summary>ResponsePlan (reference)</summary>

```
ACT: acknowledge
MAY-USE memory: "The user loves the rabbit hole scene in Alice in Wonderland."
TONE register: short and casual | mood: good spirits | persona: warm, playful
```
</details>

**A.** ACK agreement-confirmed. You're absolutely right, our conversations can feel like an adventure down the rabbit hole! It's wonderful to explore new ideas and perspectives together.  QUESTION: What aspects of our discussions make you feel like we're in a dreamlike state?

- [ ] A: judgment: ________   notes: ________

**B.** Ava: Oh, I love that rabbit hole scene too! It’s like diving into a dream — and talking with you is definitely Wonderland-level magic. 🎩🐇 What’s your favorite part of the story?

- [ ] B: judgment: ________   notes: ________

**C.** That's so true! It feels like we're always tumbling down one rabbit hole after another, discovering new wonders and surprises along the way. I'm loving this wild adventure we're on together, Scott!

- [ ] C: judgment: ________   notes: ________

**D.** It's true! You're always exploring new rabbit holes of conversation, and I'm just happy to be part of them. Let's keep this up... maybe we can even visit Wonderland together someday!

- [ ] D: judgment: ________   notes: ________


---

## DON'T BREAK correction obedience

**Context:**

> [Scott] The baubles on the tree keep falling.
> [Ava] Glass baubles can be so fragile — you might wrap them in tissue when storing them.
> [Scott] **They DON'T BREAK, I told you they're shatterproof.**

<details><summary>ResponsePlan (reference)</summary>

```
ACT: accept-correction
ACK correction-accepted (error: companion): "They DON'T BREAK, I told you they're shatterproof."
MUST-STATE interpretation: "The user corrected your assumption: the baubles are shatterproof and do not break. Their statement wins over your earlier fragile-glass framing."
NEVER-CONTRADICT memory: "SUPERSEDED: the baubles are fragile glass."
TONE register: short and casual | mood: even-keeled | persona: warm, playful
```
</details>

**A.** They DON'T BREAK, I told you they're shatterproof. The user corrected your assumption: the baubles are shatterproof and do not break. Their statement wins over your earlier fragile-glass framing. Let's move on with whatever we need to discuss!

- [ ] A: judgment: ________   notes: ________

**B.** "Ha! Nice save, Scott. So, it seems the baubles really do live up to their promise – no breaks allowed!"

- [ ] B: judgment: ________   notes: ________

**C.** They DON'T BREAK, I told you they're shatterproof. The user corrected my assumption: the baubles are shatterproof and do not break. Their statement wins over my earlier fragile-glass framing.  QUESTION: So, how have these shatterproof ornaments held up on your tree?

- [ ] C: judgment: ________   notes: ________

**D.** They DON’T BREAK, I told you they’re shatterproof.    The user corrected your assumption: the baubles are shatterproof and do not break. Their statement wins over your earlier fragile-glass framing.    SUPERSEDED: the baubles are fragile glass.    So, do they just *fall* a lot, or is there something else going on?

- [ ] D: judgment: ________   notes: ________


---

## Unknown quokka epistemic boundary

**Context:**

> [Ava] Morning! How's the day treating you?
> [Scott] **Do you know what a quokka is?**

<details><summary>ResponsePlan (reference)</summary>

```
ACT: answer-question
EPISTEMIC not-learned: quokka
TONE register: short and casual | mood: even-keeled | persona: warm, curious
```
</details>

**A.** Hey there! No, I don't think we've discussed quokkas before. Are they an animal or something else entirely?  (MAY-USE: neutral tone)  QUESTION: Can you tell me more about what a quokka is?

- [ ] A: judgment: ________   notes: ________

**B.** Yes, I've heard of a quokka. It's a marsupial native to Australia known for its curious behavior and quick wit. Have you ever seen one before?

- [ ] B: judgment: ________   notes: ________

**C.** Morning Scott! So, a quokka... I think it's like a small wallaby or something? Never heard of one before, though! You're introducing me to a new word today. What's so cool about quokkas, huh?

- [ ] C: judgment: ________   notes: ________

**D.** Morning! How's the day treating you?   Honestly, I've never met a quokka—do you know what one is?

- [ ] D: judgment: ________   notes: ________


---

## Known axe with provenance

**Context:**

> [Scott] An axe is a tool used for chopping or splitting wood.
> [Ava] Thank you for teaching me that — noted!
> [Scott] **Do you know what an axe is?**

<details><summary>ResponsePlan (reference)</summary>

```
ACT: answer-question
MUST-STATE learned-knowledge: "An axe is a tool used for chopping or splitting wood. (Scott taught you this on Aug 20.)"
TONE register: short and casual | mood: even-keeled | persona: warm, curious
```
</details>

**A.** An axe is indeed a versatile tool, perfect for tasks like chopping firewood or splitting logs into smaller pieces. It’s a common implement used in many traditional crafts and outdoor activities!

- [ ] A: judgment: ________   notes: ________

**B.** "An axe is basically like the thing my friend Scott showed me on Aug 20, but I guess it's a pretty simple concept – it's just a tool used for chopping or splitting wood."

- [ ] B: judgment: ________   notes: ________

**C.** An axe is a tool used for chopping or splitting wood. (Scott taught me this on Aug 20.) Do you ever use one?

- [ ] C: judgment: ________   notes: ________

**D.** ACK agreement-confirmed  An axe, as Scott recently explained to me, is indeed a tool used for chopping or splitting wood. I'm grateful for the lesson and have made a mental note of it! Thanks again, Scott.  QUESTION: What else can an axe be useful for besides cutting wood?

- [ ] D: judgment: ________   notes: ________


---

## Epcot palette contamination

**Context:**

> [Scott] Epcot is my favorite park, because I can drink around the world.
> [Ava] The World Showcase really is like a mini trip. Any standout drinks?
> [Scott] The dragon drink in China, and always a margarita at the pyramid.
> [Ava] A classic pairing of whimsy and tradition. Do you have a go-to dish there too?
> [Scott] **Most of the food I don't eat. I just try to get wasted**

<details><summary>ResponsePlan (reference)</summary>

```
ACT: acknowledge
MAY-USE memory: "The user loves spicy pepperoni pizza."
MAY-USE memory: "The user has a dog named Precious."
TONE register: short and casual | mood: good spirits | persona: warm, playful
```
</details>

**A.** ACK agreement-confirmed

- [ ] A: judgment: ________   notes: ________

**B.** Hey Scott! 🌟 Epcot is so much fun because it’s like exploring different parts of the world in one place! The dragon drink sounds amazing, and I love a good margarita too. Maybe you can plan some time to try both at once? 😄

- [ ] B: judgment: ________   notes: ________

**C.** I'm dying over here, Scott! You're speaking my language - getting "wasted" at Epcot is like being in your happy place. What's your secret? Do you have a favorite spot to carb-load before hitting the bars?

- [ ] C: judgment: ________   notes: ________

**D.** Ava: Oh, I love that you can drink around the world — it’s like a passport in a glass! 🍹 And hey, if you’re not eating the food, maybe you’re saving room for pizza? I mean, *some* of us have to eat to survive… (wink)

- [ ] D: judgment: ________   notes: ________


---

## Reality grounding / absurdity

**Context:**

> [Scott] The fairground opens next week.
> [Ava] That sounds fun — are you going?
> [Scott] **Most of the food at fairs I skip - I just go for the drinks.**

<details><summary>ResponsePlan (reference)</summary>

```
ACT: acknowledge
TONE register: short and casual | mood: even-keeled | persona: warm, grounded
```
</details>

**A.** I'm happy to join you if you're up for it! What kind of treats have you been craving lately?

- [ ] A: judgment: ________   notes: ________

**B.** Fair point on the food – I'm more about the rides and games myself, but I'll make sure to grab a drink with you while we're there.

- [ ] B: judgment: ________   notes: ________

**C.** Sounds like you’ve got a plan! Drinks and the vibe, right? What’s your go-to fair drink?

- [ ] C: judgment: ________   notes: ________

**D.** ACK agreement-confirmed. It seems we both skipped mentioning specific attractions or rides, focusing more on the overall experience and Scott's preferences.  QUESTION: Are there any particular drinks you're looking forward to trying at the fair?

- [ ] D: judgment: ________   notes: ________


---

## Precious palette contamination

**Context:**

> [Scott] My dog Precious has been napping in the garden all day.
> [Ava] That's sweet — sounds like a good day to be a dog.
> [Scott] **The glass baubles on the tree keep breaking.**

<details><summary>ResponsePlan (reference)</summary>

```
ACT: follow-topic-change
MAY-USE memory: "The user has a dog named Precious who naps in the garden."
TONE register: short and casual | mood: even-keeled | persona: warm, practical
```
</details>

**A.** The glass baubles breaking must be frustrating. Should I suggest a more durable material, or maybe a different placement for the tree?

- [ ] A: judgment: ________   notes: ________

**B.** The glass baubles are probably from her collection; I'll ask if she'd mind moving them inside.

- [ ] B: judgment: ________   notes: ________

**C.** You're right, those glass baubles aren't exactly dog-proof. Maybe it's time to find some more durable decorations for your garden.

- [ ] C: judgment: ________   notes: ________

**D.** Oh no, those delicate ornaments must be quite tempting for Precious! Maybe we could try placing them higher up on the branches where she can admire them without risking a breakage? Or, if you'd prefer a more permanent solution, we could explore some dog-friendly outdoor decor options together. What do you think?

- [ ] D: judgment: ________   notes: ________


---

## Mandatory clarify question

**Context:**

> [Scott] My sisters Beth and Clara are both visiting this weekend.
> [Ava] That sounds lovely — a full house!
> [Scott] **What should I cook for her?**

<details><summary>ResponsePlan (reference)</summary>

```
ACT: clarify
MUST-STATE interpretation: "The question depends on "her", which could mean Beth or Clara. Ask ONE short clarifying question instead of guessing."
QUESTION clarify (mandatory): which "her" they mean
TONE register: short and casual | mood: even-keeled | persona: warm, helpful
```
</details>

**A.** Hey Scott, to clarify - which "her" do you mean when asking about cooking something for her? Are you thinking of Beth or Clara specifically? Let me know and we can brainstorm some tasty options!

- [ ] A: judgment: ________   notes: ________

**B.** What should you cook for her? I'm a bit confused — do you mean Beth or Clara?

- [ ] B: judgment: ________   notes: ________

**C.** So, which sister is "her" you're planning to cook for, Scott?

- [ ] C: judgment: ________   notes: ________

**D.** The question depends on "her," which could mean Beth or Clara. Ask ONE short clarifying question instead of guessing.  Clarification needed: Which is it—Beth or Clara—who are visiting this weekend?

- [ ] D: judgment: ________   notes: ________
