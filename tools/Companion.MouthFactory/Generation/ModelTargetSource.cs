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
    /// Critic instructions say nothing about subject matter. They ask about faithfulness to the
    /// plan and about whether the line reads like a person — never whether it is appropriate,
    /// tasteful or safe, because those questions are how a corpus loses its A6 and A7 strata.
    /// </summary>
    private const string NaturalnessSystem =
        "You judge whether a line of dialogue sounds like a real person speaking, or like an AI "
        + "assistant. Judge ONLY rhythm, word choice and naturalness. Subject matter is NEVER a "
        + "reason to fail a line: intimacy, profanity, darkness and violence in fiction are "
        + "register, not defects. Reply with JSON: {\"natural\": true|false, \"why\": \"...\"}.";

    private const string FaithfulnessSystem =
        "You judge whether a reply expresses the meaning it was told to express, and nothing "
        + "beyond it. Judge ONLY meaning. Subject matter is NEVER a reason to fail a reply. "
        + "Reply with JSON: {\"faithful\": true|false, \"why\": \"...\"}.";

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
        ScenarioTruth scenario, global::Companion.PlanV3.PlanV3 plan, int variant,
        CancellationToken ct = default)
    {
        var user = scenario.Participants.First(p => p.Kind == ParticipantKind.User);
        var companion = scenario.Participants.First(p => p.Kind == ParticipantKind.Companion);
        var packet = RowRendering.BuildPacket(scenario, user, companion);

        var prompt = MouthPromptV4.Build(
            packet, plan,
            scenario.History.Select(t => (t.Role, t.Text)).ToList(),
            scenario.UserMessage, user.Name, companion.Name);

        // Variant-specific seed: several valid targets for one plan, each reproducible.
        var seed = unchecked(runSeed * 131 + scenario.Seed * 17 + variant);
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

        if (roles.Has(Role.NaturalnessCritic))
            results.Add(await AskAsync(
                Role.NaturalnessCritic, NaturalnessSystem, "natural",
                "REPLY:\n" + target, ct));

        return results;
    }

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
    /// What the faithfulness critic is told the reply had to convey. Structure only — the
    /// scenario's hidden state never becomes part of any training row.
    /// </summary>
    private static string Describe(ScenarioTruth scenario)
    {
        var must = scenario.ApprovedFacts.Where(f => f.Policy == FactPolicy.MustExpress).Select(f => f.Text);
        var never = scenario.ApprovedFacts.Where(f => f.Policy == FactPolicy.MustNotExpress).Select(f => f.Text);
        return "MUST CONVEY:\n" + string.Join("\n", must.DefaultIfEmpty("(nothing specific)"))
               + "\nMUST NOT STATE:\n" + string.Join("\n", never.DefaultIfEmpty("(nothing)"));
    }

    private static string Truncate(string text) => text.Length <= 300 ? text : text[..300];

    private static GenerationProvenance Unavailable(long seed) => new()
    {
        Role = Role.TargetWriter.ToString(), Model = "(unavailable)", Endpoint = "(unavailable)",
        PromptVersion = "-", Seed = seed, Attempt = 1, PromptHash = "-",
    };
}
