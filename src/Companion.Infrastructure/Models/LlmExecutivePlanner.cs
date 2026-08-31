using System.Text;
using System.Text.Json;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.PlanV3;
using Microsoft.Extensions.Logging;

namespace Companion.Infrastructure.Models;

/// <summary>
/// The executive planner backed by a local instruction model (the ExecutivePlanner role).
///
/// The model is shown the plan's CHOICES - the optional items and whether an optional question
/// is available - and asked for a JSON verdict. It is never shown a seat where words for the
/// user could come out, and its output is applied as a typed transform:
///
///  - <c>include</c>: which may_express item ids to keep. Ids not in the plan are an error;
///    items with any other policy cannot be touched from here by construction.
///  - <c>order</c>: a permutation of the KEPT may_express ids; must/admit ordering is fixed.
///  - <c>ask</c>: honored only when the plan says may_ask. false hardens the plan to
///    question_forbidden; true leaves may_ask standing. ask_required and question_forbidden
///    are not the model's to move.
///
/// The transformed plan then re-earns its place: structural validation, audience validation
/// for the current recipient, and render eligibility - the same three gates the spec demands
/// before any plan reaches the mouth. Any failure at any step returns the deterministic plan
/// unchanged, with the reason on the decision record. This planner can therefore make a turn
/// leaner or quieter; it cannot make one wrong.
/// </summary>
public sealed class LlmExecutivePlanner(
    IChatModel chat, ILogger<LlmExecutivePlanner> logger) : IExecutivePlanner
{
    public bool IsEnabled => true;

    private const string System =
        "You are a conversation planner. You never write the reply itself. "
        + "Given what must be said, what is not known, and a list of OPTIONAL items, decide "
        + "which optional items genuinely help answer the user's message, and whether an "
        + "optional question is worth asking. Prefer fewer items: an optional item earns its "
        + "place only if it is directly relevant. Respond with ONLY a JSON object: "
        + "{\"include\":[\"id\",...],\"order\":[\"id\",...],\"ask\":true|false}. "
        + "\"include\" lists the optional item ids to keep (subset of the offered ids), "
        + "\"order\" is those same ids in speaking order, \"ask\" is whether to use the "
        + "optional question. No other keys, no commentary.";

    public async Task<ExecutivePlanOutcome> RefineAsync(
        global::Companion.PlanV3.PlanV3 deterministicPlan, string userMessage, CancellationToken ct = default)
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

        // Nothing to decide: no optional content and no optional question means the plan has
        // exactly one legal rendering set. Skipping the call is not an optimisation, it is the
        // statement that a planner with no choices has no job.
        if (optional.Count == 0 && !mayAsk)
            return new ExecutivePlanOutcome(deterministicPlan,
                Decision("deterministic", "plan carries no optional choices"));

        try
        {
            var completion = await chat.CompleteAsync(
                System, DescribeChoices(deterministicPlan, optional, mayAsk, userMessage),
                ResponseFormat.Json, ct: ct);

            var proposal = Parse(completion.Text);
            if (proposal is null)
                return new ExecutivePlanOutcome(deterministicPlan,
                    Decision("deterministic", "planner output was not the expected JSON"));

            var (refined, error) = Apply(deterministicPlan, optional, mayAsk, proposal.Value);
            if (refined is null)
                return new ExecutivePlanOutcome(deterministicPlan,
                    Decision("deterministic", $"proposal rejected: {error}"));

            // The three gates, on the plan that would actually be rendered. The deterministic
            // plan already passed its own build-time lint; the refined one re-earns it here.
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

            var dropped = optional.Count - refined.Items.Count(i => i.Policy == ExpressionPolicy.may_express);
            return new ExecutivePlanOutcome(refined,
                Decision("refined", $"optional kept {optional.Count - dropped}/{optional.Count}"
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

    private static string DescribeChoices(
        global::Companion.PlanV3.PlanV3 plan, List<PlanItem> optional, bool mayAsk, string userMessage)
    {
        var sb = new StringBuilder();
        sb.Append("User's message: ").Append(userMessage).Append('\n');
        sb.Append("Act: ").Append(plan.Act).Append('\n');
        sb.Append("Will be said regardless (context only):\n");
        foreach (var i in plan.Items.Where(i => i.Policy == ExpressionPolicy.must_express))
            sb.Append("  - ").Append(i.Text).Append('\n');
        foreach (var i in plan.Items.Where(i => i.Policy == ExpressionPolicy.admit_unknown))
            sb.Append("  - (will admit not knowing) ").Append(i.Text).Append('\n');
        sb.Append("Optional items (choose which to include):\n");
        foreach (var i in optional)
            sb.Append("  ").Append(i.Id).Append(": ").Append(i.Text).Append('\n');
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

    private readonly record struct Proposal(List<string> Include, List<string> Order, bool Ask);

    private static Proposal? Parse(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(Strip(text));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            List<string> Ids(string name)
                => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
                    ? [.. v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)]
                    : [];
            var ask = root.TryGetProperty("ask", out var a) && a.ValueKind == JsonValueKind.True;
            return new Proposal(Ids("include"), Ids("order"), ask);
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

    private static (global::Companion.PlanV3.PlanV3? Plan, string? Error) Apply(
        global::Companion.PlanV3.PlanV3 plan, List<PlanItem> optional, bool mayAsk, Proposal proposal)
    {
        var offered = optional.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);

        // Every id the model names must be one it was offered. Anything else is the model
        // trying to reach an item it has no authority over, and the whole proposal dies.
        foreach (var id in proposal.Include.Concat(proposal.Order))
        {
            if (!offered.Contains(id))
                return (null, $"id '{id}' is not an offered optional item");
        }

        var keep = proposal.Include.ToHashSet(StringComparer.Ordinal);
        var orderRank = proposal.Order
            .Where(keep.Contains)
            .Select((id, rank) => (id, rank))
            .ToDictionary(x => x.id, x => x.rank, StringComparer.Ordinal);

        // Kept optional items move to the proposed order; everything that is not an optional
        // item keeps its position and its everything-else. Dropped optional items vanish -
        // declining an offer is the one power this seat legitimately has.
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

        return (plan with { Items = kept, Question = question }, null);
    }
}
