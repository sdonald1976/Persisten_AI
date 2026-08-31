using System.Text;
using System.Text.Json;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.PlanV3;
using Companion.Infrastructure.Renderer;
using Microsoft.Extensions.Logging;

namespace Companion.Infrastructure.Models;

/// <summary>
/// The executive planner backed by a local instruction model (the ExecutivePlanner role).
///
/// Two powers, both bounded, both applied as typed transforms:
///
/// 1. SELECTION - which may_express items to keep and in what order, and whether to use an
///    optional question. ask_required and question_forbidden are not the model's to move.
///
/// 2. PROPOSAL - new plan items, as typed content with provenance, never final prose:
///      { "kind": "grounded",  "text": ..., "basedOn": ["item-or-memory-id"] }
///      { "kind": "inference", "text": ... }            - marked interpretation
///      { "kind": "creative",  "text": ... }            - only when the turn invited fiction
///      { "kind": "admit",     "text": "whether ..." }  - names something not known
///
/// The AUTHORITY LAYER here (deterministic code) admits or refuses each proposal:
///  - grounded requires every basedOn to name an offered memory or an existing plan item -
///    the evidence identity rides into the item's provenance;
///  - inference enters marked as inference, and is refused when it reads as an
///    autobiographical experience claim (the same InventedExperience gate the reply checks
///    use, applied earlier);
///  - creative is refused outright unless the typed signals say fiction was invited;
///  - admit becomes an admit_unknown item - uncertainty is spoken, not smoothed over;
///  - every admitted proposal enters as may_express (or admit_unknown); obligations,
///    suppressions, privacy and tool authority are unreachable from this seat;
///  - a proposal sharing content with any must_not_express item is refused;
///  - the refined plan re-passes structural, audience and render-eligibility validation,
///    or the deterministic plan stands.
///
/// The pending conversational move rides in as typed state: when the user just accepted an
/// invitation, the planner is told so and told that re-issuing it is not an option - and the
/// caller separately suppresses any question item whose move identity was already satisfied,
/// so even a planner that ignores the instruction cannot repeat the move.
/// </summary>
public sealed class LlmExecutivePlanner(
    IChatModel chat, ILogger<LlmExecutivePlanner> logger) : IExecutivePlanner
{
    public bool IsEnabled => true;

    private const int MaxProposals = 3;
    private const int MaxProposalChars = 240;

    private const string SystemPrompt =
        "You are a conversation planner. You never write the reply itself; you decide WHAT the "
        + "reply should convey, as typed items. Respond with ONLY a JSON object:\n"
        + "{\"include\":[\"id\",...],\"order\":[\"id\",...],\"ask\":true|false,"
        + "\"propose\":[{\"kind\":\"grounded|inference|creative|admit\",\"text\":\"...\","
        + "\"basedOn\":[\"id\",...]}]}\n"
        + "Rules: \"include\" lists optional item ids worth keeping (prefer fewer). \"propose\" "
        + "adds NEW points the reply should make (at most 3, each one plain sentence): "
        + "kind=grounded needs basedOn citing the memory/item ids it stands on; kind=inference "
        + "is your own clearly-hedged reading of the situation; kind=admit names something not "
        + "known (\"whether ...\", \"what ...\") when honesty requires saying so; kind=creative "
        + "is fiction and is accepted only when the turn invites it. NEVER invent facts, "
        + "experiences, or history. If the user just accepted an offer or asked for specifics, "
        + "propose items that DELIVER - advancing content, or an honest admission that there is "
        + "nothing concrete to deliver - never a repeat of the offer. No other keys, no "
        + "commentary.";

    public async Task<ExecutivePlanOutcome> RefineAsync(
        global::Companion.PlanV3.PlanV3 deterministicPlan, PlanningSignals signals,
        CancellationToken ct = default)
    {
        DecisionRecord Decision(string verdict, string? reason) => new()
        {
            Stage = "plan.executive",
            Decider = verdict.StartsWith("refined", StringComparison.Ordinal) ? "model" : "rule",
            Verdict = verdict,
            Reason = reason,
        };

        var optional = deterministicPlan.Items
            .Where(i => i.Policy == ExpressionPolicy.may_express && !string.IsNullOrWhiteSpace(i.Text))
            .ToList();
        var mayAsk = deterministicPlan.Question.Policy == QuestionPolicy.may_ask;

        try
        {
            var completion = await chat.CompleteAsync(
                SystemPrompt, Describe(deterministicPlan, optional, mayAsk, signals),
                ResponseFormat.Json, ct: ct);

            var proposal = Parse(completion.Text);
            if (proposal is null)
                return new ExecutivePlanOutcome(deterministicPlan,
                    Decision("deterministic", "planner output was not the expected JSON"));

            var (refined, admitted, refused, error) =
                Apply(deterministicPlan, optional, mayAsk, signals, proposal.Value);
            if (refined is null)
                return new ExecutivePlanOutcome(deterministicPlan,
                    Decision("deterministic", $"proposal rejected: {error}"));

            var structural = PlanV3Codec.Validate(refined);
            if (structural.Count > 0)
                return new ExecutivePlanOutcome(deterministicPlan,
                    Decision("deterministic", $"structural: {structural[0]}"));

            var user = refined.Participants.FirstOrDefault(p => p.Role == ParticipantRole.user);
            var audience = PlanV3Codec.ValidateForAudience(
                refined, user is null ? [] : [user.Id],
                new RendererTrustContext(RendererTransport.local_loopback));
            if (!audience.Ok)
                return new ExecutivePlanOutcome(deterministicPlan,
                    Decision("deterministic", $"audience: {audience.Errors.FirstOrDefault()}"));

            var eligibility = PlanV3Codec.CheckRenderEligibility(refined);
            if (!eligibility.Eligible)
                return new ExecutivePlanOutcome(deterministicPlan,
                    Decision("deterministic", "refined plan not render-eligible"));

            var kept = refined.Items.Count(i =>
                i.Policy == ExpressionPolicy.may_express && optional.Any(o => o.Id == i.Id));
            return new ExecutivePlanOutcome(refined,
                Decision("refined",
                    $"optional kept {kept}/{optional.Count}"
                    + $", proposals admitted {admitted}, refused {refused}"
                    + (mayAsk ? $", ask={proposal.Value.Ask}" : "")));
        }
        catch (Exception ex)
        {
            // A planner failure is a quality loss, never a turn loss - and never a reason to
            // reach for any other model.
            logger.LogWarning(ex, "Executive planner failed; deterministic plan used.");
            return new ExecutivePlanOutcome(deterministicPlan,
                Decision("deterministic", $"planner error: {ex.GetType().Name}"));
        }
    }

    // ---- the model's view of the turn ------------------------------------------------------

    private static string Describe(
        global::Companion.PlanV3.PlanV3 plan, List<PlanItem> optional, bool mayAsk,
        PlanningSignals s)
    {
        var sb = new StringBuilder();
        sb.Append("User's message: ").Append(s.UserMessage).Append('\n');

        if (s.Recent.Count > 0)
        {
            sb.Append("Recent conversation:\n");
            foreach (var (role, text) in s.Recent.TakeLast(6))
                sb.Append("  [").Append(role).Append("] ").Append(Clip(text, 160)).Append('\n');
        }

        if (s.Pending is { } pending)
        {
            sb.Append("PENDING MOVE from the previous turn: ")
              .Append(pending.Kind).Append(" - \"").Append(Clip(pending.Text, 120)).Append("\"\n");
            sb.Append(s.PendingResolution switch
            {
                MoveResolution.Accepted =>
                    "The user ACCEPTED it. Deliver on it now - advancing content, or an honest "
                    + "admission that there is nothing concrete behind it. Re-issuing the same "
                    + "offer or question is not an option.\n",
                MoveResolution.Answered =>
                    "The user ANSWERED it. Respond to their answer; do not ask it again.\n",
                MoveResolution.Rejected =>
                    "The user DECLINED it. Let it go; do not re-offer.\n",
                MoveResolution.Redirected =>
                    "The user moved past it. Follow them; do not drag it back.\n",
                _ => "The user has not engaged it yet.\n",
            });
        }

        sb.Append("Act: ").Append(plan.Act).Append('\n');
        sb.Append("Will be said regardless (context only):\n");
        foreach (var i in plan.Items.Where(i => i.Policy == ExpressionPolicy.must_express))
            sb.Append("  - ").Append(i.Text).Append('\n');
        foreach (var i in plan.Items.Where(i => i.Policy == ExpressionPolicy.admit_unknown))
            sb.Append("  - (will admit not knowing) ").Append(i.Text).Append('\n');

        sb.Append("Optional items already available (choose which to include):\n");
        foreach (var i in optional)
            sb.Append("  ").Append(i.Id).Append(": ").Append(i.Text).Append('\n');

        if (s.Memories.Count > 0)
        {
            sb.Append("Retrieved memories (cite by id in basedOn for grounded proposals):\n");
            foreach (var (id, text) in s.Memories.Take(8))
                sb.Append("  mem:").Append(id.ToString("N")[..8]).Append(": ")
                  .Append(Clip(text, 140)).Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(s.ToolResults))
            sb.Append("Tool results this turn:\n").Append(Clip(s.ToolResults!, 500)).Append('\n');

        sb.Append(s.CreativeInvited
            ? "Fiction/roleplay is invited this turn: creative proposals are acceptable.\n"
            : "Fiction is NOT invited: creative proposals will be refused.\n");

        if (mayAsk)
        {
            var q = plan.Items.FirstOrDefault(i => i.Id == plan.Question.ItemId)?.Text;
            sb.Append("Optional question available: ").Append(q ?? "(unspecified)").Append('\n');
        }
        else
        {
            sb.Append("No question may be asked this turn; \"ask\" must be false.\n");
        }

        return sb.ToString();
    }

    // ---- parsing ---------------------------------------------------------------------------

    private readonly record struct ProposedItem(string Kind, string Text, List<string> BasedOn);

    private readonly record struct Proposal(
        List<string> Include, List<string> Order, bool Ask, List<ProposedItem> Propose);

    private static Proposal? Parse(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(Strip(text));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            static List<string> Ids(JsonElement el, string name)
                => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
                    ? [.. v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                          .Select(e => e.GetString()!)]
                    : [];

            var proposals = new List<ProposedItem>();
            if (root.TryGetProperty("propose", out var pr) && pr.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in pr.EnumerateArray())
                {
                    if (p.ValueKind != JsonValueKind.Object)
                        continue;
                    var kind = p.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String
                        ? k.GetString()! : "";
                    var body = p.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                        ? t.GetString()! : "";
                    proposals.Add(new ProposedItem(kind, body, Ids(p, "basedOn")));
                }
            }

            var ask = root.TryGetProperty("ask", out var a) && a.ValueKind == JsonValueKind.True;
            return new Proposal(Ids(root, "include"), Ids(root, "order"), ask, proposals);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Local models fence JSON in markdown often enough that stripping is table stakes.</summary>
    private static string Strip(string text)
    {
        var t = text.Trim();
        if (t.StartsWith("```", StringComparison.Ordinal))
        {
            var open = t.IndexOf('\n');
            var close = t.LastIndexOf("```", StringComparison.Ordinal);
            if (open >= 0 && close > open)
                t = t[(open + 1)..close].Trim();
        }
        return t;
    }

    // ---- the authority layer ---------------------------------------------------------------

    private static (global::Companion.PlanV3.PlanV3? Plan, int Admitted, int Refused, string? Error) Apply(
        global::Companion.PlanV3.PlanV3 plan, List<PlanItem> optional, bool mayAsk,
        PlanningSignals signals, Proposal proposal)
    {
        var offered = optional.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);

        // Every id named in selection must be one the model was offered. Anything else is the
        // model reaching for an item it has no authority over, and the whole proposal dies -
        // partial acceptance of a hostile proposal is still acceptance.
        foreach (var id in proposal.Include.Concat(proposal.Order))
        {
            if (!offered.Contains(id))
                return (null, 0, 0, $"id '{id}' is not an offered optional item");
        }

        var keep = proposal.Include.ToHashSet(StringComparer.Ordinal);
        var orderRank = proposal.Order
            .Where(keep.Contains)
            .Select((id, rank) => (id, rank))
            .ToDictionary(x => x.id, x => x.rank, StringComparer.Ordinal);

        var kept = new List<PlanItem>();
        foreach (var item in plan.Items)
        {
            if (item.Policy != ExpressionPolicy.may_express || !offered.Contains(item.Id))
                kept.Add(item);
            else if (keep.Contains(item.Id))
                kept.Add(item);
        }
        kept = [.. kept.OrderBy(i =>
            i.Policy == ExpressionPolicy.may_express && orderRank.TryGetValue(i.Id, out var r)
                ? r : -1)];

        // ---- proposal admission. Each proposal is judged alone; a refused proposal is
        // dropped and counted, never fatal - refusing to fabricate must not cost the turn.
        var admitted = 0;
        var refused = 0;
        var citable = BuildCitables(plan, signals);
        var suppressedTokens = plan.Items
            .Where(i => i.Policy == ExpressionPolicy.must_not_express && i.Text is not null)
            .SelectMany(i => DistinctiveTokens(i.Text!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var next = 0;

        foreach (var p in proposal.Propose.Take(MaxProposals))
        {
            var text = (p.Text ?? "").Trim();
            if (text.Length is 0 or > MaxProposalChars)
            {
                refused++;
                continue;
            }

            // A proposal that touches suppressed content is refused regardless of kind: the
            // NEVER section is not reachable from the planning seat, in either direction.
            if (DistinctiveTokens(text).Count(suppressedTokens.Contains) >= 2)
            {
                refused++;
                continue;
            }

            switch (p.Kind)
            {
                case "grounded":
                    // Evidence identity is the admission ticket, and it survives on the item.
                    if (p.BasedOn.Count == 0 || !p.BasedOn.All(citable.Contains))
                    {
                        refused++;
                        continue;
                    }
                    kept.Add(new PlanItem
                    {
                        Id = $"xp{next++}",
                        Type = "knowledge",
                        Policy = ExpressionPolicy.may_express,
                        Text = text,
                        Source = "retrieval",
                        Provenance = new Provenance(
                            Origin: "executive-grounded",
                            EvidenceRef: string.Join(",", p.BasedOn)),
                    });
                    admitted++;
                    break;

                case "inference":
                    // Marked as her reading of the situation - and never an experience claim.
                    // "I once tried..." dressed as inference is fabricated autobiography, and
                    // the same gate the reply checks use refuses it here, earlier.
                    if (RendererShadowChecks.LooksLikeInventedExperience(text))
                    {
                        refused++;
                        continue;
                    }
                    kept.Add(new PlanItem
                    {
                        Id = $"xp{next++}",
                        Type = "interpretation",
                        Policy = ExpressionPolicy.may_express,
                        Text = text,
                        Source = "interpretation",
                        Provenance = new Provenance(Origin: "executive-inference"),
                    });
                    admitted++;
                    break;

                case "creative":
                    if (!signals.CreativeInvited)
                    {
                        refused++;
                        continue;
                    }
                    kept.Add(new PlanItem
                    {
                        Id = $"xp{next++}",
                        Type = "creative",
                        Policy = ExpressionPolicy.may_express,
                        Text = text,
                        Source = "interpretation",
                        Provenance = new Provenance(Origin: "executive-creative"),
                    });
                    admitted++;
                    break;

                case "admit":
                    kept.Add(new PlanItem
                    {
                        Id = $"xp{next++}",
                        Type = "knowledge-boundary",
                        Policy = ExpressionPolicy.admit_unknown,
                        Text = text.TrimEnd('.', '?'),
                        Source = "interpretation",
                        Provenance = new Provenance(Origin: "executive-admit"),
                    });
                    admitted++;
                    break;

                default:
                    refused++;
                    break;
            }
        }

        var question = plan.Question;
        if (mayAsk)
        {
            if (proposal.Ask)
            {
                // The suggestion item must survive whenever the question stays available -
                // may_ask with an itemId pointing at a dropped item is structurally invalid,
                // and the model excluding it while asking for the question is a contradiction
                // resolved in favour of the question.
                if (plan.Question.ItemId is { } sid
                    && kept.All(i => i.Id != sid)
                    && plan.Items.FirstOrDefault(i => i.Id == sid) is { } suggestion)
                    kept.Add(suggestion);
            }
            else
            {
                // Declining the optional question hardens the plan to question_forbidden, and
                // the suggestion item goes with it: a question-shaped may_express item under a
                // forbidden question policy invites the mouth to violate the policy.
                question = new QuestionPolicyBlock(QuestionPolicy.question_forbidden);
                if (plan.Question.ItemId is { } sid)
                    kept.RemoveAll(i => i.Id == sid);
            }
        }

        return (plan with { Items = kept, Question = question }, admitted, refused, null);
    }

    /// <summary>Everything a grounded proposal may cite: plan item ids and offered memory ids.</summary>
    private static HashSet<string> BuildCitables(
        global::Companion.PlanV3.PlanV3 plan, PlanningSignals signals)
    {
        var citable = new HashSet<string>(StringComparer.Ordinal);
        foreach (var i in plan.Items)
            citable.Add(i.Id);
        foreach (var (id, _) in signals.Memories)
            citable.Add("mem:" + id.ToString("N")[..8]);
        return citable;
    }

    private static List<string> DistinctiveTokens(string text)
        => [.. System.Text.RegularExpressions.Regex.Matches(text, @"[\w][\w'-]*")
            .Select(m => m.Value)
            .Where(w => w.Length >= 5 || w.Any(char.IsDigit))
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    private static string Clip(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";
}
