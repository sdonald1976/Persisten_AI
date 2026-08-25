using Companion.Core.Domain;
using Companion.Core.Services;

namespace Companion.PlanV3;

/// <summary>
/// Source 3: the user's explicit standing preferences reach the native plan. Register
/// preferences become register VOTES under <c>user-preference.&lt;dimension&gt;</c>;
/// expression restrictions become <c>must_not_express</c> NOTE items — two mechanisms,
/// because they are two different kinds of authority (a register setting shapes speech,
/// a restriction forbids a subject).
///
/// Every vote and every item cites the preference record's stable id as its evidence
/// reference. NO preference text enters a vote, a decision, or telemetry — only the note
/// item's text names its subject, because the renderer must know what not to raise.
///
/// Within-user precedence was already resolved by the pure
/// <see cref="UserPreferenceResolution"/>; this contributor forwards winners only.
/// Cross-authority precedence (vs hosting) belongs to the assembler.
/// </summary>
public sealed class UserPreferenceContributor(
    IReadOnlyList<UserPreferenceRecord> activeRecords) : IPlanV3Contributor
{
    public string SourceId => "user-preference";

    public PlanContributionResult Contribute(PlanContributionContext context)
    {
        var report = UserPreferenceResolution.Resolve(activeRecords);
        var byId = activeRecords.ToDictionary(r => r.Id);

        var votes = report.Register
            .Select(d => new RegisterProposal(
                d.Dimension,
                d.Value,
                $"user-preference.{d.Dimension}",
                new Provenance(Origin: "told-by-user", EvidenceRef: d.WinnerId.ToString()),
                Restrictive: d.Restrictive))
            .ToList();

        var items = report.ExpressionRestrictions
            .Where(d => byId.TryGetValue(d.WinnerId, out var r) && r.Subject is not null)
            .Select(d => new ProposedItem
            {
                LocalId = $"restriction-{d.WinnerId:N}",
                Type = "expression-restriction",
                Category = RenderCategory.note,
                ProposedPolicy = ExpressionPolicy.must_not_express,
                ReasonCode = "user-preference.expression-restriction.stated",
                // Names the subject (the renderer must know what not to raise), never
                // the user's statement.
                Text = $"Do not raise {byId[d.WinnerId].Subject}.",
                Provenance = new Provenance(Origin: "told-by-user", EvidenceRef: d.WinnerId.ToString()),
                Retention = Retention.no_training,
            })
            .ToList();

        return new PlanContributionResult(items, votes);
    }
}

/// <summary>
/// Source 3: hosting configuration is a SEPARATE authority. It reads deployment
/// configuration, never the user's store, and votes under <c>hosting-config.*</c> with a
/// configuration-path evidence reference — so a deployment restriction can never
/// masquerade as something the user asked for. Which authority won, and why, is exactly
/// what the assembler's register decision records.
/// </summary>
public sealed class HostingConfigContributor(
    IReadOnlyDictionary<string, string> registerRestrictions) : IPlanV3Contributor
{
    public string SourceId => "hosting-config";

    public PlanContributionResult Contribute(PlanContributionContext context)
        => new(
            [],
            registerRestrictions
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new RegisterProposal(
                    kv.Key,
                    kv.Value,
                    $"hosting-config.{kv.Key}",
                    new Provenance(Origin: "derived", EvidenceRef: $"config:HostingPolicy:Register:{kv.Key}"),
                    Restrictive: true))
                .ToList());
}
