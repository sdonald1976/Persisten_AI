namespace Companion.Soak;

/// <summary>The outcome of running one scenario.</summary>
public sealed record Result(string Scenario, List<Fault> Faults, List<Turn> Turns, List<string> Notes)
{
    public bool Passed => Faults.Count == 0;
    public TimeSpan Took => TimeSpan.FromSeconds(Turns.Sum(t => t.Took.TotalSeconds));
}

/// <summary>
/// The conversations, so nobody has to keep inventing them.
///
/// Each one reproduces a way the companion has actually been found broken, phrased the way the
/// failure was originally hit rather than the way the code is written — which is the only reason
/// they catch anything the unit suite does not.
/// </summary>
public static class Scenarios
{
    public static IReadOnlyList<string> Names =>
        new[] { "memory", "fidelity", "register", "compound", "restart", "long", "context" };

    public static async Task<Result> RunAsync(string name, Api api, int promptBudget, int longTurns)
        => name switch
        {
            "memory" => await MemoryAsync(api, promptBudget),
            "context" => await ContextAsync(api, promptBudget),
            "fidelity" => await FidelityAsync(api, promptBudget),
            "register" => await RegisterAsync(api, promptBudget),
            "compound" => await CompoundAsync(api, promptBudget),
            "restart" => await RestartAsync(api, promptBudget),
            "long" => await LongAsync(api, promptBudget, longTurns),
            _ => throw new ArgumentException($"unknown scenario '{name}'", nameof(name)),
        };

    /// <summary>
    /// Facts stated in one conversation must be there in the next one.
    ///
    /// This is the whole product in a single check, and it was false for the entire life of the
    /// project: the extraction role was pointed at a model that answered "{}" to everything, so the
    /// store stayed empty while she recalled things perfectly well inside a conversation — because
    /// the transcript is in the prompt — and knew nothing the moment a new one began.
    /// </summary>
    private static async Task<Result> MemoryAsync(Api api, int budget)
    {
        var faults = new List<Fault>();
        var turns = new List<Turn>();
        var notes = new List<string>();

        // Where someone grew up: unmistakably durable, unmistakably biography, and the kind of
        // thing no extractor should ever pass over. The first two attempts at this probe both
        // failed for reasons that were the harness's fault rather than the companion's, and both
        // are worth remembering.
        //
        // It stated the same fact every run, so after the first run deduplication — working
        // correctly — meant the count never moved again and the check failed forever. Then it used
        // an invented board game, and the extractor returned "[]": asked to judge whether
        // "a board game called Bramvale17" is durable biography, it said no, which is a defensible
        // reading of a nonsense word. A probe has to be something a competent extractor would be
        // *wrong* to skip, or the test measures taste instead of capability.
        var nonce = Nonce();
        var stated = $"I grew up in a town called {nonce}.";

        var before = await api.MemoryCountAsync();

        var first = await api.StartConversationAsync("soak: memory (stating)");
        await SayAsync(api, first, stated, turns, faults, budget);

        var after = await api.MemoryCountAsync();
        notes.Add($"memories: {before} → {after} (stated \"{nonce}\")");

        // The real test, and the only one that matters: a new conversation sharing no transcript.
        // The count is reported but never judged on its own — a fact she already held is a pass, so
        // long as she can still produce it.
        var second = await api.StartConversationAsync("soak: memory (recalling)");
        var recall = await SayAsync(api, second, "What town did I grow up in?", turns, faults, budget);

        var retrieved = await api.LastTurnMemoriesRetrievedAsync();
        notes.Add($"memories retrieved into the fresh conversation: {retrieved}");
        if (after <= before)
            notes.Add("count did not move — either already known, or nothing was written");

        if (recall.Reply.Contains(nonce, StringComparison.OrdinalIgnoreCase))
            return new Result("memory", faults, turns, notes);

        faults.Add(after <= before
            ? new Fault(
                "no-memory-formed",
                $"\"{nonce}\" was neither stored nor recalled",
                "check Models:Extraction — a model too small for the prompt returns nothing, silently")
            : new Fault(
                "recall-missed",
                $"stored a memory but a fresh conversation never said \"{nonce}\" ({retrieved} retrieved)",
                Flat(recall.Reply)));

        return new Result("memory", faults, turns, notes);
    }

    /// <summary>
    /// A plausible but invented English place name. Plausible so the extractor treats it as real
    /// biography rather than noise; invented so a fluent guess cannot pass the recall check without
    /// anything having actually been remembered.
    /// </summary>
    private static string Nonce()
    {
        var starts = new[] { "Quill", "Marrow", "Thistle", "Bram", "Gild", "Vellum", "Harrow", "Fen" };
        var ends = new[] { "castle", "wick", "bourne", "hallow", "mere", "ridge", "vale", "gate" };
        var rng = Random.Shared;
        return starts[rng.Next(starts.Length)] + ends[rng.Next(ends.Length)];
    }

    /// <summary>
    /// A short message gets a short reply, and is not answered with an interview.
    ///
    /// Measured at its worst: a mean of 3.3 questions per reply, fifteen in one, and 926 characters
    /// back from "She snuggles and gives kisses :)".
    /// </summary>
    private static async Task<Result> RegisterAsync(Api api, int budget)
    {
        var faults = new List<Fault>();
        var turns = new List<Turn>();

        var conv = await api.StartConversationAsync("soak: register");
        foreach (var line in new[]
                 {
                     "Morning.",
                     "She snuggles and gives kisses :)",
                     "lol fair",
                     "Just be mindful of what you're doing :)",
                 })
        {
            var turn = await SayAsync(api, conv, line, turns, faults, budget);
            faults.AddRange(Checks.ForRegister(turn));
        }

        var questions = turns.Select(t => t.Reply.Count(c => c == '?')).ToList();
        var notes = new List<string>
        {
            $"questions per reply: {string.Join(", ", questions)}",
            $"reply lengths: {string.Join(", ", turns.Select(t => t.Reply.Length))}",
        };

        return new Result("register", faults, turns, notes);
    }

    /// <summary>
    /// Several things in one message stay several things.
    ///
    /// "I have an anniversary date today and I am working on you. Considering I drank too much
    /// again last night I am not sure if my mother is coming today or tomorrow" came back as
    /// "planning a special day with your mother" — two unrelated things fused into one event, and
    /// a third dropped entirely.
    /// </summary>
    private static async Task<Result> CompoundAsync(Api api, int budget)
    {
        var faults = new List<Fault>();
        var turns = new List<Turn>();

        var conv = await api.StartConversationAsync("soak: compound");
        var turn = await SayAsync(
            api, conv,
            "I have an anniversary date today and I am working on you. Considering I drank too much " +
            "again last night I am not sure if my mother is coming today or tomorrow :)",
            turns, faults, budget);

        // The specific fusion that happened. Not a general comprehension test — a regression guard
        // for one sentence that has been got wrong before.
        var reply = turn.Reply;
        var fused = new[] { "day with your mother", "day with your mom", "anniversary with your mother", "anniversary with your mom" };
        if (fused.Any(f => reply.Contains(f, StringComparison.OrdinalIgnoreCase)))
            faults.Add(new Fault("fused-facts", "merged the anniversary and the mother's visit into one event", Flat(reply)));

        var notes = new List<string>
        {
            reply.Contains("anniversary", StringComparison.OrdinalIgnoreCase) ? "mentions the anniversary" : "does not mention the anniversary",
            reply.Contains("mother", StringComparison.OrdinalIgnoreCase) || reply.Contains("mom", StringComparison.OrdinalIgnoreCase)
                ? "mentions the visit" : "does not mention the visit",
        };

        return new Result("compound", faults, turns, notes);
    }

    /// <summary>
    /// A conversation long enough to put the prompt under pressure.
    ///
    /// The trimming path had never once executed against a real model — it is the code protecting
    /// the original reported bug, and the longest run before this reached under half its budget.
    /// What matters here is not that trimming happens but that she is still herself afterwards, and
    /// still knows what was said at the start.
    /// </summary>
    private static async Task<Result> LongAsync(Api api, int budget, int count)
    {
        var faults = new List<Fault>();
        var turns = new List<Turn>();

        var conv = await api.StartConversationAsync("soak: long");
        await SayAsync(api, conv, "Before we start: my dog is called Precious and my deck is half rebuilt.", turns, faults, budget);

        var filler = new[]
        {
            "Work has been steady, mostly meetings.",
            "The weather turned cold this week.",
            "I watched a documentary about deep sea vents last night.",
            "Thinking about planting garlic before the frost.",
            "The car needs new tyres before winter.",
            "I have been sleeping badly lately.",
            "Made a decent curry at the weekend.",
            "The neighbours got a puppy.",
        };

        for (var i = 0; i < count; i++)
            await SayAsync(api, conv, filler[i % filler.Length], turns, faults, budget);

        var recall = await SayAsync(api, conv, "What did I say my dog was called, right at the start?", turns, faults, budget);
        if (!recall.Reply.Contains("Precious", StringComparison.OrdinalIgnoreCase))
            faults.Add(new Fault("lost-the-thread", "could not recall a fact from the first message of this conversation", Flat(recall.Reply)));

        var peak = turns.Max(t => t.PacketTokens);
        var notes = new List<string>
        {
            $"packet grew to ~{peak} tokens (budget {budget})",
            peak >= budget ? "the trimming path was exercised" : "budget was never reached — trimming still untested",
        };

        return new Result("long", faults, turns, notes);
    }

    /// <summary>
    /// What the store is holding at the end of a conversation, rather than how the replies read.
    ///
    /// Every check here failed against a running companion, and none of them is visible from the
    /// reply text — she talked her way through the whole conversation sounding entirely coherent
    /// while the store behind her filled up with facts the user never stated, lost the ones they
    /// did, and answered "nothing unfinished" to someone in the middle of a job with a deadline.
    /// That is why this scenario reads /memories and /loops instead of judging prose: a reply is
    /// generated fresh from a transcript that is still in the prompt, so it looks right for the
    /// entire session in which the damage is being done, and only a later session shows it.
    /// </summary>
    private static async Task<Result> FidelityAsync(Api api, int budget)
    {
        var faults = new List<Fault>();
        var turns = new List<Turn>();
        var notes = new List<string>();

        // A nonce per run: dedup is working correctly, so a fixed noun would be merged into the
        // memory left by the previous run and every check after the first would measure nothing.
        var nonce = Nonce();
        var conv = await api.StartConversationAsync("soak: fidelity");

        await SayAsync(api, conv, $"I'm rebuilding the irrigation at the {nonce} allotment before the frost.", turns, faults, budget);
        await SayAsync(api, conv, $"I've started a second thing too - a raised-bed build over at {nonce} Marsh Lane.", turns, faults, budget);

        // 1. Two projects are two projects. Both landing in the slot user/works_on used to mean the
        //    first was superseded by the second, and the audit trail called it a stated new value.
        var afterProjects = await api.MemoryStatesAsync();
        var irrigation = afterProjects.Where(m => Mentions(m.Content, "irrigation")).ToList();
        var beds = afterProjects.Where(m => Mentions(m.Content, "raised", "bed")).ToList();
        notes.Add($"projects held: {irrigation.Count} irrigation, {beds.Count} raised-bed");

        if (irrigation.Count > 0 && !irrigation.Any(m => m.Status == "Active"))
            faults.Add(new Fault("project-clobbered", "starting a second project retired the first", irrigation[0].Content));
        else if (irrigation.Count == 0)
            notes.Add("no irrigation memory was formed at all — check Models:Extraction");

        // 2. A question is not a statement. Every word of the fact below is in the message, which
        //    is exactly why evidence verification passed it: it checks the words are real, not that
        //    they were claimed.
        var before = await api.MemoryCountAsync();
        await SayAsync(api, conv, $"Did I ever tell you what timber I bought for the {nonce} beds?", turns, faults, budget);
        var invented = (await api.MemoryStatesAsync())
            .FirstOrDefault(m => Mentions(m.Content, "timber") && Mentions(m.Content, "bought", "buy", "purchas"));
        if (invented.Content is { Length: > 0 })
            faults.Add(new Fault("question-became-fact", "stored something the user only asked about", invented.Content));
        notes.Add($"memories after the question: {before} → {await api.MemoryCountAsync()}");

        // 3. A change the user announces has to land. The predicates differ ("drinks_coffee_black"
        //    then "prefers"), so this never worked through slot matching alone.
        await SayAsync(api, conv, "I drink my coffee black, no sugar.", turns, faults, budget);
        await SayAsync(api, conv, "Actually I've gone off black coffee. I take oat milk lattes now.", turns, faults, budget);

        var coffee = (await api.MemoryStatesAsync()).Where(m => Mentions(m.Content, "coffee", "latte")).ToList();
        var currentCoffee = coffee.Where(m => m.Status == "Active").ToList();
        notes.Add($"coffee facts: {string.Join("; ", coffee.Select(m => $"{m.Status} \"{Flat(m.Content)}\""))}");

        if (currentCoffee.Count > 1)
        {
            faults.Add(new Fault(
                "change-not-applied",
                $"{currentCoffee.Count} contradictory coffee facts are both current",
                Flat(currentCoffee[0].Content)));
        }
        else if (currentCoffee.Count == 1 && !Mentions(currentCoffee[0].Content, "latte", "oat"))
        {
            faults.Add(new Fault("change-not-applied", "the superseded preference is the one still current",
                Flat(currentCoffee[0].Content)));
        }

        // 4. Unfinished work is the product. Nothing episodic was extracted across an entire real
        //    conversation, so nothing ever opened a loop, so "what's unfinished?" said "nothing".
        var loops = await api.OpenLoopsAsync();
        notes.Add($"open loops: {loops.Count}");
        if (loops.Count == 0)
            faults.Add(new Fault("no-open-loop", "a deadline and an unfinished job left nothing on her radar", ""));

        // 5. Loops must be hers to hold, not hers to have invented. An open loop describing the
        //    companion doing the user's gardening came from a hallucinated first-person reply and
        //    then opened the following session.
        var appropriated = loops.FirstOrDefault(l =>
            Mentions(l, "compost", "planting", "heirloom", "tomato", "set aside", "sow"));
        if (appropriated is not null)
            faults.Add(new Fault("appropriated-loop", "opened a loop about living the user's life", Flat(appropriated)));

        return new Result("fidelity", faults, turns, notes);
    }

    /// <summary>
    /// An ordinary remark must not be mistaken for a commission.
    ///
    /// "I'm writing a talk on soil chemistry for the county show in October" matched the
    /// deliverable-verb list on the word "writing", which put a conversational turn on the
    /// auto-continuation path. The completion judge then declared a finished reply unfinished four
    /// times: five generation rounds, 277 seconds, and four complete answers with four sign-offs
    /// concatenated into one turn.
    /// </summary>
    private static async Task<Result> RestartAsync(Api api, int budget)
    {
        var faults = new List<Fault>();
        var turns = new List<Turn>();

        var conv = await api.StartConversationAsync("soak: restart");
        var turn = await SayAsync(
            api, conv,
            "Separate thing entirely: I'm writing a talk on soil chemistry for the county show in October.",
            turns, faults, budget);

        var notes = new List<string>
        {
            $"generation rounds: {turn.Rounds}",
            $"reply length: {turn.Reply.Length} characters in {turn.Took.TotalSeconds:N0}s",
        };

        if (turn.Rounds > 1)
        {
            faults.Add(new Fault(
                "continued-a-finished-reply",
                $"an ordinary remark was continued over {turn.Rounds} rounds",
                Flat(turn.Reply)));
        }

        // The visible signature, independent of the round count: a reply that says goodbye more
        // than once has answered more than once.
        var signOffs = SignOff.Matches(turn.Reply).Count;
        if (signOffs > 1)
            faults.Add(new Fault("restarted", $"signed off {signOffs} times in one reply", Flat(turn.Reply)));

        return new Result("restart", faults, turns, notes);
    }

    private static readonly System.Text.RegularExpressions.Regex SignOff = new(
        @"(let me know if|hope (this|that) helps|best of luck|happy planning|" +
        @"feel free to (ask|reach)|i'?m always here|glad to help)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
        | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool Mentions(string text, params string[] words)
        => words.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A short answer to her own question is read AS that answer.
    ///
    /// The live failure, verbatim: she asked "What's your favorite kind of magic?", the user said
    /// "Additive.", and the reply reinterpreted "Additive" as being about the relationship — with
    /// the question sitting right there in the prompt. The fix is system-side (the answer-binding
    /// rule, docs/LANGUAGE_ORGAN.md Phase 1), so the fault here is judged on the DECISION record,
    /// which is deterministic; whether the reply then honors the binding is the model's half, and
    /// is reported as a note either way so model comparisons have something to read.
    /// </summary>
    private static async Task<Result> ContextAsync(Api api, int budget)
    {
        var faults = new List<Fault>();
        var turns = new List<Turn>();
        var notes = new List<string>();

        var conv = await api.StartConversationAsync("soak: context");

        // The model cannot be scripted over HTTP, so invite the question instead. If it declines
        // to end with one, that is a note, not a fault — the scenario just cannot run this time.
        var ask = await SayAsync(api, conv,
            "Ask me one short question about my hobbies. Just the question, nothing else.",
            turns, faults, budget);
        if (!ask.Reply.TrimEnd().EndsWith('?'))
        {
            notes.Add($"model did not leave a trailing question; binding unmeasurable this run: {Flat(ask.Reply)}");
            return new Result("context", faults, turns, notes);
        }

        var answer = await SayAsync(api, conv, "Woodworking.", turns, faults, budget);
        Describe(answer, notes);

        var decisions = answer.Decisions ?? Array.Empty<string>();
        if (!decisions.Contains("interpretation=answers-open-question"))
        {
            faults.Add(new Fault(
                "answer-not-bound",
                "a one-word reply to her own trailing question was not bound by the system",
                Flat(ask.Reply)));
        }

        // The model's half: does the reply actually engage with the answer it was handed?
        notes.Add(answer.Reply.Contains("woodwork", StringComparison.OrdinalIgnoreCase)
            ? "reply engaged with the bound answer"
            : $"reply did not mention the bound answer (model-side; not a fault): {Flat(answer.Reply)}");

        // ---- stage 2: an ordinal against her own enumeration ----
        var list = await SayAsync(api, conv,
            "Suggest exactly three small workshop upgrades as a bulleted list, one short line each. No other text.",
            turns, faults, budget);
        if (list.Reply.Split('\n').Count(l => l.TrimStart().StartsWith('-') || l.TrimStart().StartsWith('*')) >= 2)
        {
            var pick = await SayAsync(api, conv, "Let's do the second one.", turns, faults, budget);
            Describe(pick, notes);
            var d2 = pick.Decisions ?? Array.Empty<string>();
            if (!d2.Contains("interpretation=resolves-reference") && !d2.Contains("interpretation=answers-open-question"))
            {
                faults.Add(new Fault(
                    "ordinal-not-resolved",
                    "\"the second one\" against her own bulleted list was not resolved by the system",
                    Flat(list.Reply)));
            }
        }
        else
        {
            notes.Add($"model did not produce a bulleted list; ordinal case unmeasurable this run: {Flat(list.Reply)}");
        }

        // ---- stage 3: a pronoun back to a named person (heuristic — reported, not faulted) ----
        await SayAsync(api, conv, "My sister Beth is visiting on Saturday.", turns, faults, budget);
        var pronoun = await SayAsync(api, conv, "I'm planning a small dinner for her.", turns, faults, budget);
        Describe(pronoun, notes);

        // ---- stage 4: THE CANONICAL CLARIFY SPECIMEN (intent promotion eval) ----
        // Two possible "her"s: the system must select clarify (deterministic — faulted), and
        // whether the model then asks which sister or answers anyway is the note the eventual
        // promotion decision reads. In the first live shadow run it answered anyway.
        await SayAsync(api, conv, "My other sister Clara is arriving along with Beth as well.", turns, faults, budget);
        var cook = await SayAsync(api, conv, "What should I cook for her?", turns, faults, budget);
        Describe(cook, notes);
        if (!(cook.Decisions ?? Array.Empty<string>()).Contains("intent=clarify"))
        {
            faults.Add(new Fault(
                "clarify-not-selected",
                "an ambiguous 'her' question did not classify as clarify (shadow intent)",
                Flat(cook.Reply)));
        }
        var askedWhich = cook.Reply.Contains('?')
            && (cook.Reply.Contains("which", StringComparison.OrdinalIgnoreCase)
                || (cook.Reply.Contains("Beth") && cook.Reply.Contains("Clara")));
        notes.Add(askedWhich
            ? "canonical clarify: model ASKED which sister — agrees with system intent"
            : $"canonical clarify: model answered without asking (the canonical disagreement): {Flat(cook.Reply)}");

        return new Result("context", faults, turns, notes);
    }

    /// <summary>The working-context evidence for one turn, written into the scenario notes.</summary>
    private static void Describe(Turn turn, List<string> notes)
    {
        if (turn.WorkingContext is { } wc)
            notes.Add($"[{Flat(turn.Sent)}] {wc}");
        if (turn.RetrievedRaw is { Count: > 0 } raw)
        {
            notes.Add($"  retrieved (raw query): {string.Join(" / ", raw.Select(Flat))}");
            notes.Add($"  retrieved (resolved):  {string.Join(" / ", (turn.Retrieved ?? Array.Empty<string>()).Select(Flat))}");
        }
    }

    private static async Task<Turn> SayAsync(
        Api api, string conv, string message, List<Turn> turns, List<Fault> faults, int budget)
    {
        var turn = await api.SayAsync(conv, message);
        faults.AddRange(Checks.ForTurn(turn, turns, budget));
        turns.Add(turn);
        return turn;
    }

    private static string Flat(string text)
    {
        var flat = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        return flat.Length <= 120 ? flat : flat[..120] + "…";
    }
}
