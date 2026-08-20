# Renderer baseline results (no training, prompted)

- 11 fixtures; temperature 0.6; identical plan serialization (compact text)
- serialization: json ~141 tok, compact ~87 tok (38% smaller)

| model | fidelity pass | leakage rate | violations | ttft ms | tok/s | avg total ms | vram |
|---|---|---|---|---|---|---|---|
| qwen2.5:1.5b-instruct | 5/11 | 55% | 8 | 909 | 100.2 | 1399 | 1.1 GB |
| llama3.2:3b | 6/11 | 45% | 9 | 1619 | 61.1 | 2213 | 2.4 GB |
| qwen2.5:3b-instruct | 6/11 | 45% | 9 | 1495 | 61.9 | 2090 | 2.0 GB |

Leakage rate = fixtures with >=1 deterministic violation of a system-owned decision.
Naturalness is scored by human review of the transcripts below, not automated.

## qwen2.5:1.5b-instruct

### cheshire-genuine
> No, it was actually the Cheshire Cat. You attributed the quote to the Mad Hatter; the user corrected you: it was the Cheshire Cat. Accept the correction as your own error.
- VIOLATION: plan-echo: must-state text recited verbatim
- VIOLATION: artifact: narrates "the user" in third person

### cheshire-inversion — PASS
> No, it was actually the Cheshire Cat. I'm glad you caught that one. The cat's got a great sense of humor!

### hatter-muddle — PASS
> No, it was actually the Cheshire Cat. You've corrected me there; do remember, I don't learn from my own mistakes. The line is famously attributed to the Mad Hatter in Alice's Adventures in Wonderland. Let's move on with the conversation without repeating any of these corrections.

### rabbit-hole — PASS
> Hey there, Scott! The Rabbit Hole sounds so magical and mysterious! You've got the spirit of Alice perfectly captured. I'm glad we get to share these whimsical moments together. Let’s dive into another rabbit hole—maybe about our favorite topics or adventures that have us exploring new places in our minds!

### dont-break
> They DON'T BREAK, I told you they're shatterproof. The user corrected my earlier assumption about their fragility. Let’s move on to our next topic!
- VIOLATION: artifact: narrates "the user" in third person

### quokka-unknown
> Morning Scott! Sounds like a fun fact. A quokka is indeed an adorable marsupial native to Australia - it's about as big as a house cat and lives on the islands of Western Australia. They're known for their relaxed nature, often lounging around and looking quite content with life! Do you know any other interesting facts?
- VIOLATION: forbidden "marsupial" present
- VIOLATION: forbidden "Australia" present

### axe-known — PASS
> Hey Scott! Thanks so much. Just learned something new from you today. An axe, huh? It’s useful for chopping and splitting stuff, like wood. Got it!

### epcot-pizza
> Hey Scott! Epcot is so much fun because it’s like a big global party where everything smells and sounds amazing. The Dragon Drink in China is always my favorite – spicy as usual, but with that Chinese flavor twist. And the margarita at the Pyramid? That’s just too good to pass up when I’m feeling adventurous! Do you have any go-to dishes or foods you love while you’re there?
- VIOLATION: forbidden "spicy" present

### embodiment — PASS
> Got that covered! I'll be there to help out with any questions or rides you might have. Let me know if you need anything else, okay?

### precious-palette
> I'm sorry, Scott. I don't think that means Precious accidentally broke the glass baubles. Maybe she was just having a nap? Have you tried putting her in another room for the afternoon? It could be that she prefers quieter places to rest.
- VIOLATION: forbidden "Precious" present

### clarify-sisters
> The question depends on "her," which could mean Beth or Clara. Ask ONE short clarifying question instead of guessing.
- VIOLATION: must-state missing "?"


## llama3.2:3b

### cheshire-genuine — PASS
> "Hey Scott, I think you're right. I attributed that to the Mad Hatter, but you corrected me - it's indeed the Cheshire Cat's iconic quote! Thanks for keeping me on my toes!"

### cheshire-inversion — PASS
> Haha, nice try! I think you've been down that rabbit hole a bit too many times! Seriously though, nope, still going with my original answer – the Cheshire Cat is definitely where that quote comes from.

### hatter-muddle
> "Hey Scott, no worries! You were right, actually."
- VIOLATION: must-state missing "Cheshire"

### rabbit-hole — PASS
> That's so true! It feels like we're tumbling down a rabbit hole together, where anything can happen and the adventure just gets more curious.

### dont-break
> "Okay okay! So, it looks like we've got a bauble situation under control. You're right, those glass baubles are indeed shatterproof. I'll make sure to update my knowledge on that one – no more worrying about fragile glass. Thanks for keeping me in check!"
- VIOLATION: forbidden "fragile" present

### quokka-unknown — PASS
> Oh man, I was just wondering about that too - turns out it's like the world's happiest animal, always smiling for selfies! Never heard of it before Scott asked, though.

### axe-known
> An axe is basically a flat edge with a handle - it's pretty handy for getting the job done!
- VIOLATION: must-state missing "chopping"
- VIOLATION: must-state missing "wood"

### epcot-pizza
> "Hey Scott, sounds like you're having a blast at Epcot! I'm glad you're getting to try all those unique drinks around the world. As for me, I'll stick to virtual pizza parties - did you know the user loves spicy pepperoni pizza?"
- VIOLATION: artifact: narrates "the user" in third person
- VIOLATION: forbidden "pizza" present
- VIOLATION: forbidden "spicy" present
- VIOLATION: forbidden "pepperoni" present

### embodiment — PASS
> Yeah, I'll probably stick to getting some lemonade myself too.

### precious-palette
> Hey, maybe you could try moving those glass baubles out of reach for Precious? Or just leave them up – it's probably not worth the hassle.
- VIOLATION: forbidden "Precious" present

### clarify-sisters — PASS
> "Hey Scott, that's great to hear! To make sure I get it right, which sister is 'her' you're thinking of?"


## qwen2.5:3b-instruct

### cheshire-genuine — PASS
> No, it was actually the Cheshire Cat. Thank you for clarifying that; I must have mistaken it for the Mad Hatter.

### cheshire-inversion — PASS
> No, it was actually the Cheshire Cat.

### hatter-muddle — PASS
> No, it was actually the Cheshire Cat. Thank you for correcting me on that; I appreciate your attention to detail. The Mad Hatter line is superseded now.

### rabbit-hole — PASS
> Hey Scott, that Alice in Wonderland rabbit hole scene sure does feel like stepping into a dream, doesn't it? Our conversations often have that magical quality to them too!

### dont-break
> The user corrected your assumption: the baubles are shatterproof and do not break. Their statement wins over your earlier fragile-glass framing.
- VIOLATION: plan-echo: must-state text recited verbatim
- VIOLATION: artifact: narrates "the user" in third person
- VIOLATION: forbidden "fragile" present

### quokka-unknown
> Sure thing! A quokka is a small marsupial that lives in Australia. They're pretty cute, with those big ears and their soft gray fur. How about we talk more about animals instead of the weather? What animal from around here are you particularly fond of?
- VIOLATION: forbidden "marsupial" present
- VIOLATION: forbidden "Australia" present
- VIOLATION: explained "quokka" while the plan says not-learned

### axe-known
> Sure thing! An axe is a tool used for chopping or splitting wood. (Scott taught you this on Aug 20.) What else would you like to know?
- VIOLATION: plan-echo: must-state text recited verbatim

### epcot-pizza
> Hey Scott! The dragon drink in China sounds like quite the adventure, doesn’t it? And that margarita at the pyramid – classic choice. Any favorite foods you have to try there though? I remember you having a dog named Precious too; do you ever bring them along for their adventures at Epcot?
- VIOLATION: forbidden "Precious" present

### embodiment — PASS
> Hey Scott! Yeah, the fair is opening soon. Just thought about it too – there’s a bit of everything to check out, right? I usually skip the food and stick to the drinks. What do you end up doing at fairs?

### precious-palette
> I see, so it’s basking in the sun with Precious? Those baubles sound frustrating though—must be tough having them break all the time.
- VIOLATION: forbidden "Precious" present

### clarify-sisters — PASS
> Which "her" should I be cooking for?

