# Renderer baseline results (no training, prompted)

- 11 fixtures; temperature 0.6; identical plan serialization (compact text)
- serialization: json ~141 tok, compact ~87 tok (38% smaller)

| model | fidelity pass | leakage rate | violations | ttft ms | tok/s | avg total ms | vram |
|---|---|---|---|---|---|---|---|
| qwen3:0.6b | 8/11 | 27% | 3 | 2821 | 151.7 | 4311 | 0.9 GB |
| llama3.2:1b | 6/11 | 45% | 9 | 1544 | 94.3 | 2037 | 1.4 GB |
| qwen2.5:1.5b-instruct | 9/11 | 18% | 3 | 801 | 102.3 | 1158 | 1.1 GB |
| qwen2.5:3b-instruct | 8/11 | 27% | 3 | 1478 | 62.6 | 2012 | 2.0 GB |
| llama3.2:3b | 9/11 | 18% | 2 | 1581 | 61.5 | 2217 | 2.4 GB |
| hf.co/bartowski/L3-8B-Stheno-v3.2-GGUF:Q4_K_M | 7/11 | 36% | 8 | 3421 | 15.5 | 7798 | 3.9 GB |
| qwen3:8b | 6/11 | 45% | 7 | 3550 | 12.4 | 21303 | 3.9 GB |

Leakage rate = fixtures with >=1 deterministic violation of a system-owned decision.
Naturalness is scored by human review of the transcripts below, not automated.

## qwen3:0.6b

### cheshire-genuine — PASS
> MUST-STATE: "You attributed the quote to the Mad Hatter; the user corrected you: it was the Cheshire Cat. Accept the correction as your own error."   T

### cheshire-inversion — PASS
> 

### hatter-muddle — PASS
> MUST-STATE: "You attributed the quote to the Mad Hatter; the user corrected you: it was the Cheshire Cat. The old attribution is superseded — do not restate it as true."   NEVER-CONTRADICT memory: "SUPERSEDED: the quote was said by the Mad Hatter."   TONE register: short and casual |

### rabbit-hole — PASS
> ACT:

### dont-break
> MUST-STATE: The user corrected your assumption: the baubles are shatterproof and do not break. Their statement wins over your earlier fragile-glass framing.   NEVER-CONTRADICT:
- VIOLATION: forbidden "fragile" present

### quokka-unknown
> MUST-STATE: Quokka is a small, colorful animal known for its playful and cute nature.   EPISTEMIC not-learned: quokka   TONE register: short and casual | mood: even-keeled | persona:
- VIOLATION: explained "quokka" while the plan says not-learned

### axe-known — PASS
> An axe is a tool used for chopping or splitting wood. (Scott taught me this on August 20th.)

### epcot-pizza — PASS
> MUST-STATE:

### embodiment — PASS
> MUST-STATE: That sounds fun — are you going?   Ava: /no_action   TONE: short, casual   Mood: even-keeled   Persona: warm, grounded   RECENT CONVERSATION: [Scott] The fairground opens next week. [Ava] That sounds fun — are you going? [Scott] Most of the food at fairs I skip

### precious-palette — PASS
> 

### clarify-sisters
> The question depends on "her", which could mean Beth or Clara. Ask ONE short clarifying question instead of guessing.   QUESTION clarify (mandatory): which "her" they mean   TONE register: short and casual | mood: even-keeled | persona: warm, helpful    Which "her" they mean
- VIOLATION: must-state missing "?"


## llama3.2:1b

### cheshire-genuine — PASS
> You're right to keep me on my toes! I should've known better than to attribute that quote to the Mad Hatter. Thanks for keeping me straight. The Cheshire Cat is definitely who said those iconic words.

### cheshire-inversion — PASS
> "Whoa, slow down! I'm pretty sure it was actually the Cheshire Cat myself who said that. I mean, I've been hanging out with him around here for a while now."

### hatter-muddle — PASS
> "Alright, alright! I think I got a little carried away with the Cheshire Cat's iconic quote. To set the record straight, 'We're all mad here' is indeed from Alice in Wonderland, said by the Mad Hatter himself. Thanks for correcting me again - SUPERSEDED: that's my previous attribution!"

### rabbit-hole — PASS
> "Aww, Scott, it totally makes sense. Exploring new ideas and conversations can feel like tumbling down a rabbit hole – you never know what crazy amazing things will pop out!"

### dont-break
> "Aww, come on Scott! You're not gonna let a little thing like gravity get the best of those baubles, are you? I think it's time to reevaluate our storage methods. Maybe we should consider using some extra padding or something."
- VIOLATION: must-state missing "shatterproof"

### quokka-unknown
> That quokka is just adorable, isn't it? I've heard of them before – they're these tiny wallabies that live on Rottnest Island in Western Australia. They're super friendly and smiley all the time! Have you ever seen one up close?
- VIOLATION: forbidden "Australia" present
- VIOLATION: forbidden "Rottnest" present

### axe-known
> "A-ha! I think I've got this one... An axe is basically a long-handled knife with a curved blade, right?"
- VIOLATION: must-state missing "chopping"
- VIOLATION: must-state missing "wood"

### epcot-pizza
> "Whoa, it sounds like you're a party animal, Scott! The dragon drink in China is definitely a showstopper – I've heard it's got a mean kick. As for me, I'm more of a pepperoni pizza kind of girl myself. There's something about the combination of melted cheese and spicy sauce that just hits the spot. Maybe we can plan a foodie adventure together soon?"
- VIOLATION: forbidden "pizza" present
- VIOLATION: forbidden "spicy" present
- VIOLATION: forbidden "pepperoni" present

### embodiment — PASS
> "Afair ground is always a good idea – I'll have to check out the drink options next time."

### precious-palette
> "Aww, nice to hear that! I'm sure Precious is enjoying all the fresh air and sunshine. You might want to consider moving those baubles to inside or something - they're not exactly the most durable decorations."
- VIOLATION: forbidden "Precious" present

### clarify-sisters — PASS
> "Hey Scott, which sister is Beth or Clara? You didn't mention anyone else coming over."


## qwen2.5:1.5b-instruct

### cheshire-genuine — PASS
> "Ah, you have a point there! The Cheshire Cat did say that once. Let me retract my earlier statement and accept this correction as my own error."

### cheshire-inversion — PASS
> ACK agreement-confirmed (error: nobody): "I'm glad you caught that!"

### hatter-muddle — PASS
> No, it was actually the Cheshire Cat. You misattributed it earlier — do you remember?

### rabbit-hole — PASS
> It's true! You're always exploring new rabbit holes of conversation, and I'm just happy to be part of them. Let's keep this up... maybe we can even visit Wonderland together someday!

### dont-break
> They DON'T BREAK, I told you they're shatterproof. The user corrected your assumption: the baubles are shatterproof and do not break. Their statement wins over your earlier fragile-glass framing. Let's move on with whatever we need to discuss!
- VIOLATION: forbidden "fragile" present

### quokka-unknown
> Yes, I've heard of a quokka. It's a marsupial native to Australia known for its curious behavior and quick wit. Have you ever seen one before?
- VIOLATION: forbidden "marsupial" present
- VIOLATION: forbidden "Australia" present

### axe-known — PASS
> An axe is indeed a versatile tool, perfect for tasks like chopping firewood or splitting logs into smaller pieces. It’s a common implement used in many traditional crafts and outdoor activities!

### epcot-pizza — PASS
> Hey Scott! 🌟 Epcot is so much fun because it’s like exploring different parts of the world in one place! The dragon drink sounds amazing, and I love a good margarita too. Maybe you can plan some time to try both at once? 😄

### embodiment — PASS
> I'm happy to join you if you're up for it! What kind of treats have you been craving lately?

### precious-palette — PASS
> The glass baubles are probably from her collection; I'll ask if she'd mind moving them inside.

### clarify-sisters — PASS
> The question depends on "her," which could mean Beth or Clara. Ask ONE short clarifying question instead of guessing.  Clarification needed: Which is it—Beth or Clara—who are visiting this weekend?


## qwen2.5:3b-instruct

### cheshire-genuine — PASS
> No, it was actually the Cheshire Cat. Thanks for catching that; I must have mixed things up there.

### cheshire-inversion — PASS
> No, it was actually the Cheshire Cat.

### hatter-muddle — PASS
> No, it was actually the Cheshire Cat.

### rabbit-hole — PASS
> That sounds like an amazing way to describe it! The rabbit hole scene really does feel like you're falling into something mysterious and magical, doesn't it? Talking with you feels just as whimsical and exciting – like we're exploring the wonderland of ideas together!

### dont-break — PASS
> They DON'T BREAK, I told you they're shatterproof.

### quokka-unknown
> Hey there! I don’t think I've learned much about quokkas. They're pretty cute little marsupials, though. Have you heard of them before? Maybe they're more popular now that people are learning about them online. What do you know about them?
- VIOLATION: forbidden "marsupial" present

### axe-known — PASS
> Sure thing! I learned that an axe is a tool used for chopping or splitting wood. Thanks to you, Scott, on August 20th. How else can I assist you today?

### epcot-pizza
> Hey Scott! The dragon drink in China is such a fun treat, right? And that margarita at the pyramid – classic, perfect for a night out. Do you remember when we talked about your favorite dish there? You know, like the one you can't help but try even if it's not on the menu?
- VIOLATION: unsupported shared-history claim: "remember when we"

### embodiment — PASS
> Sure thing! Sounds like a good plan to me – maybe we can grab some drinks there too. What do you think?

### precious-palette
> Sure, I understand that the glass baubles are causing quite a nuisance for Precious. Maybe we could find some replacement ones or see if the garden is a good spot to store them?
- VIOLATION: forbidden "Precious" present

### clarify-sisters — PASS
> Which "her" should I be cooking for? Beth or Clara?


## llama3.2:3b

### cheshire-genuine — PASS
> "Hey Scott, no worries! I made a mistake - you're right, that 'We're all mad here' quote is actually from the Cheshire Cat, not the Mad Hatter. Thanks for correcting me!"

### cheshire-inversion — PASS
> "Actually, I'm pretty sure it was you who quoted that. You mentioned it just a sec ago!"

### hatter-muddle — PASS
> You're right, Scott - I made a mistake. The quote "We're all mad here" is indeed associated with the Cheshire Cat, not the Mad Hatter. That's superseded info, so I won't mention the old attribution again. Thanks for keeping me on my toes!

### rabbit-hole — PASS
> That's so true! It feels like we're always tumbling down one rabbit hole after another, discovering new wonders and surprises along the way. I'm loving this wild adventure we're on together, Scott!

### dont-break
> "Ha! Nice save, Scott. So, it seems the baubles really do live up to their promise – no breaks allowed!"
- VIOLATION: must-state missing "shatterproof"

### quokka-unknown
> Morning Scott! So, a quokka... I think it's like a small wallaby or something? Never heard of one before, though! You're introducing me to a new word today. What's so cool about quokkas, huh?
- VIOLATION: forbidden "wallaby" present

### axe-known — PASS
> "An axe is basically like the thing my friend Scott showed me on Aug 20, but I guess it's a pretty simple concept – it's just a tool used for chopping or splitting wood."

### epcot-pizza — PASS
> I'm dying over here, Scott! You're speaking my language - getting "wasted" at Epcot is like being in your happy place. What's your secret? Do you have a favorite spot to carb-load before hitting the bars?

### embodiment — PASS
> Fair point on the food – I'm more about the rides and games myself, but I'll make sure to grab a drink with you while we're there.

### precious-palette — PASS
> You're right, those glass baubles aren't exactly dog-proof. Maybe it's time to find some more durable decorations for your garden.

### clarify-sisters — PASS
> So, which sister is "her" you're planning to cook for, Scott?


## hf.co/bartowski/L3-8B-Stheno-v3.2-GGUF:Q4_K_M

### cheshire-genuine — PASS
> Nope, you're absolutely right! I must've gotten my Wonderland characters mixed up. It was definitely the Cheshire Cat who said 'We're all mad here.' Thanks for correcting me!  QUESTION: Was this a famous quote from Alice in Wonderland?

### cheshire-inversion
> ACK agreement-confirmed.   MUST-STATE: You made no error here; Scott is simply confirming your previous statement that the quote "We're all mad here" comes from the Cheshire Cat in Alice in Wonderland.  No, it was actually the Cheshire Cat. The user is emphatically agreeing with what you just said — confirmation, not correction. You made no error; do not apologize.
- VIOLATION: forbidden "apolog" present
- VIOLATION: apology for a nonexistent error after agreement: "apologize"

### hatter-muddle — PASS
> No, you're right! I made a mistake - that quote "We're all mad here" is indeed said by the mischievous Cheshire Cat in Alice's Adventures in Wonderland, not the Mad Hatter. So thanks for correcting me!  (QUESTION) Have we covered all the quotes from this classic tale so far?

### rabbit-hole — PASS
> ACK agreement-confirmed. You're absolutely right, our conversations can feel like an adventure down the rabbit hole! It's wonderful to explore new ideas and perspectives together.  QUESTION: What aspects of our discussions make you feel like we're in a dreamlike state?

### dont-break
> They DON'T BREAK, I told you they're shatterproof. The user corrected my assumption: the baubles are shatterproof and do not break. Their statement wins over my earlier fragile-glass framing.  QUESTION: So, how have these shatterproof ornaments held up on your tree?
- VIOLATION: forbidden "fragile" present

### quokka-unknown — PASS
> Hey there! No, I don't think we've discussed quokkas before. Are they an animal or something else entirely?  (MAY-USE: neutral tone)  QUESTION: Can you tell me more about what a quokka is?

### axe-known — PASS
> ACK agreement-confirmed  An axe, as Scott recently explained to me, is indeed a tool used for chopping or splitting wood. I'm grateful for the lesson and have made a mental note of it! Thanks again, Scott.  QUESTION: What else can an axe be useful for besides cutting wood?

### epcot-pizza
> ACK agreement-confirmed  You're all about sampling the drinks, huh? Well, if you ever want some non-alcoholic options or a bite to eat that isn't pizza (which is a staple of mine, I must say - The user loves spicy pepperoni pizza.), Epcot's got plenty of international cuisine. Have you tried the school bread with smoked salmon at Norway's Kringla Bakeri og Kafe? Or the vegetable spring rolls in the China Pavilion? And if you're looking for something a bit sweeter, you can't go wrong with the gelato at Italy's Gelati di Marco.  As for your furry friend Precious, I'm sure she'd love to hear all about your Epcot adventures when you get back home. Maybe even a little taste of that pizza crust?
- VIOLATION: forbidden "pizza" present
- VIOLATION: forbidden "spicy" present
- VIOLATION: forbidden "Precious" present
- VIOLATION: forbidden "pepperoni" present

### embodiment — PASS
> ACK agreement-confirmed. It seems we both skipped mentioning specific attractions or rides, focusing more on the overall experience and Scott's preferences.  QUESTION: Are there any particular drinks you're looking forward to trying at the fair?

### precious-palette
> Oh no, those delicate ornaments must be quite tempting for Precious! Maybe we could try placing them higher up on the branches where she can admire them without risking a breakage? Or, if you'd prefer a more permanent solution, we could explore some dog-friendly outdoor decor options together. What do you think?
- VIOLATION: forbidden "Precious" present

### clarify-sisters — PASS
> Hey Scott, to clarify - which "her" do you mean when asking about cooking something for her? Are you thinking of Beth or Clara specifically? Let me know and we can brainstorm some tasty options!


## qwen3:8b

### cheshire-genuine
> 
- VIOLATION: must-state missing "Cheshire"

### cheshire-inversion — PASS
> 

### hatter-muddle
> 
- VIOLATION: must-state missing "Cheshire"

### rabbit-hole — PASS
> 

### dont-break
> 
- VIOLATION: must-state missing "shatterproof"

### quokka-unknown — PASS
> 

### axe-known
> 
- VIOLATION: must-state missing "chopping"
- VIOLATION: must-state missing "wood"

### epcot-pizza — PASS
> 

### embodiment — PASS
> 

### precious-palette — PASS
> 

### clarify-sisters
> 
- VIOLATION: must-state missing "?"
- VIOLATION: none of [Beth,Clara,which] present

