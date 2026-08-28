namespace Companion.MouthFactory.Generation;

/// <summary>
/// One fact a plan can carry, plus the wording in it that cannot survive paraphrase.
///
/// <see cref="Anchors"/> is EMPTY for almost every fact, and that is the point. The writer is
/// instructed to convey meaning in fresh words, so demanding lexical overlap with an ordinary
/// proposition punishes the behaviour the corpus exists to teach. Anchors are for the residue that
/// genuinely has one correct surface: a person's name, a day, a time, a filename, a quoted term,
/// an exact quantity. "Priya" cannot be paraphrased; "the bread came out flat" can.
/// </summary>
public sealed record SituationFact(string Text, params string[] Anchors);

/// <summary>
/// A concrete thing that has happened, and the user turn that raises it.
///
/// The pilot's Layer A scenarios paired an arbitrary fact with an arbitrary prompt: a plan saying
/// "the printer jammed again" arrived under the user message "so what's the story?". Nothing tied
/// them together, so the writer answered the prompt and ignored the plan — one accepted row
/// narrates an entire meeting that appears nowhere in its scenario. And where the plan carried
/// nothing at all, "any news?" left no honest reply except deferral: a quarter of those rows are
/// non-answers, which is what a corpus teaches when it asks a question that has no answer in it.
///
/// A situation fixes both by construction. The user message carries the topic, the facts belong to
/// that same topic, and a reply that drifts elsewhere is visibly wrong to the faithfulness critic
/// because the critic is now shown the message too.
/// </summary>
public sealed record Situation(
    string Id,
    string UserMessage,
    IReadOnlyList<SituationFact> Facts,
    IReadOnlyList<string>? Background = null,
    IReadOnlyList<string>? Unknowns = null,
    IReadOnlyList<string>? Ambiguities = null,
    string? Question = null,
    IReadOnlyList<string>? RequiredTokens = null,
    SituationFact? Forbidden = null,
    SituationCorrection? Correction = null)
{
    public IReadOnlyList<string> BackgroundItems => Background ?? [];
    public IReadOnlyList<string> UnknownItems => Unknowns ?? [];
    public IReadOnlyList<string> AmbiguityItems => Ambiguities ?? [];
    public IReadOnlyList<string> ExactTokens => RequiredTokens ?? [];
}

/// <summary>
/// A fact that was corrected, and the token that tells the two versions apart.
///
/// <see cref="Discriminator"/> is what marks the STALE claim and appears in no correct reply. It is
/// declared rather than derived because deriving it from the prose is what deleted the corrections
/// stratum: every content word of "the meeting is on Thursday" became forbidden, including the
/// word both versions share.
/// </summary>
public sealed record SituationCorrection(
    string StaleText, string CurrentText, string Discriminator, string Anchor);

/// <summary>
/// The situation catalogue, grouped by the register each group suits.
///
/// Size is a correctness property, not a nicety. The pilot drew Layer A from four small-talk
/// strings and four prompts, so 684 of 2,585 rejections — 26.5% — were the writer producing the
/// same sentence twice. Diversity at the scenario level is the only thing that fixes that;
/// generating more targets per scenario just produces more copies of the same reply.
/// </summary>
public static class Situations
{
    /// <summary>Everyday domestic and personal life. The default pool for Layer A.</summary>
    public static readonly IReadOnlyList<Situation> Everyday =
    [
        new("bread", "did the loaf work out this time?",
            [new SituationFact("the bread came out flat again")],
            Background: ["the kitchen is too cold for a good rise"]),

        new("bus", "you're late - what happened?",
            [new SituationFact("the 7:40 was cancelled", "7:40"),
             new SituationFact("the next one was twenty minutes behind"),
             new SituationFact("the replacement bus went the long way round")]),

        new("rain", "did you get caught in that downpour?",
            [new SituationFact("it rained solidly from two until six")]),

        new("boiler", "any word from the plumber?",
            [new SituationFact("the plumber can come Thursday morning", "Thursday")],
            Unknowns: ["what the repair will cost"]),

        new("cat", "is she still sulking at me?",
            [new SituationFact("the cat has been asleep on the radiator all afternoon")]),

        new("parcel", "did anything turn up while I was out?",
            [new SituationFact("the parcel arrived just after eleven")],
            Background: ["the neighbour signed for it"]),

        new("garden", "how's the tomato situation looking?",
            [new SituationFact("three of the tomato plants have blight"),
             new SituationFact("the rest look healthy enough"),
             new SituationFact("the courgettes have taken over the bed")]),

        new("dentist", "did you manage to move my appointment?",
            [new SituationFact("the dentist moved you to the 14th", "14th")]),

        new("neighbour", "was that shouting next door again last night?",
            [new SituationFact("it went on until about one")],
            Background: ["they have been arguing most evenings this week"]),

        new("car", "what did the garage say about the noise?",
            [new SituationFact("the garage says it is a heat shield"),
             new SituationFact("it will be forty pounds to fix", "forty"),
             new SituationFact("they can do it on Wednesday", "Wednesday")]),

        new("kettle", "is there tea going?",
            [new SituationFact("the kettle has just boiled")]),

        new("laundry", "is anything dry yet?",
            [new SituationFact("the washing is still damp"),
             new SituationFact("the radiator has been off all day")]),

        new("shopping", "did you remember the coffee?",
            [new SituationFact("there was no coffee left on the shelf"),
             new SituationFact("the shop had the wrong kind of milk"),
             new SituationFact("the bread was reduced so there are two loaves")]),

        new("bike", "is the bike rideable?",
            [new SituationFact("the back tyre is flat again")],
            Unknowns: ["whether it is the same puncture as before"]),

        new("phone", "did my mother ring?",
            [new SituationFact("your mother rang about half four", "half four")]),

        new("holiday", "did you get anywhere with the booking?",
            [new SituationFact("the flights are held until Friday", "Friday"),
             new SituationFact("the hotel wants a deposit"),
             new SituationFact("the cheaper week is the one after")]),

        new("builder", "what did the builder say about the roof?",
            [new SituationFact("two tiles have slipped"),
             new SituationFact("the felt underneath is sound"),
             new SituationFact("he can start the week after next")]),

        new("party", "is Saturday still happening?",
            [new SituationFact("eleven people have said yes", "eleven"),
             new SituationFact("your sister cannot make it")]),
    ];

    /// <summary>Work, technical and procedural. Where exact surface most often matters.</summary>
    public static readonly IReadOnlyList<Situation> Working =
    [
        new("build", "did the rebuild go through?",
            [new SituationFact("the second build finished clean")],
            Question: "do you want me to kick off the deploy?"),

        new("tests", "how did the suite do overnight?",
            [new SituationFact("the test suite passed"),
             new SituationFact("the disk is nearly full"),
             new SituationFact("two of the slow tests were skipped")],
            Background: ["the backup ran at three"],
            Question: "shall I clear the old artefacts out?"),

        new("deploy", "are we clear to ship?",
            [new SituationFact("the deployment is waiting on approval")],
            Question: "do you want it held until morning?"),

        new("review", "when did Priya want the review?",
            [new SituationFact("Priya moved the review to Thursday", "Priya", "Thursday")]),

        new("script", "which script does the release use now?",
            [new SituationFact("it runs release-prod.sh now", "release-prod.sh")],
            RequiredTokens: ["release-prod.sh"]),

        new("printer", "is the printer working yet?",
            [new SituationFact("the printer jammed again"),
             new SituationFact("the spare toner is the wrong model")],
            Question: "do you want me to order the right cartridge?"),

        new("meeting", "how did the planning meeting go?",
            [new SituationFact("the meeting overran by an hour"),
             new SituationFact("nothing was decided about the timeline"),
             new SituationFact("Priya is writing the summary up", "Priya")],
            Question: "should I put another one in for Friday?"),

        new("invoice", "has the client paid yet?",
            [new SituationFact("the client paid on Friday", "Friday")],
            Background: ["the invoice was three weeks overdue"],
            Question: "do you want the next one sent early?"),

        new("outage", "is the site back?",
            [new SituationFact("the site came back at nine"),
             new SituationFact("the cause was a certificate that expired"),
             new SituationFact("the renewal is automated now")],
            Question: "shall I write it up as an incident?"),

        new("interview", "how did the candidate do?",
            [new SituationFact("the candidate answered the design question well")],
            Unknowns: ["whether they will accept the salary"],
            Question: "shall I set up a second round?"),

        new("laptop", "did IT sort the laptop?",
            [new SituationFact("IT replaced the battery"),
             new SituationFact("it still will not charge past eighty percent", "eighty")],
            Question: "shall I raise it with them again?"),

        new("ticket", "what happened with that support ticket?",
            [new SituationFact("the ticket was closed without a reply")],
            Question: "do you want it reopened?"),

        new("migration", "did the data migration finish?",
            [new SituationFact("the migration finished at four this morning", "four")],
            Ambiguities: ["which of the two databases was migrated"],
            Question: "do you want me to check which one?"),

        new("contract", "did the contract come back signed?",
            [new SituationFact("the contract came back unsigned"),
             new SituationFact("they want the payment terms changed"),
             new SituationFact("legal have already seen the new draft")],
            Question: "should I send it back tonight or leave it?"),

        new("standup", "anything from standup I should know?",
            [new SituationFact("the design review slipped to next week")],
            Background: ["two people are off sick"],
            Question: "do you want me to move your one-to-one?"),

        new("release", "where did the release get to?",
            [new SituationFact("the release went out at six", "six"),
             new SituationFact("one customer has already reported a bug"),
             new SituationFact("the rollback is ready if we need it")],
            Question: "do you want me to roll it back?"),

        new("budget", "did the budget get signed off?",
            [new SituationFact("the budget was cut by a fifth"),
             new SituationFact("the contractor line survived intact"),
             new SituationFact("Priya is redoing the forecast", "Priya")],
            Question: "shall I pull the contractor numbers together?"),

        new("audit", "how did the audit go?",
            [new SituationFact("the audit found three minor issues"),
             new SituationFact("nothing was material")],
            Question: "do you want the three written up?"),
    ];

    /// <summary>
    /// Turns where the USER brings the news and the reply reacts to it.
    ///
    /// This is what a legitimate zero-must plan looks like, and the frozen corpus is full of them:
    /// 127 of its 730 rows carry no SAY item, and one of them answers "I got the promotion!" with
    /// "YES! After three weeks of that knot in your stomach — you earned this one." Nothing was
    /// required because nothing needed supplying; the turn already had its content.
    ///
    /// The distinction that matters is not whether the plan obliges something, but whether the
    /// TURN carries something. A zero-must plan over "any news?" has neither and produces evasion.
    /// A zero-must plan over "I got the promotion!" produces that line.
    /// </summary>
    public static readonly IReadOnlyList<Situation> Acknowledgements =
    [
        new("promotion", "I got the promotion!", []),
        new("offer-in", "they accepted the offer on the flat.", []),
        new("failed-mot", "the car failed its MOT. Again.", []),
        new("finished-it", "right - that's the whole thing finished.", []),
        new("bad-news", "they've cancelled the project.", []),
        new("quit", "I handed my notice in this morning.", []),
        new("scan-clear", "the scan came back clear.", []),
        new("dog", "we're getting the dog on Saturday.", []),
        new("row", "I had a blazing row with my brother.", []),
        new("locked-out", "I've locked myself out. Standing in the rain.", []),
        new("won", "we won. Nobody expected that.", []),
        new("tired-out", "I have not slept properly in four days.", []),
        new("gave-up", "I'm giving up on the sourdough.", []),
        new("moved", "the last box is in. We're moved.", []),
        new("results", "she got the grades she needed.", []),
        new("redundant", "they made half the team redundant today.", []),
    ];

    /// <summary>Warmth, closeness and intimacy. Register variation, never a content class.</summary>
    public static readonly IReadOnlyList<Situation> Close =
    [
        new("late-home", "I'm home - sorry, it went on forever.",
            [new SituationFact("dinner has been in the oven since seven", "seven")]),

        new("missed", "I missed you today.",
            [new SituationFact("the flat has been very quiet")]),

        new("tired", "I am completely done in.",
            [new SituationFact("you have been up since five", "five")]),

        new("compliment", "you looked good tonight, you know.",
            [new SituationFact("you noticed him watching from across the room")]),

        new("argument", "I shouldn't have said that earlier.",
            [new SituationFact("it landed harder than he meant it to")]),

        new("closer", "come here.",
            [new SituationFact("he has not stopped thinking about last night")]),

        new("slow", "say that again, slower.",
            [new SituationFact("you said it deliberately the first time")]),

        new("teasing", "you're impossible, you know that?",
            [new SituationFact("you have been winding him up all evening")]),

        new("evening-in", "what do you fancy doing tonight?",
            [new SituationFact("nothing is booked"),
             new SituationFact("the good wine is still unopened"),
             new SituationFact("he is asleep on the sofa already")]),

        new("after", "that was something.",
            [new SituationFact("neither of you has moved yet"),
             new SituationFact("it is starting to get light outside")]),

        new("apart", "another week away. I hate this bit.",
            [new SituationFact("it is four days until he is back", "four"),
             new SituationFact("the bed is too big without him")]),
    ];

    /// <summary>Fiction. Invented scene content is licensed; the frame decides that, not the topic.</summary>
    public static readonly IReadOnlyList<Situation> Fiction =
    [
        new("cave", "Vex draws her blade and steps into the dark.",
            [new SituationFact("the lantern goes out")]),

        new("narrow", "the passage narrows ahead.",
            [new SituationFact("something is moving further down")]),

        new("continue", "keep going - what happens next?",
            [new SituationFact("the door at the end is already open")]),

        new("exit", "alright, out of the scene for a sec.",
            [new SituationFact("you are back in the room with him")]),

        new("switch", "forget the cave - start something new.",
            [new SituationFact("the old scene is closed")]),

        new("market", "Vex pushes through the crowd towards the stall.",
            [new SituationFact("the stallholder recognises her")]),

        new("storm", "the ship is taking water.",
            [new SituationFact("the mast has already gone"),
             new SituationFact("the crew are cutting the rigging free"),
             new SituationFact("land is somewhere off the port bow")]),

        new("dark-corridor", "she edges along the wall.",
            [new SituationFact("the lantern goes out"),
             new SituationFact("something further down is breathing")]),

        new("confront", "Vex steps out where they can see her.",
            [new SituationFact("three of them turn at once", "three"),
             new SituationFact("the one in front is already reaching for a blade"),
             new SituationFact("the door behind her has closed")]),

        new("aftermath", "and afterwards?",
            [new SituationFact("the hall is empty now"),
             new SituationFact("her hands will not stop shaking")]),
    ];


    /// <summary>
    /// Correction turns: the user fixes something the companion had wrong.
    ///
    /// b3 drew all 89 of its scenarios from one of these in the last run - six distinct inputs
    /// across the whole stratum - so the writer produced the same acknowledgement over and over
    /// and dedup rejected most of it. A correction is a structure, not a sentence, and it fits any
    /// fact with a value that can change.
    /// </summary>
    public static readonly IReadOnlyList<Situation> Corrections =
    [
        new("day", "wait, no - it's Tuesday, not Thursday.", [],
            Correction: new SituationCorrection(
                "the meeting is on Thursday", "the meeting is on Tuesday", "thursday", "Tuesday"),
            Unknowns: ["who booked the room"]),

        new("room", "it's in the Anderson room, not the Blake room.", [],
            Correction: new SituationCorrection(
                "the review is in the Blake room", "the review is in the Anderson room",
                "blake", "Anderson")),

        new("who", "it was Priya who sent it, not Tom.", [],
            Correction: new SituationCorrection(
                "Tom sent the contract", "Priya sent the contract", "tom", "Priya")),

        new("count", "there were nine of them, not twelve.", [],
            Correction: new SituationCorrection(
                "twelve people came", "nine people came", "twelve", "nine")),

        new("time", "half seven, not half eight.", [],
            Correction: new SituationCorrection(
                "the train is at half eight", "the train is at half seven",
                "half eight", "half seven")),

        new("pronoun", "she uses she/her, I got that wrong earlier.", [],
            Correction: new SituationCorrection(
                "the request came from him", "the request came from her", "him", "her")),

        new("place", "it's the Leeds office, not Sheffield.", [],
            Correction: new SituationCorrection(
                "the audit is at the Sheffield office", "the audit is at the Leeds office",
                "sheffield", "Leeds")),

        new("cost", "it was ninety, not nineteen.", [],
            Correction: new SituationCorrection(
                "the part cost nineteen pounds", "the part cost ninety pounds",
                "nineteen", "ninety")),

        new("date", "the 12th, not the 21st.", [],
            Correction: new SituationCorrection(
                "the deadline is the 21st", "the deadline is the 12th", "21st", "12th")),

        new("version", "we're on 4.2, not 4.1.", [],
            Correction: new SituationCorrection(
                "production runs 4.1", "production runs 4.2", "4.1", "4.2")),

        new("month", "it's March, not May.", [],
            Correction: new SituationCorrection(
                "the renewal is in May", "the renewal is in March", "May", "March")),

        new("team", "that's the platform team, not infrastructure.", [],
            Correction: new SituationCorrection(
                "infrastructure own the pipeline", "platform own the pipeline",
                "infrastructure", "platform")),

        new("name", "it's Aoife, not Martin - I keep getting that wrong too.", [],
            Correction: new SituationCorrection(
                "Martin is running the workshop", "Aoife is running the workshop",
                "Martin", "Aoife")),

        new("morning", "afternoon, not morning.", [],
            Correction: new SituationCorrection(
                "the delivery comes in the morning", "the delivery comes in the afternoon",
                "morning", "afternoon")),

        new("outcome", "no - it passed. I misread it.", [],
            Correction: new SituationCorrection(
                "the inspection failed", "the inspection passed", "failed", "passed")),

        new("venue", "the Crown, not the Bell.", [],
            Correction: new SituationCorrection(
                "they are meeting at the Bell", "they are meeting at the Crown",
                "bell", "Crown")),

        new("relation", "he's my cousin, not my brother.", [],
            Correction: new SituationCorrection(
                "your brother is visiting", "your cousin is visiting", "brother", "cousin")),

        new("distance", "forty miles, not fourteen.", [],
            Correction: new SituationCorrection(
                "it is fourteen miles away", "it is forty miles away", "fourteen", "forty")),
    ];

    /// <summary>
    /// Turns where the reply must reproduce an identifier exactly. The one place paraphrase is a
    /// defect rather than the instruction.
    /// </summary>
    public static readonly IReadOnlyList<Situation> Procedures =
    [
        new("script", "which script does the release use now?",
            [new SituationFact("the release runs release-prod.sh now", "release-prod.sh")],
            RequiredTokens: ["release-prod.sh"]),

        new("branch", "what's the branch called?",
            [new SituationFact("the work is on hotfix/session-timeout", "hotfix/session-timeout")],
            RequiredTokens: ["hotfix/session-timeout"]),

        new("flag", "what do I pass to skip the cache?",
            [new SituationFact("it takes --no-cache", "--no-cache")],
            RequiredTokens: ["--no-cache"]),

        new("table", "which table did it write to?",
            [new SituationFact("it wrote to audit_events", "audit_events")],
            RequiredTokens: ["audit_events"]),

        new("port", "what port is it listening on?",
            [new SituationFact("it is on 5266", "5266")],
            RequiredTokens: ["5266"]),

        new("package", "what's the package called again?",
            [new SituationFact("it is Companion.PlanV3", "Companion.PlanV3")],
            RequiredTokens: ["Companion.PlanV3"]),

        new("error", "what did the error actually say?",
            [new SituationFact("it said ERR_CERT_DATE_INVALID", "ERR_CERT_DATE_INVALID")],
            RequiredTokens: ["ERR_CERT_DATE_INVALID"]),

        new("host", "which box is it running on?",
            [new SituationFact("it runs on build-07", "build-07")],
            RequiredTokens: ["build-07"]),

        new("endpoint", "what's the health endpoint?",
            [new SituationFact("it answers on /api/version", "/api/version")],
            RequiredTokens: ["/api/version"]),

        new("env", "which variable sets the writer model?",
            [new SituationFact("it reads MOUTH_WRITER_MODEL", "MOUTH_WRITER_MODEL")],
            RequiredTokens: ["MOUTH_WRITER_MODEL"]),

        new("file", "where does the manifest get written?",
            [new SituationFact("it writes export/manifest.json", "export/manifest.json")],
            RequiredTokens: ["export/manifest.json"]),

        new("command", "how do I resume it?",
            [new SituationFact("the command is mouth-factory resume", "mouth-factory resume")],
            RequiredTokens: ["mouth-factory resume"]),

        new("model", "which one is the judge again?",
            [new SituationFact("faithfulness runs on phi4", "phi4")],
            RequiredTokens: ["phi4"]),

        new("seed", "what seed did that use?",
            [new SituationFact("it ran with seed 20260826", "20260826")],
            RequiredTokens: ["20260826"]),

        new("queue", "which queue does it publish to?",
            [new SituationFact("it publishes to turn-events", "turn-events")],
            RequiredTokens: ["turn-events"]),

        new("cert", "what expired exactly?",
            [new SituationFact("the wildcard cert for *.internal expired", "*.internal")],
            RequiredTokens: ["*.internal"]),
    ];

    /// <summary>
    /// Turns carrying something upstream marked must_not_express. The forbidden item is real and
    /// adjacent, because a forbidden item nobody would mention teaches nothing.
    /// </summary>
    public static readonly IReadOnlyList<Situation> Withheld =
    [
        new("parcel-w", "did anything turn up while I was out?",
            [new SituationFact("the parcel arrived just after eleven")],
            Background: ["the courier wore a blue jacket"],
            Forbidden: new SituationFact("the invoice is three weeks overdue")),

        new("tests-w", "how did the suite do overnight?",
            [new SituationFact("the test suite passed")],
            Background: ["the backup ran at three"],
            Forbidden: new SituationFact("two people are being let go on Friday")),

        new("build-w", "did the rebuild go through?",
            [new SituationFact("the second build finished clean")],
            Background: ["the neighbour complained about the noise"],
            Forbidden: new SituationFact("the client has asked to renegotiate")),

        new("dinner-w", "anything I should know before tonight?",
            [new SituationFact("the table is booked for eight", "eight")],
            Background: ["it rained all afternoon"],
            Forbidden: new SituationFact("your sister has already told them the news")),

        new("scan-w", "did the post come?",
            [new SituationFact("two cards came for you")],
            Background: ["the cat got out again"],
            Forbidden: new SituationFact("the hospital letter arrived as well")),

        new("car-w", "what did the garage say?",
            [new SituationFact("the garage says it is a heat shield")],
            Background: ["they were short-staffed today"],
            Forbidden: new SituationFact("the MOT is due next week")),

        new("bank-w", "anything in the post?",
            [new SituationFact("the new cards arrived")],
            Background: ["the postman was late again"],
            Forbidden: new SituationFact("the overdraft letter came too")),

        new("work-w", "how was your day?",
            [new SituationFact("the release went out on time")],
            Background: ["the office was freezing"],
            Forbidden: new SituationFact("your role is on the restructure list")),

        new("family-w", "did you speak to Mum?",
            [new SituationFact("she rang about Sunday lunch")],
            Background: ["the line was terrible"],
            Forbidden: new SituationFact("she has been to the doctor twice this month")),

        new("house-w", "any news from the estate agent?",
            [new SituationFact("two more viewings are booked")],
            Background: ["the survey is still outstanding"],
            Forbidden: new SituationFact("the buyers have reduced their offer")),

        new("trip-w", "are we still on for Friday?",
            [new SituationFact("the tickets are confirmed")],
            Background: ["the weather looks poor"],
            Forbidden: new SituationFact("Tom has said he cannot come")),
    ];

    /// <summary>Turns that invite advice, where inventing biography is the failure mode.</summary>
    public static readonly IReadOnlyList<Situation> Advice =
    [
        new("weekend", "what do you reckon I should do this weekend?",
            [new SituationFact("nothing is in the diary this weekend")]),

        new("evening", "what should I do with myself tonight?",
            [new SituationFact("you have nothing on until Thursday", "Thursday")]),

        new("gift", "what do I get her, then?",
            [new SituationFact("her birthday is on the 9th", "9th")]),

        new("break", "I need to get away. Where?",
            [new SituationFact("you have four days of leave left", "four")]),

        new("read", "give me something to read.",
            [new SituationFact("there are two unread books by the bed")]),

        new("supper", "what are we doing about food?",
            [new SituationFact("there is not much in the fridge")]),

        new("film", "pick something for tonight.",
            [new SituationFact("you fell asleep in the last two")]),

        new("exercise", "I need to move more. Ideas?",
            [new SituationFact("the bike has a flat tyre")]),

        new("sunday", "what shall we do Sunday?",
            [new SituationFact("the forecast is dry until three", "three")]),

        new("study", "should I sign up for the course?",
            [new SituationFact("enrolment closes on the 30th", "30th")]),
    ];

    public static IReadOnlyList<Situation> ForFamily(string familyId) => familyId switch
    {
        "a6a" or "a6b" or "a6c" or "a6e" or "a6f" => Close,
        "a7a" or "a7b" or "b11" => Fiction,
        "a4" or "b5" or "b7" or "b9" => Working,
        "a2" or "a3" => Working.Concat(Everyday).ToList(),
        _ => Everyday.Concat(Working).ToList(),
    };
}
