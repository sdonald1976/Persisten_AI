using Companion.Core.Domain;
using Companion.PlanV3;

namespace Companion.MouthFactory.Schema;

/// <summary>
/// Scenario truth + a target utterance → the exact bytes the mouth will be trained on.
///
/// The only thing this class does that matters is CALL <c>MouthPromptV4</c>. It builds no prompt
/// of its own and formats no plan of its own; if it did, the corpus would be in the factory's
/// format rather than the renderer's, and every row would be quietly wrong.
///
/// The system message is a real <see cref="ContextPacket"/> put through the real
/// <c>ContextPacketRenderer</c>, because that is what occupies the system slot in production.
/// </summary>
public static class RowRendering
{
    public static (TrainingRow? Row, TrainingRowMetadata? Metadata, string? Failure) Render(
        ScenarioTruth scenario,
        global::Companion.PlanV3.PlanV3 plan,
        string target,
        int variantIndex,
        GenerationProvenance provenance)
    {
        if (string.IsNullOrWhiteSpace(target))
            return (null, null, "empty target");

        var user = scenario.Participants.FirstOrDefault(p => p.Kind == ParticipantKind.User);
        var companion = scenario.Participants.FirstOrDefault(p => p.Kind == ParticipantKind.Companion);
        if (user is null || companion is null)
            return (null, null, "scenario lacks a user or companion participant");

        var packet = BuildPacket(scenario, user, companion);
        var transcript = scenario.History
            .Select(t => (t.Role, t.Text))
            .ToList();

        string system, input;
        try
        {
            system = MouthPromptV4.SystemMessage(packet);
            input = MouthPromptV4.UserMessage(plan, transcript, scenario.UserMessage, user.Name, companion.Name);
        }
        catch (PlanNotRenderableException ex)
        {
            // Cannot happen for a plan that cleared PlanConstruction, but the serializer is the
            // authority and a change there should surface here rather than produce a row.
            return (null, null, $"render-ineligible: {string.Join("; ", ex.Eligibility.Reasons)}");
        }

        var id = $"{scenario.Id}#{variantIndex}";
        var row = new TrainingRow
        {
            Id = id,
            System = system,
            Input = input,
            Target = target.Trim(),
            FormatVersion = MouthPromptV4.FormatVersion,
        };

        var words = target.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var metadata = new TrainingRowMetadata
        {
            Id = id,
            ScenarioId = scenario.Id,
            ScenarioFamilyId = scenario.ScenarioFamilyId,
            FamilyId = scenario.FamilyId,
            Layer = scenario.Layer,
            SourceFamilyId = scenario.SourceFamilyId,
            SourceRowRef = scenario.SourceRowRef,
            VariantIndex = variantIndex,
            Generation = provenance,
            TranscriptTurns = scenario.History.Count,
            TargetWords = words,
            Opening = Opening(target),
        };

        return (row, metadata, null);
    }

    /// <summary>
    /// The context packet a scenario implies. Deliberately minimal — R5 §4's "minimal is not
    /// absent": a Layer A row still carries the real packet shape, because a row trained in a
    /// different shape teaches a format the model will never see again.
    /// </summary>
    public static ContextPacket BuildPacket(
        ScenarioTruth scenario, Participant user, Participant companion)
        => new()
        {
            UserMessage = scenario.UserMessage,
            Identities = new PromptIdentityContext
            {
                UserName = user.Name,
                CompanionName = companion.Name,
                CompanionPronouns = companion.Pronouns,
            },
            // Background-only facts are the packet's business: they may shape tone and must not
            // surface, which is precisely what a memory line is.
            Memories = scenario.ApprovedFacts
                .Where(f => f.Policy == FactPolicy.BackgroundOnly)
                .Select(f => new ContextItem
                {
                    Text = f.Text,
                    Provenance = ContextProvenance.DirectStatement,
                })
                .ToList(),
        };

    /// <summary>First three words, lowercased and stripped — the unit opening-diversity counts.</summary>
    public static string Opening(string target)
    {
        var words = target.ToLowerInvariant()
            .Split([' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray()))
            .Where(w => w.Length > 0)
            .Take(3);
        return string.Join(' ', words);
    }
}
