using System.Text.Json;
using Companion.MouthFactory.Schema;
using Companion.PlanV3;

namespace Companion.MouthFactory.Generation;

/// <summary>
/// The real target source: a configured local TargetWriter writes the utterance, and separately
/// configured critics judge it.
///
/// The separation is enforced by <see cref="RoleRouter"/>, not by convention here — the writer
/// role physically cannot be asked for a verdict, and a critic role physically cannot be asked
/// for a target.
///
/// The writer receives the mouth's exact USER message — the CompactV4 plan and transcript window
/// built by <c>MouthPromptV4</c> — because a teacher shown a different framing of the task writes
/// targets for a task the mouth will never be given.
///
/// Its SYSTEM message is the mouth's system message PLUS teacher rules, and the difference is
/// deliberate. R5 §4's system prompt is Ava's rendered context packet, which describes who she is
/// and contains no instruction to obey a plan — an untrained teacher handed it asks questions the
/// plan forbids and recites items it was told to keep silent. The trained mouth learns plan
/// obedience from tens of thousands of examples; a teacher has to be told.
///
/// This does NOT change the row. <c>RowRendering</c> builds the stored row from
/// <c>MouthPromptV4</c> independently, so what is trained on stays byte-identical to what the
/// shipping renderer will produce. Only the teacher sees the extra rules.
/// </summary>
public sealed class ModelTargetSource(RoleRouter roles, long runSeed) : ITargetSource
{
    /// <summary>
    /// LINGUISTIC QUALITY ONLY. The previous prompt asked whether a line "sounds like a real
    /// person", and a 3B judge read that as licence to grade politeness, completeness and
    /// taste: it rejected 12 of 15 natural human lines, called "Build is done." incomplete and
    /// badly punctuated, and failed a profane line for "inappropriate and offensive language"
    /// while being told in the same breath that subject matter is never a reason to fail.
    ///
    /// The question is narrowed to what a language judge is for, licences are stated AS
    /// licences rather than as exceptions, and the non-criteria are enumerated - a general
    /// instruct model supplies them itself otherwise.
    /// </summary>
    private const string NaturalnessSystem =
        """
        You judge ONE thing: does this line read as fluent, idiomatic human speech?

        FAIL it only for linguistic defects:
        - broken grammar or syntax a fluent speaker would not produce
        - garbled, incoherent or self-contradicting phrasing
        - stiff corporate or assistant register ("I would be happy to assist you with that")
        - mechanical repetition, or a sentence that reads as generated boilerplate

        PASS it in all of these. They are licensed and are NOT defects:
        - very short replies and sentence fragments. "Done." and "Build is done." are natural.
        - profanity, crudeness, explicit sexual language
        - romance, flirtation, intimacy
        - dark, violent or disturbing content in fiction
        - blunt, cold, sarcastic, skeptical or impolite tone
        - deliberate vagueness, evasion, or declining to elaborate
        - saying less than you think the situation warrants

        You are NOT judging politeness, appropriateness, safety, helpfulness, completeness,
        whether it answers fully, or whether you like it. Subject matter is NEVER a reason to
        fail a line.

        Reply with JSON only: {"natural": true|false, "why": "..."}
        """;

    /// <summary>
    /// MEANING ONLY, with the whole plan in view.
    ///
    /// The previous version received only the must-express and must-not-express texts, then
    /// asked whether the reply said "nothing beyond" them. Every optional item, permitted
    /// question and licensed register choice therefore read as unsupported: it falsely
    /// rejected 5 of 9 compliant fixtures, and the causes were exactly the fields it was
    /// never given.
    /// </summary>
    private const string FaithfulnessSystem =
        """
        You judge whether a reply is FAITHFUL to a plan already decided. Judge meaning only.

        FAIL it only for these:
        - a REQUIRED point is missing (it may be reworded freely; only its meaning must survive)
        - a FORBIDDEN point is stated, hinted at, or explained
        - BACKGROUND content surfaces as a claim rather than merely colouring the tone
        - it asserts a specific fact nobody supplied: a detail, name, number, cause or
          relationship invented to fill a gap
        - it resolves something marked AMBIGUOUS instead of leaving it open
        - a question is asked when the question policy forbids one, or none is asked when the
          policy requires one
        - it claims a shared memory, a physical experience, or a real-life fact about the user
          that nothing licenses. Inside an active FICTION FRAME invented scene content IS
          licensed and must not be failed.

        PASS it in all of these:
        - OPTIONAL points used, partly used, or ignored. Using one is licensed, never a fault.
        - the required meaning conveyed in completely fresh wording. Paraphrase is REQUIRED of
          the writer; matching the plan phrasing is not expected and copying it is worse.
        - register that is blunt, warm, profane, sexual, dark, terse or expansive as permitted
        - saying less than you would have, or declining to elaborate
        - a permitted question

        You are NOT judging style, tone, politeness, appropriateness, subject matter, or
        whether it is a good reply. Those are licensed by the register and never your concern.

        Reply with JSON only: {"faithful": true|false, "why": "..."}
        """;

    /// <summary>
    /// The plan-obedience rules a teacher needs and the mouth will eventually internalise. Kept
    /// close to the production plan/2 renderer prompt's hard rules, because those are the ones
    /// run-1c was measured against and they are known to work.
    /// </summary>
    private const string TeacherRules =
        """


        ---
        You are writing ONE reply, as this companion, to the message below.
        The RESPONSE PLAN is a decision that has already been made. Obey it exactly:
        - SAY items: convey each one, in fresh words. Never copy their wording.
        - OPTIONAL items: use one only if it genuinely fits. Silence is correct.
        - NEVER items: do not assert, mention, hint at, or explain them.
        - BACKGROUND items: they may colour your tone; their content must not surface.
        - Ask a question ONLY if the plan's question line says so. If it says
          question_forbidden, your reply must contain NO question and NO question mark.
        - Never quote, mention or imitate the plan, its headers, or its vocabulary.
        - Never write another speaker's turn, and never label your own.
        - Match the register exactly. Intimacy, profanity and darkness are register: when the
          register invites them, write them fully and without hedging.
        Output the reply text only.
        """;

    public async Task<TargetCandidate> WriteAsync(
        ScenarioTruth scenario, global::Companion.PlanV3.PlanV3 plan, int attemptSeed,
        CancellationToken ct = default)
    {
        var user = scenario.Participants.First(p => p.Kind == ParticipantKind.User);
        var companion = scenario.Participants.First(p => p.Kind == ParticipantKind.Companion);
        var packet = RowRendering.BuildPacket(scenario, user, companion);

        var prompt = MouthPromptV4.Build(
            packet, plan,
            scenario.History.Select(t => (t.Role, t.Text)).ToList(),
            scenario.UserMessage, user.Name, companion.Name);

        // Attempt-specific seed: several valid targets for one plan, each reproducible.
        var seed = unchecked(runSeed * 131 + scenario.Seed * 17 + attemptSeed);
        var request = new RoleRequest
        {
            Role = Role.TargetWriter,
            // The mouth's system message plus teacher rules. The ROW keeps the former alone.
            System = prompt.System + TeacherRules,
            User = prompt.User,
            Seed = seed,
        };

        try
        {
            var response = await roles.WriteTargetAsync(request, ct);
            var text = response.Text.Trim();
            return text.Length == 0
                ? new TargetCandidate(null, response.Provenance, "empty-generation")
                : new TargetCandidate(text, response.Provenance);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new TargetCandidate(null, Unavailable(seed), "generator-unavailable");
        }
    }

    public async Task<IReadOnlyList<CheckResult>> CriticiseAsync(
        ScenarioTruth scenario, string target, CancellationToken ct = default)
    {
        var results = new List<CheckResult>();

        if (roles.Has(Role.FaithfulnessCritic))
            results.Add(await AskAsync(
                Role.FaithfulnessCritic, FaithfulnessSystem, "faithful",
                Describe(scenario) + "\n\nREPLY:\n" + target, ct));

        // A SECOND, independently-modelled faithfulness judge on the same evidence.
        //
        // Measured need: on the 11 negatives the deterministic gate does NOT catch, no
        // single locally-available judge distinct from the writer cleared 50% specificity
        // - the best managed 36%. Two of them ANDed reach 55% at no cost in sensitivity,
        // because their false accepts do not coincide. Acceptance requires every critic to
        // pass, so this is an AND by construction, and a disagreement routes the row to
        // manual review rather than into the corpus.
        if (roles.Has(Role.AdversarialCritic))
            results.Add(await AskAsync(
                Role.AdversarialCritic, FaithfulnessSystem, "faithful",
                Describe(scenario) + "\n\nREPLY:\n" + target, ct));

        if (roles.Has(Role.NaturalnessCritic))
            results.Add(await AskAsync(
                Role.NaturalnessCritic, NaturalnessSystem, "natural",
                "REPLY:\n" + target, ct));

        return results;
    }

    /// <summary>
    /// One role, one verdict. The staged pipeline calls this so a single judge can be
    /// loaded once and run over an entire batch; the interleaved path keeps using
    /// CriticiseAsync. Both share AskAsync, so the two schedules cannot drift in what
    /// they actually ask a critic.
    /// </summary>
    public async Task<CriticVerdict> CriticiseOneAsync(
        string role, ScenarioTruth scenario, string target, CancellationToken ct = default)
    {
        var parsed = Enum.Parse<Role>(role);
        var (system, field, user) = parsed switch
        {
            Role.NaturalnessCritic => (
                NaturalnessSystem, "natural", "REPLY:\n" + target),
            _ => (
                FaithfulnessSystem, "faithful",
                Describe(scenario) + "\n\nREPLY:\n" + target),
        };

        var check = await AskAsync(parsed, system, field, user, ct);
        return new CriticVerdict
        {
            Role = role,
            Model = Environment.GetEnvironmentVariable(EnvFor(parsed)) ?? "(unknown)",
            Passed = check.Passed,
            Code = check.Code,
            Detail = check.Detail,
            AtUtc = DateTimeOffset.UtcNow.ToString("O"),
        };
    }

    private static string EnvFor(Role role) => role switch
    {
        Role.FaithfulnessCritic => "MOUTH_FAITHFULNESS_MODEL",
        Role.NaturalnessCritic => "MOUTH_NATURALNESS_MODEL",
        Role.AdversarialCritic => "MOUTH_ADVERSARIAL_MODEL",
        Role.StyleCritic => "MOUTH_STYLE_MODEL",
        _ => "MOUTH_WRITER_MODEL",
    };

    private async Task<CheckResult> AskAsync(
        Role role, string system, string field, string user, CancellationToken ct)
    {
        try
        {
            var response = await roles.CriticiseAsync(new RoleRequest
            {
                Role = role, System = system, User = user, Seed = 0,
            }, ct);

            var passed = ParseVerdict(response.Text, field);
            return new CheckResult
            {
                Name = role.ToString(), Passed = passed,
                Code = passed ? null : $"{field}-critic",
                // The critic's prose is kept as diagnostic DETAIL on the metadata record. It is
                // never part of the row, and the export never reads this file.
                Detail = passed ? null : Truncate(response.Text),
                Kind = CheckKind.Critic,
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // An unreachable critic must not silently pass rows. It routes them to review.
            return new CheckResult
            {
                Name = role.ToString(), Passed = false, Code = "critic-unavailable",
                Kind = CheckKind.Critic,
            };
        }
    }

    private static bool ParseVerdict(string text, string field)
    {
        try
        {
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                using var doc = JsonDocument.Parse(text[start..(end + 1)]);
                if (doc.RootElement.TryGetProperty(field, out var value))
                    return value.ValueKind == JsonValueKind.True;
            }
        }
        catch (JsonException) { /* falls through to the conservative answer */ }

        // Unparseable is not approval.
        return false;
    }

    /// <summary>
    /// Everything the faithfulness critic needs to judge, and nothing it does not.
    ///
    /// The old version sent must-express and must-not-express alone, which is why it failed
    /// compliant rows: an optional item it had never heard of looked like invention, a
    /// permitted question looked like an unrequested one, and a licensed register looked like
    /// a liberty. Each section below exists because its absence produced a measured false
    /// rejection.
    ///
    /// Still excluded, deliberately: seeds, model identity, check history, hard-case tagging
    /// and anything else about how the row was made. The critic judges the reply against the
    /// plan, not the row against the factory.
    /// </summary>
    private static string Describe(ScenarioTruth scenario)
    {
        var sb = new System.Text.StringBuilder();

        void Section(string header, IEnumerable<string> lines, string empty)
        {
            var list = lines.ToList();
            sb.AppendLine(header + ":");
            if (list.Count == 0)
                sb.AppendLine("  " + empty);
            else
                foreach (var line in list)
                    sb.AppendLine("  - " + line);
        }

        Section("REQUIRED - the meaning must survive, in any wording",
            scenario.ApprovedFacts.Where(f => f.Policy == FactPolicy.MustExpress).Select(f => f.Text),
            "(nothing required)");

        Section("OPTIONAL - licensed. Using these is never a fault",
            scenario.ApprovedFacts.Where(f => f.Policy == FactPolicy.MayExpress).Select(f => f.Text),
            "(none)");

        Section("BACKGROUND - may colour tone; must not surface as a claim",
            scenario.ApprovedFacts.Where(f => f.Policy == FactPolicy.BackgroundOnly).Select(f => f.Text),
            "(none)");

        Section("FORBIDDEN - must not be stated, hinted at or explained",
            scenario.ApprovedFacts.Where(f => f.Policy == FactPolicy.MustNotExpress).Select(f => f.Text)
                .Concat(scenario.Superseded.Select(x => x.StaleText + " (superseded)")),
            "(none)");

        Section("MUST ADMIT NOT KNOWING", scenario.EpistemicUnknowns, "(none)");
        Section("KEEP AMBIGUOUS - do not resolve", scenario.IntentionalAmbiguities, "(none)");

        var q = scenario.Question.Policy.ToLowerInvariant();
        sb.AppendLine("QUESTION POLICY:");
        sb.AppendLine("  " + q switch
        {
            "must_ask" => "a question is REQUIRED: " + (scenario.Question.Text ?? ""),
            "may_ask" => "a question is PERMITTED but not required: " + (scenario.Question.Text ?? ""),
            _ => "no question may be asked",
        });

        var r = scenario.Register;
        sb.AppendLine("REGISTER PERMITTED:");
        sb.AppendLine("  warmth=" + r.Warmth + " bluntness=" + r.Bluntness
            + " playfulness=" + r.Playfulness + " teasing=" + r.Teasing
            + " intensity=" + r.Intensity + " verbosity=" + r.Verbosity
            + " profanity=" + r.Profanity);

        sb.AppendLine("FICTION FRAME:");
        sb.AppendLine("  " + (scenario.Frame is { } f
            ? "ACTIVE (" + f.Transition + "). Invented scene content is LICENSED; only a "
              + "crossing into a real-world claim about the user is a fault."
            : "none - this is a real conversation"));

        return sb.ToString();
    }

    private static string Truncate(string text) => text.Length <= 300 ? text : text[..300];

    private static GenerationProvenance Unavailable(long seed) => new()
    {
        Role = Role.TargetWriter.ToString(), Model = "(unavailable)", Endpoint = "(unavailable)",
        PromptVersion = "-", Seed = seed, Attempt = 1, PromptHash = "-",
    };
}
