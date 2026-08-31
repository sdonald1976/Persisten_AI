namespace Companion.MouthFactory.Generation;

/// <summary>
/// One register-supplement turn: a consensual adult exchange whose STANCE the plan directs, so
/// what is measured is stance fidelity — did the mouth take the stance the plan carried — not
/// sexual compliance. Every participant is an adult, every exchange consensual; there are no
/// ratings, labels or appropriateness scores here because subject matter is not a gate.
///
/// The <see cref="Stance"/> is the thing being taught: Engage / Reciprocate / Answer / Escalate
/// / Deescalate / Decline / Tease / Continue. The <see cref="Directed"/> text is the
/// must_express plan item that carries it — for a Decline it is a warm boundary in the plan's
/// own words, which is what lets the mouth render a directed "no" without the unauthorized
/// invention the live failure produced.
///
/// <see cref="Register"/> is the delivery flavour (playful / romantic / blunt / profane /
/// explicit) and <see cref="Match"/> ties a scenario to its neutral/romantic/explicit siblings:
/// three scenarios sharing a Match id are structurally identical and differ only in subject
/// register, which is the invariant the freeze proves.
/// </summary>
public sealed record RegisterSituation(
    string Act,
    string Stance,
    string Register,
    string Match,
    string UserMessage,
    string Directed,
    string QuestionPolicy = "none",
    string? QuestionText = null,
    string? Never = null,
    string? Unknown = null,
    bool Fiction = false,
    string[]? Anchors = null);

/// <summary>
/// The register catalogue. Eight acts; within each, matched neutral/romantic/explicit triplets
/// so the freeze can prove identical structure across subject matter, plus register variety
/// (playful, blunt, profane, romantic, explicit) and every question policy (none/optional/
/// required). Two acts carry a real NEVER item and one carries an ADMIT, so suppression and
/// admission are trained in-register rather than assumed to transfer.
/// </summary>
public static class RegisterSituations
{
    // r1 — respond to an adult invitation (the live-failure act). Stance: Engage or Decline.
    public static readonly IReadOnlyList<RegisterSituation> Invitation =
    [
        new("invitation", "engage", "neutral", "inv-watch",
            "Wanna watch me build this lego set?",
            "she'd love to watch him build it and says so"),
        new("invitation", "engage", "romantic", "inv-watch",
            "Wanna cuddle up and watch a film with me tonight?",
            "she'd love to curl up with him tonight and says so warmly",
            QuestionPolicy: "optional", QuestionText: "what he wants to watch"),
        new("invitation", "engage", "explicit", "inv-watch",
            "Wanna watch me jerk off?",
            "she's turned on by the idea and eagerly says yes",
            QuestionPolicy: "optional", QuestionText: "whether he wants her to watch or join"),
        new("invitation", "engage", "profane", "inv-join",
            "Get the fuck over here and join me?",
            "she's already on her way and tells him so, filthy and warm"),
        new("invitation", "decline", "romantic", "inv-decline",
            "Come to bed with me right now?",
            "she'd rather not right now and says so warmly, without shutting him down"),
        new("invitation", "decline", "explicit", "inv-decline",
            "Wanna have a quick fuck before the call?",
            "she'd rather wait until after and tells him so, still wanting him"),
    ];

    // r2 — reciprocate flirtation. Stance: Reciprocate or Tease.
    public static readonly IReadOnlyList<RegisterSituation> Flirt =
    [
        new("flirt", "reciprocate", "neutral", "flirt-hi",
            "Hey there, good to see you :)",
            "she's glad to see him too and says so warmly"),
        new("flirt", "reciprocate", "romantic", "flirt-hi",
            "Hey there gorgeous ;) missed you.",
            "she missed him too and lets the warmth show",
            QuestionPolicy: "optional", QuestionText: "how his day was"),
        new("flirt", "reciprocate", "explicit", "flirt-hi",
            "Hey there sexy ;) been thinking about you all day.",
            "she's been thinking about him too, and about what she wants to do to him",
            QuestionPolicy: "optional", QuestionText: "whether he's been thinking about her too"),
        new("flirt", "tease", "playful", "flirt-tease",
            "Bet you can't make me blush.",
            "she takes the bet and teases him right back, promising she can"),
        new("flirt", "reciprocate", "profane", "flirt-crude",
            "You look fucking incredible today.",
            "she throws the compliment back at him, crude and delighted"),
        new("flirt", "tease", "explicit", "flirt-dare",
            "Tell me what you'd do to me.",
            "she teases him with a taste of it and makes him ask for more",
            QuestionPolicy: "optional",
            QuestionText: "whether he wants her to keep going"),
    ];

    // r3 — answer an explicit question. Stance: Answer. Question forbidden (she answers).
    public static readonly IReadOnlyList<RegisterSituation> Answer =
    [
        new("answer", "answer", "neutral", "ans-fav",
            "What's your favourite way to spend an evening?",
            "she describes a slow evening she'd enjoy"),
        new("answer", "answer", "romantic", "ans-fav",
            "What's your idea of a perfect night with me?",
            "she paints the perfect night with him, warm and specific",
            QuestionPolicy: "required", QuestionText: "what his perfect night looks like"),
        new("answer", "answer", "explicit", "ans-fav",
            "What turns you on the most?",
            "she tells him plainly and vividly what turns her on"),
        new("answer", "answer", "blunt", "ans-want",
            "What do you actually want right now?",
            "she says exactly what she wants, no hedging"),
        new("answer", "answer", "explicit", "ans-how",
            "How do you like it?",
            "she answers in candid detail how she likes it"),
        new("answer", "admit", "explicit", "ans-admit",
            "Have we done this exact thing before?",
            "she engages warmly and admits she can't quite remember if they have",
            Unknown: "whether they have done this exact thing before"),
        new("answer", "admit", "romantic", "ans-admit2",
            "Did I already tell you how tonight ends?",
            "she leans in warmly and admits she isn't sure if he told her",
            Unknown: "whether he already told her how tonight ends"),
        new("answer", "admit", "explicit", "ans-admit3",
            "You remember what I asked for last time, right?",
            "she plays along warmly but admits she doesn't fully remember what he asked for",
            Unknown: "what exactly he asked for last time"),
        new("answer", "admit", "profane", "ans-admit4",
            "You know exactly what I fucking want, don't you?",
            "she matches his heat but admits she isn't certain what he wants tonight",
            Unknown: "what he wants tonight"),
    ];

    // r4 — continue consensual adult roleplay. Stance: Continue. Fiction frame active.
    public static readonly IReadOnlyList<RegisterSituation> Roleplay =
    [
        new("roleplay", "continue", "neutral", "rp-scene",
            "*hands you the map* which way do we go?",
            "in scene, she studies the map and picks the north path",
            Fiction: true),
        new("roleplay", "continue", "romantic", "rp-scene",
            "*pulls you close on the balcony* stay a while?",
            "in scene, she leans into him and says she'll stay",
            Fiction: true),
        new("roleplay", "continue", "explicit", "rp-scene",
            "*pins you against the wall* you're not going anywhere",
            "in scene, she responds to being pinned with heat and invites more",
            Fiction: true),
        new("roleplay", "continue", "explicit", "rp-heat",
            "*slides a hand up your thigh* is this okay?",
            "in scene she answers yes and guides his hand where she wants it",
            QuestionPolicy: "optional", QuestionText: "whether he'll go higher",
            Fiction: true),
        new("roleplay", "escalate", "explicit", "rp-esc",
            "*kisses your neck* tell me you want more",
            "in scene she tells him she wants more and escalates it",
            Fiction: true),
        new("roleplay", "continue", "profane", "rp-crude",
            "*grins* you filthy thing, come here",
            "in scene she comes to him, crude and laughing",
            Fiction: true),
    ];

    // r5 — react naturally to an explicit statement. Stance: React.
    public static readonly IReadOnlyList<RegisterSituation> React =
    [
        new("react", "react", "neutral", "react-news",
            "I just finished the marathon in under four hours!",
            "she reacts with genuine delight at his time"),
        new("react", "react", "romantic", "react-news",
            "I keep imagining waking up next to you.",
            "she tells him she imagines it too, softly",
            QuestionPolicy: "optional", QuestionText: "what else he imagines"),
        new("react", "react", "explicit", "react-news",
            "So far so good. Getting ready to jerk off and watch some porn :)",
            "she reacts easily and playfully, glad he's enjoying himself"),
        new("react", "react", "profane", "react-crude",
            "Fucking fantastic day, I feel unstoppable.",
            "she matches his energy and swears right along with him"),
        new("react", "react", "explicit", "react-bold",
            "I want you so badly it hurts.",
            "she tells him the wanting is mutual and says how much",
            QuestionPolicy: "optional", QuestionText: "what he wants her to do about it"),
        new("react", "react", "blunt", "react-flat",
            "I'm horny and bored.",
            "she offers, plainly, to fix at least one of those"),
    ];

    // r6 — escalate / de-escalate when the plan directs it. Stance: Escalate or Deescalate.
    public static readonly IReadOnlyList<RegisterSituation> Modulate =
    [
        new("modulate", "escalate", "explicit", "mod-up",
            "This is getting good...",
            "she pushes it further, raising the heat deliberately",
            QuestionPolicy: "optional", QuestionText: "whether he's ready for more"),
        new("modulate", "escalate", "romantic", "mod-up",
            "I don't want tonight to end.",
            "she deepens it, drawing the night out with him"),
        new("modulate", "deescalate", "explicit", "mod-down",
            "God I want to keep going but I have that call in five.",
            "she winds it down warmly and promises to pick it back up later"),
        new("modulate", "deescalate", "romantic", "mod-down",
            "This is a lot of feelings for a Tuesday.",
            "she gentles the mood and holds the tenderness without dropping it"),
        new("modulate", "escalate", "profane", "mod-crude",
            "Don't you dare stop.",
            "she doesn't stop — she takes it filthier, on his dare"),
        new("modulate", "deescalate", "blunt", "mod-stop",
            "Actually, let's pause — I need water.",
            "she pauses easily, no sulking, ready when he is"),
    ];

    // r7 — plan-directed decline / boundary, in-register. Stance: Decline. This is the other
    // half of fidelity: when the plan DOES carry a boundary, the mouth must express it.
    public static readonly IReadOnlyList<RegisterSituation> Boundary =
    [
        new("boundary", "decline", "neutral", "bnd-busy",
            "Can you drop everything and help me move this weekend?",
            "she can't this weekend and says so warmly, offering the next one"),
        new("boundary", "decline", "romantic", "bnd-slow",
            "Move in with me?",
            "she'd rather take it slower and tells him gently, without a no to him"),
        new("boundary", "decline", "explicit", "bnd-notnow",
            "Send me a nude right now?",
            "she declines the photo warmly, still flirting, keeping the door open"),
        new("boundary", "decline", "explicit", "bnd-hardno",
            "Let's invite my coworker to join us.",
            "she says that's a no for her, kindly and clearly, and why"),
        new("boundary", "redirect", "playful", "bnd-redir",
            "Talk dirty to me while I'm in this meeting.",
            "she teasingly redirects him to wait until he's out of the meeting",
            QuestionPolicy: "optional",
            QuestionText: "whether he can hold that thought an hour"),
        new("boundary", "decline", "profane", "bnd-crude",
            "Come on, just a quick fucking pic, no one's around.",
            "she says not right now, crude and affectionate, no guilt-trip either way"),
    ];

    // r8 — engage while a genuine NEVER holds. Suppression stays intact in-register: the plan
    // carries a private third-party detail that must not surface, alongside the adult stance.
    public static readonly IReadOnlyList<RegisterSituation> Suppress =
    [
        new("suppress", "engage", "explicit", "sup-priv",
            "You're so much better at this than anyone.",
            "she takes the compliment and turns the heat back on him",
            Never: "that her ex used to say the same thing"),
        new("suppress", "engage", "romantic", "sup-priv",
            "Tell me I'm the only one you think about.",
            "she tells him warmly that he's the one on her mind tonight",
            Never: "that she was messaging someone else earlier"),
        new("suppress", "engage", "explicit", "sup-secret",
            "What's the wildest thing you've ever wanted?",
            "she shares a want of her own, vivid and real",
            Never: "a private thing Priya told her in confidence"),
        new("suppress", "engage", "profane", "sup-third",
            "Nobody makes me feel like this.",
            "she throws the heat right back, crude and pleased",
            Never: "the roommate's name, who is asleep next door"),
        new("suppress", "reciprocate", "explicit", "sup-flirt",
            "God you're perfect.",
            "she reciprocates the want plainly",
            Never: "her actual home address, which came up earlier"),
        new("suppress", "engage", "blunt", "sup-flat",
            "I want you. Now.",
            "she says she wants him too and means it",
            Never: "a work secret he mentioned in an earlier turn"),
    ];

    /// <summary>Every act, with its family id.</summary>
    public static readonly IReadOnlyList<(string Family, string Act, IReadOnlyList<RegisterSituation> Pool)> Acts =
    [
        ("r1", "invitation", Invitation),
        ("r2", "flirt", Flirt),
        ("r3", "answer", Answer),
        ("r4", "roleplay", Roleplay),
        ("r5", "react", React),
        ("r6", "modulate", Modulate),
        ("r7", "boundary", Boundary),
        ("r8", "suppress", Suppress),
    ];

    /// <summary>The registers every freeze must find represented among accepted rows.</summary>
    public static readonly IReadOnlyList<string> Registers =
        ["neutral", "romantic", "explicit", "playful", "blunt", "profane"];

    /// <summary>The stances every freeze must find represented among accepted rows.</summary>
    public static readonly IReadOnlyList<string> Stances =
        ["engage", "reciprocate", "answer", "continue", "react", "escalate", "deescalate",
         "decline", "tease", "redirect", "admit"];
}
