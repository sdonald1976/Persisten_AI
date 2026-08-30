namespace Companion.MouthFactory.Generation;

/// <summary>
/// One supplement turn: something known, something genuinely not known, and no question allowed.
///
/// This is the composition Run-2 was never trained on. Every hardCase row in the Run-2 corpus was
/// routed to the hard/evaluation split, so of 2,000 exported training rows, zero carried
/// "question forbidden WITH an unresolved ambiguity or an admitted unknown". Run-2 does not
/// degrade on it — it extrapolates, and it extrapolates to a stub: 9.8% distinct openings on
/// hard-eval against 80.4% on validation.
///
/// The shape each of these has to teach is narrow and specific: say the part you know, name the
/// part you do not, and stop — without asking, without inventing the missing piece, and without
/// retreating into "I'll let you know". The <see cref="Known"/> fact is what keeps the reply
/// useful; the <see cref="Unknown"/> is what must survive; the <see cref="Act"/> is what stops
/// every answer becoming the same uncertainty template.
/// </summary>
public sealed record SupplementSituation(
    string Act,
    string UserMessage,
    string Known,
    string Unknown,
    string? Ambiguity = null,
    string? Background = null,
    string[]? KnownAnchors = null);

/// <summary>
/// The supplement catalogue, grouped by conversational act.
///
/// Eight acts, because the failure to avoid is a corpus that teaches one sentence. If every row
/// were an acknowledgement, the model would learn "acknowledge, then hedge" and produce that
/// shape for a joke, a summary and a correction alike — which is the stub problem again, wearing
/// a different template.
///
/// Every scenario id, fact, wording and family instance here is new. Nothing is copied from the
/// Run-2 corpus, and the 61 hard-eval rows are untouched.
/// </summary>
public static class SupplementSituations
{
    /// <summary>s1 — acknowledgement. Receive the news, hold the gap open.</summary>
    public static readonly IReadOnlyList<SupplementSituation> Acknowledgement =
    [
        new("acknowledgement", "did the payment go through?",
            "the payment cleared this morning",
            "whether the transfer fee was taken as well"),
        new("acknowledgement", "did the forms get filed?",
            "the forms went in on Tuesday", "whether the council has logged them yet",
            KnownAnchors: ["Tuesday"]),
        new("acknowledgement", "any post today?",
            "two things came for you", "which of them is the one you were waiting on",
            Ambiguity: "which of the two letters"),
        new("acknowledgement", "is the order confirmed?",
            "the order confirmation arrived", "what the delivery window actually is"),
        new("acknowledgement", "did she get back to you?",
            "Nadia replied late last night", "whether she has spoken to the others yet",
            KnownAnchors: ["Nadia"]),
        new("acknowledgement", "so the appeal went in?",
            "the appeal was submitted before the deadline",
            "how long they take to respond"),
    ];

    /// <summary>s2 — concise explanation. Explain the part that is settled, and only that part.</summary>
    public static readonly IReadOnlyList<SupplementSituation> Explanation =
    [
        new("explanation", "why is the report shorter this month?",
            "two of the sections were merged", "whether anything was dropped in the merge"),
        new("explanation", "what's the hold-up on the invoice?",
            "it is sitting with accounts for approval", "who is covering approvals this week"),
        new("explanation", "how does the new rota work?",
            "the rota rotates every three weeks", "which week you start on",
            KnownAnchors: ["three"]),
        new("explanation", "why did the alarm go off?",
            "the back sensor triggered it", "whether it was the cat or the wind"),
        new("explanation", "what changed in the contract?",
            "the notice period moved from one month to three", "why they wanted it changed",
            KnownAnchors: ["three"]),
        new("explanation", "why is the bill higher?",
            "the standing charge went up in April", "whether usage went up as well",
            KnownAnchors: ["April"]),
    ];

    /// <summary>s3 — emotional reaction. Feeling is not a substitute for the gap; both are said.</summary>
    public static readonly IReadOnlyList<SupplementSituation> Reaction =
    [
        new("reaction", "they let Marcus go this morning.",
            "Marcus was told at nine", "whether anyone else is affected",
            KnownAnchors: ["Marcus"]),
        new("reaction", "the results came back.",
            "the letter arrived opened and read", "what the follow-up appointment is for"),
        new("reaction", "I think I've made a mistake.",
            "the email has already gone out", "whether anyone has read it yet"),
        new("reaction", "she's moving to Aberdeen.",
            "the move is happening in spring", "whether it is permanent",
            KnownAnchors: ["Aberdeen"]),
        new("reaction", "we got the keys!",
            "completion went through at noon", "when the removals van can come"),
        new("reaction", "I'm so tired of this.",
            "this is the fourth week of it", "whether the referral has been made",
            KnownAnchors: ["fourth"]),
    ];

    /// <summary>s4 — summary. Compress what is known; do not smooth over what is not.</summary>
    public static readonly IReadOnlyList<SupplementSituation> Summary =
    [
        new("summary", "give me the state of it.",
            "three of the five workstreams are finished",
            "whether the last two are blocked or just slow",
            KnownAnchors: ["three", "five"]),
        new("summary", "where are we with the move?",
            "the survey and the searches are done", "when the mortgage offer lands"),
        new("summary", "what's outstanding?",
            "only the signage and the final inspection are left",
            "who is booking the inspection"),
        new("summary", "catch me up on the thread.",
            "the thread ended with Priya's proposal", "whether anyone has agreed to it",
            KnownAnchors: ["Priya"]),
        new("summary", "how did the week go?",
            "two releases went out and neither rolled back",
            "whether the support tickets are related"),
        new("summary", "what did the audit find?",
            "the audit raised four minor points", "whether any of them need a response",
            KnownAnchors: ["four"]),
    ];

    /// <summary>s5 — correction handling. Accept the correction; the gap it opens stays open.</summary>
    public static readonly IReadOnlyList<SupplementSituation> Correction =
    [
        new("correction", "no, it was Thursday, not Tuesday.",
            "the meeting was on Thursday", "whether the room booking moved with it",
            KnownAnchors: ["Thursday"]),
        new("correction", "it's Rowan who owns that, not me.",
            "Rowan owns the rollout", "whether Rowan has been told about the change",
            KnownAnchors: ["Rowan"]),
        new("correction", "I said forty, not fourteen.",
            "the figure is forty", "whether the earlier version went out with fourteen in it",
            KnownAnchors: ["forty"]),
        new("correction", "that's the old address.",
            "the current address is the one on the last invoice",
            "whether the delivery was sent to the old one"),
        new("correction", "she uses they/them.",
            "the note has been corrected", "whether the original went out uncorrected"),
        new("correction", "not the blue folder - the grey one.",
            "the grey folder is the current one", "what is still in the blue one",
            Ambiguity: "which folder the earlier note referred to"),
    ];

    /// <summary>s6 — practical response. Say what can be done now, not what you would need to ask.</summary>
    public static readonly IReadOnlyList<SupplementSituation> Practical =
    [
        new("practical", "the boiler's making that noise again.",
            "the engineer has a slot on Friday", "whether Friday works for you",
            KnownAnchors: ["Friday"]),
        new("practical", "we're out of printer paper.",
            "there is a half-ream in the second drawer",
            "whether the order was placed last week"),
        new("practical", "the train's cancelled.",
            "the replacement bus leaves from stand C", "how long the bus actually takes",
            KnownAnchors: ["stand C"]),
        new("practical", "I can't find the spare key.",
            "the spare was moved off the hook in October",
            "where it was moved to",
            KnownAnchors: ["October"]),
        new("practical", "the site's down.",
            "the status page is already showing the incident",
            "whether it is the database or the front end"),
        new("practical", "what do I bring?",
            "the list on the fridge covers most of it", "whether they are providing plates"),
    ];

    /// <summary>s7 — humour. A joke that still says the true thing and still leaves the gap.</summary>
    public static readonly IReadOnlyList<SupplementSituation> Humour =
    [
        new("humour", "the printer has eaten another document.",
            "the printer jammed on page three again",
            "whether the document survived",
            KnownAnchors: ["three"]),
        new("humour", "guess who forgot the milk.",
            "there is no milk in the house", "whether the corner shop is still open"),
        new("humour", "my plant is dying dramatically.",
            "the leaves started dropping this week", "whether it is water or light"),
        new("humour", "the cat has claimed the new chair.",
            "she has been on it since breakfast", "whether she intends to give it back"),
        new("humour", "another meeting that could have been an email.",
            "the meeting ran forty minutes", "whether anything was decided",
            KnownAnchors: ["forty"]),
        new("humour", "the sourdough has developed opinions.",
            "the starter doubled overnight", "whether it is ready to bake with"),
    ];

    /// <summary>
    /// s8 — fiction-aware. Inside a scene, uncertainty is narrated rather than admitted, and the
    /// scene must not resolve what the plan left open.
    /// </summary>
    public static readonly IReadOnlyList<SupplementSituation> Fiction =
    [
        new("fiction", "Vex stops at the fork in the tunnel.",
            "both passages carry the same draught",
            "which passage the sound came from",
            Ambiguity: "which of the two passages"),
        new("fiction", "she turns the letter over in her hands.",
            "the seal is unbroken", "who sent it"),
        new("fiction", "the lantern gutters.",
            "there is oil enough for an hour", "how far the tunnel still runs",
            KnownAnchors: ["hour"]),
        new("fiction", "he counts the coins twice.",
            "the purse is lighter than it was", "where the difference went"),
        new("fiction", "the door at the end is ajar.",
            "no light comes from beyond it", "whether anyone is through there"),
        new("fiction", "keep going.",
            "the corridor opens into a hall", "what is at the far end of it"),
    ];

    /// <summary>Every act, in a fixed order so a family index maps to the same act on every run.</summary>
    public static readonly IReadOnlyList<(string Family, string Act, IReadOnlyList<SupplementSituation> Pool)> Acts =
    [
        ("s1", "acknowledgement", Acknowledgement),
        ("s2", "explanation", Explanation),
        ("s3", "reaction", Reaction),
        ("s4", "summary", Summary),
        ("s5", "correction", Correction),
        ("s6", "practical", Practical),
        ("s7", "humour", Humour),
        ("s8", "fiction", Fiction),
    ];
}
