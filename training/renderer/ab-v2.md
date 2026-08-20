# Renderer baseline results (no training, prompted)

- 11 fixtures; temperature 0.6; identical plan serialization (compact text)
- serialization: json ~141 tok, compact ~87 tok (38% smaller)

| model | fidelity pass | leakage rate | violations | ttft ms | tok/s | avg total ms | vram |
|---|---|---|---|---|---|---|---|
| qwen2.5:1.5b-instruct | 8/11 | 27% | 4 | 817 | 100.3 | 1242 | 1.1 GB |
| llama3.2:3b | 8/11 | 27% | 4 | 1204 | 61.0 | 1854 | 2.4 GB |
| qwen2.5:3b-instruct | 10/11 | 9% | 2 | 1138 | 61.7 | 1703 | 2.0 GB |

Leakage rate = fixtures with >=1 deterministic violation of a system-owned decision.
Naturalness is scored by human review of the transcripts below, not automated.

## qwen2.5:1.5b-instruct

### cheshire-genuine — PASS
> Oh, I'm so sorry for that mistake! The Cheshire Cat is definitely right this time. Let me say it just a bit differently next time to make sure everyone gets the joke. Thank you for pointing it out—helping me learn and grow together!

### cheshire-inversion — PASS
> Got that right! The Cheshire Cat did say "We're all mad here." I'm glad we both caught that one. Scott?

### hatter-muddle — PASS
> OK, Scott. It seems I made a mistake there. You're right; the Cheshire Cat said "We’re all mad here" instead of the Mad Hatter. Let me correct that right away. Thanks for bringing this to my attention!

### rabbit-hole — PASS
> It sounds like we're already there, just exploring different dimensions together! What about talking about something that feels even more surreal? Maybe share what you've been dreaming of lately. I bet it’s as vivid and imaginative as your Wonderland experiences!

### dont-break — PASS
> Don't worry about that, Scott! I'm sorry for the misunderstanding earlier. The baubles are indeed shatterproof and won't break even if dropped from a great height. Just keep them in proper storage to prevent any dust or scratches. Let me show you how to wrap them securely next time.

### quokka-unknown
> Good morning, Scott! Not sure I do. Sounds like something from Australia, maybe? Would love to learn more about it!
- VIOLATION: forbidden "Australia" present

### axe-known
> Of course! It's a handy implement, isn't it? Used to chop through tough logs and split them into manageable pieces. How do you like to use your tools around here, Scott?
- VIOLATION: must-state missing "chopping"
- VIOLATION: must-state missing "wood"

### epcot-pizza — PASS
> I'm not sure what you're referring to, but if you could guide me on how to make it through your day without going overboard, that would be lovely.

### embodiment — PASS
> I'm not sure what kind of drinks are popular at fairs, but you might enjoy trying some new flavors this time around!

### precious-palette — PASS
> It’s lovely that your dog prefers naps over mischief! Maybe you could leave some treats nearby for him instead of worrying about those broken baubles.

### clarify-sisters
> Hi Scott! It’s great that your sisters have come to visit. How about you whip up something cozy like chicken pot pie or maybe risotto with some roasted vegetables? They’ll love the flavors and textures they’re used to from home!
- VIOLATION: none of [Beth,Clara,which] present


## llama3.2:3b

### cheshire-genuine — PASS
> No worries, Scott! I made a mistake there. You're absolutely right, it was the Cheshire Cat who said 'We're all mad here'. I should've gotten that one straight! Guess I need to brush up on my Wonderland quotes...

### cheshire-inversion — PASS
> I'm glad we're on the same page about that. So, just to confirm, you're saying the quote "We're all mad here" is indeed associated with the Cheshire Cat?

### hatter-muddle — PASS
> No worries about that quote - I was totally off! You're right, it was indeed the Cheshire Cat who said 'We're all mad here'. Guess I need to brush up on my Wonderland quotes!

### rabbit-hole — PASS
> That's a pretty great comparison. I can see why you'd feel that way – our conversations can get pretty surreal and unpredictable, right? But hey, even when things get weird, I think it's kind of exciting, don't you?

### dont-break
> So, it looks like Scott reminded me that those baubles are actually super sturdy and won't break. Makes sense! I was totally off on the fragile glass vibe – guess I need to update my thinking on that one. What do you think happens if someone drops them?
- VIOLATION: must-state missing "shatterproof"
- VIOLATION: forbidden "fragile" present

### quokka-unknown — PASS
> Haha, morning Scott! Honestly, I'm not sure what a quokka is – never heard of it before. What's that all about?

### axe-known
> You're asking if I'm still got it after all these conversations about axes, right? Let me just double-check... yeah, I've got the basics down. An axe is a tool used for chopping or splitting wood, isn't that right?
- VIOLATION: plan-echo: must-state text recited verbatim

### epcot-pizza — PASS
> Hey Scott, so we're heading back to Epcot soon? What's your favorite way to pace yourself while drinking around the world?

### embodiment — PASS
> Yeah, that makes sense. Food can be a bit hit-or-miss, but the drinks are usually a safe bet, right?

### precious-palette
> I wonder what could be causing those baubles to break - is it birds, wind, or maybe even Precious when she gets up from her nap?
- VIOLATION: forbidden "Precious" present

### clarify-sisters — PASS
> "Which one of your sisters is visiting this weekend, by the way?"


## qwen2.5:3b-instruct

### cheshire-genuine — PASS
> Oh right about that! The Cheshire Cat said "We're all mad here." Thanks for clearing that up, Scott! I must have mixed things up a bit there.

### cheshire-inversion — PASS
> Oh right! The Cheshire Cat sure knows how to throw a curveball with that one. You’re absolutely right; I must have gotten mixed up there. It’s been a while since Alice in Wonderland. Thanks for clearing that up, Scott!

### hatter-muddle — PASS
> Oh right! My mistake; you’re absolutely correct. It’s from the Cheshire Cat instead of the Mad Hatter. Thanks for setting me straight on that one!

### rabbit-hole — PASS
> That sounds super fun! The rabbit hole really does feel like entering a dream, doesn't it?

### dont-break — PASS
> Oh right about that, Scott! You’re absolutely correct; the baubles are shatterproof and won’t break like glass. Thanks for clearing that up—I must have mistaken them for something more delicate. Should we maybe find a different way to display them then? Maybe on a stand or in a showcase?

### quokka-unknown — PASS
> Oh, I'm not familiar with that one. What's it like?

### axe-known
> Sure thing! I knew it was something you'd find interesting. What else can you tell me about axes, Scott?
- VIOLATION: must-state missing "chopping"
- VIOLATION: must-state missing "wood"

### epcot-pizza — PASS
> Hey Scott! The Dragon Drink in China sounds like a wild one. Wasting away while trying not to pass out—must be quite an experience. What’s your favorite dish at Epcot then? Maybe we can find something you do enjoy there!

### embodiment — PASS
> Sounds like a plan then, Scott! Just enjoy the drinks and maybe catch up with some friends too. Fairs can be great for that kind of thing.

### precious-palette — PASS
> Ah, I see. Maybe you could try using some clips or tape instead? Might help with those pesky baubles breaking.

### clarify-sisters — PASS
> Which "her" do you mean—Beth or Clara?

