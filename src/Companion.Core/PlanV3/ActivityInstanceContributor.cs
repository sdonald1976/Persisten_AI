using Companion.Core.Activities;

namespace Companion.PlanV3;

/// <summary>
/// The bridge from the activity runtime to native V3 (Source 1b). Contributes exactly the
/// SELECTED move and one minimal frame line — never the ledger, never hypotheses, never
/// evidence. A selection failure contributes nothing and reports the diagnosed reason, so
/// a turn can never quietly pretend the activity vanished.
/// </summary>
public sealed class ActivityInstanceContributor(
    Companion.Core.Activities.ActivityInstance? instance,
    StrategyState? state = null,
    string? selectionFailureReason = null) : IPlanV3Contributor
{
    public string SourceId => "procedure";

    public PlanContributionResult Contribute(PlanContributionContext context)
    {
        if (instance is null || !instance.IsActive)
            return PlanContributionResult.Empty;

        if (selectionFailureReason is { } failure)
            return PlanContributionResult.Failed($"procedure-selection-failed:{failure}");

        var items = new List<ProposedItem>();
        var evidence = new Provenance(Origin: "derived", EvidenceRef: $"activity:{instance.InstanceId}");

        if (instance.PendingMove is { } move)
            items.Add(new ProposedItem
            {
                LocalId = $"move-{move.StableKey}",
                Type = move.Kind == ActivityMoveKind.Guess ? "activity-guess" : "activity-question",
                Category = RenderCategory.clarify,
                ProposedPolicy = ExpressionPolicy.ask_required,
                Text = move.Text,
                Provenance = evidence,
            });

        var asker = instance.AskerParticipantId == context.CompanionParticipantId
            ? $"{context.CompanionParticipantId} asks" : "the user asks";
        items.Add(new ProposedItem
        {
            LocalId = "frame",
            Type = "activity-state",
            Category = RenderCategory.state,
            ProposedPolicy = ExpressionPolicy.background_only,
            Text = $"{instance.ActivityType}: {asker}; question {instance.CurrentQuestionNumber} "
                 + $"of {instance.QuestionLimit}.",
            Provenance = evidence,
        });

        return new PlanContributionResult(items);
    }
}
