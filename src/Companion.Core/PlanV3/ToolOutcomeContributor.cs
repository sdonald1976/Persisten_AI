using Companion.Core.Domain;
using Companion.Core.Services;

namespace Companion.PlanV3;

/// <summary>
/// Source 2: builds native contributions from the TYPED tool execution outcomes captured
/// before prose conversion. It never reads <c>ResultsSection</c>, rendered JSON, or any
/// prompt text — its only input is <see cref="ToolExecutionOutcome"/>.
///
/// Authority separation, enforced here and again by the assembler's grants:
///  - the tool supplies a value; it never decides that Ava must speak it;
///  - a result that was refused, never executed, or carries a secret contributes NOTHING
///    (not even background — there is no lawful reading of it this turn);
///  - a failure never becomes a success claim: the item carries the safe summary only,
///    with no provider text, exception, or stack trace;
///  - planner promotion cannot override authorization, disclosure, or retention — those
///    are checked BEFORE the disposition is honored;
///  - tool text is quoted DATA, never protocol instruction.
/// </summary>
public sealed class ToolOutcomeContributor(
    IReadOnlyList<ToolExecutionOutcome> outcomes) : IPlanV3Contributor
{
    public string SourceId => "tool";

    /// <summary>Content-safe per-call decisions for diagnostics: ids and reasons, no text.</summary>
    public List<(string ToolCallId, string Decision, string? Reason)> Decisions { get; } = [];

    public PlanContributionResult Contribute(PlanContributionContext context)
    {
        var items = new List<ProposedItem>();

        foreach (var o in outcomes)
        {
            // ---- gates that precede any disposition ----
            if (!o.Requested)
            {
                Decisions.Add((o.ToolCallId, "skipped", "not-requested"));
                continue;
            }
            if (!o.Authorized)
            {
                Decisions.Add((o.ToolCallId, "withheld", o.RefusalReason ?? "unauthorized"));
                continue;
            }
            if (o.ContainsSecret || CarriesSecret(o))
            {
                Decisions.Add((o.ToolCallId, "withheld", "secret-detected"));
                continue;
            }
            if (o.PlannerDisposition == ToolPlannerDisposition.Withheld)
            {
                Decisions.Add((o.ToolCallId, "withheld", "planner-disposition-withheld"));
                continue;
            }
            if (!o.Executed || o.Status == ToolExecutionStatus.NotExecuted)
            {
                Decisions.Add((o.ToolCallId, "skipped", "not-executed"));
                continue;
            }
            if (o.Status == ToolExecutionStatus.Cancelled)
            {
                // Cancelled says nothing about success OR failure — background context only.
                items.Add(Background(o, $"The {o.Tool} lookup was cancelled before it finished."));
                Decisions.Add((o.ToolCallId, "background", "cancelled"));
                continue;
            }

            var failed = o.Status is ToolExecutionStatus.Failed or ToolExecutionStatus.TimedOut;
            if (failed)
            {
                // A failure may be acknowledged when the request depended on it, but only
                // through the SAFE summary, and never as a claim that anything succeeded.
                var summary = o.SafeFailureSummary ?? $"The {o.Tool} lookup did not succeed.";
                var wanted = o.PlannerDisposition is ToolPlannerDisposition.MustExpress
                    or ToolPlannerDisposition.MayExpress;
                items.Add(new ProposedItem
                {
                    LocalId = o.ToolCallId,
                    Type = "tool-failure",
                    Category = wanted ? RenderCategory.claim : RenderCategory.observation,
                    // Never must_express: a failure acknowledgment is offered, not compelled,
                    // and the grant table has no (claim, must_express) path without promotion.
                    ProposedPolicy = wanted ? ExpressionPolicy.may_express : ExpressionPolicy.background_only,
                    PlanningPromotion = wanted,
                    // The reason code is what SCOPES the may_express grant to failures: a
                    // success carries none, so it can never travel this tuple.
                    ReasonCode = wanted
                        ? $"tool-failure.{o.Status.ToString().ToLowerInvariant()}"
                        : null,
                    Text = summary,
                    Quoted = false,
                    Provenance = new Provenance(Origin: "tool"),
                    Retention = Retention(o),
                    Value = Attribution(o),
                });
                Decisions.Add((o.ToolCallId, wanted ? "failure-acknowledged" : "background",
                    o.Status.ToString().ToLowerInvariant()));
                continue;
            }

            // ---- success: disclosure and retention gate expression ----
            if (!o.DisclosurePermitted)
            {
                items.Add(Background(o, $"A {o.Tool} result is available but not disclosable."));
                Decisions.Add((o.ToolCallId, "background", "disclosure-not-permitted"));
                continue;
            }

            var expressible = o.PlannerDisposition is ToolPlannerDisposition.MustExpress
                or ToolPlannerDisposition.MayExpress;
            var text = Describe(o);
            items.Add(new ProposedItem
            {
                LocalId = o.ToolCallId,
                Type = "tool-result",
                Category = expressible ? RenderCategory.claim : RenderCategory.observation,
                ProposedPolicy = o.PlannerDisposition switch
                {
                    ToolPlannerDisposition.MustExpress => ExpressionPolicy.must_express,
                    ToolPlannerDisposition.MayExpress => ExpressionPolicy.may_express,
                    _ => ExpressionPolicy.background_only,
                },
                PlanningPromotion = expressible,
                Text = text,
                // Tool output is quoted DATA. Quoting exempts it from the coaching lint
                // (it is a fact about what a tool returned) while the grant table keeps it
                // from ever becoming authority.
                Quoted = true,
                Provenance = new Provenance(Origin: "tool"),
                Disclosure = o.AuthorizedAudience.Count > 0 ? Disclosure.restricted : null,
                Audience = o.AuthorizedAudience.Count > 0 ? o.AuthorizedAudience : null,
                Retention = Retention(o),
                Value = Attribution(o),
            });
            Decisions.Add((o.ToolCallId,
                expressible ? "expressible" : "background",
                o.PlannerDisposition.ToString().ToLowerInvariant()));
        }

        return new PlanContributionResult(items);
    }

    private static ProposedItem Background(ToolExecutionOutcome o, string text) => new()
    {
        LocalId = o.ToolCallId,
        Type = "tool-result",
        Category = RenderCategory.observation,
        ProposedPolicy = ExpressionPolicy.background_only,
        Text = text,
        Provenance = new Provenance(Origin: "tool"),
        Retention = Retention(o),
        Value = Attribution(o),
    };

    /// <summary>Independent attribution: every contribution names its call and tool.</summary>
    private static System.Text.Json.Nodes.JsonNode? Attribution(ToolExecutionOutcome o)
        => System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(new
        {
            toolCallId = o.ToolCallId,
            tool = o.Tool,
            toolVersion = o.ToolVersion,
            status = o.Status.ToString(),
            activity = o.RelatedActivityInstanceId,
            project = o.RelatedProjectId,
        }));

    private static Companion.PlanV3.Retention Retention(ToolExecutionOutcome o) => o.Retention switch
    {
        "full" => Companion.PlanV3.Retention.full,
        "no_telemetry_text" => Companion.PlanV3.Retention.no_telemetry_text,
        "volatile_turn_only" => Companion.PlanV3.Retention.volatile_turn_only,
        _ => Companion.PlanV3.Retention.no_training,
    };

    /// <summary>
    /// Defence in depth: the tool layer should have flagged it, and we check again — twice.
    /// The value scan catches known credential SHAPES; the structural scan catches a
    /// credential-shaped FIELD NAME whatever its value looks like, because a tool returning
    /// <c>{"token": "hunter2"}</c> is handing over a credential the shape rules would pass.
    /// </summary>
    internal static bool CarriesSecret(ToolExecutionOutcome o)
    {
        if (o.StructuredResult is null)
            return false;
        var json = System.Text.Json.JsonSerializer.Serialize(o.StructuredResult);
        if (SecretDetector.LooksLikeSecret(json))
            return true;
        try
        {
            return HasCredentialField(System.Text.Json.Nodes.JsonNode.Parse(json));
        }
        catch (System.Text.Json.JsonException)
        {
            // Unparseable payload: the value scan above already had its say.
            return false;
        }
    }

    private static readonly System.Text.RegularExpressions.Regex CredentialField =
        new(@"^(?:password|passwd|pwd|secret|token|api[_-]?key|apikey|access[_-]?token|"
            + @"refresh[_-]?token|client[_-]?secret|private[_-]?key|credential|auth)s?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool HasCredentialField(System.Text.Json.Nodes.JsonNode? node) => node switch
    {
        System.Text.Json.Nodes.JsonObject obj => obj.Any(kv =>
            (CredentialField.IsMatch(kv.Key) && kv.Value is not null
                                             && kv.Value.ToJsonString() is not ("null" or "\"\""))
            || HasCredentialField(kv.Value)),
        System.Text.Json.Nodes.JsonArray arr => arr.Any(HasCredentialField),
        _ => false,
    };

    /// <summary>
    /// A bounded, factual description of the structured result. Deliberately not the raw
    /// payload: the plan carries what was found, not a dump.
    /// </summary>
    private static string Describe(ToolExecutionOutcome o)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(o.StructuredResult);
        if (json.Length > 300)
            json = json[..300];
        return $"The {o.Tool} lookup returned: {json}";
    }
}

/// <summary>
/// The tool-authorization subsystem's own contributor: it alone may record, under its
/// scoped family, that unauthorized material must not be spoken.
/// </summary>
public sealed class ToolAuthorizationContributor(
    IReadOnlyList<ToolExecutionOutcome> outcomes) : IPlanV3Contributor
{
    public string SourceId => "tool-authorization";

    public PlanContributionResult Contribute(PlanContributionContext context)
        => new(outcomes
            .Where(o => o.Requested && (!o.Authorized || Secret(o)))
            .Select(o => new ProposedItem
            {
                LocalId = $"withhold-{o.ToolCallId}",
                Type = "tool-withholding",
                Category = RenderCategory.note,
                ProposedPolicy = ExpressionPolicy.must_not_express,
                ReasonCode = Secret(o)
                    ? "tool-authorization.secret-bearing-result"
                    : "tool-authorization.result-unauthorized",
                // The NOTE names the tool, never the withheld content.
                Text = $"A {o.Tool} result was withheld.",
                Provenance = new Provenance(Origin: "derived"),
                Retention = Companion.PlanV3.Retention.no_training,
            })
            .ToList());

    /// <summary>
    /// The SAME detection the result contributor applies. Without this the authorization
    /// subsystem would record a withholding note only for results the tool layer had
    /// already flagged — and a credential the layer missed would be withheld silently,
    /// which is the one outcome that must never go unrecorded.
    /// </summary>
    private static bool Secret(ToolExecutionOutcome o)
        => o.ContainsSecret || ToolOutcomeContributor.CarriesSecret(o);
}
