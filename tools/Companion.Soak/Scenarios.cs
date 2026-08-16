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
    public static IReadOnlyList<string> Names => new[] { "memory", "register", "compound", "long" };

    public static async Task<Result> RunAsync(string name, Api api, int promptBudget, int longTurns)
        => name switch
        {
            "memory" => await MemoryAsync(api, promptBudget),
            "register" => await RegisterAsync(api, promptBudget),
            "compound" => await CompoundAsync(api, promptBudget),
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
