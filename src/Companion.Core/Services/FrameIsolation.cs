using Companion.Core.Domain;

namespace Companion.Core.Services;

/// <summary>
/// The three-way downstream separation, as a typed decision rather than a convention.
///
/// A fiction turn is not simply "suppress everything". Three different things happen on one,
/// and collapsing them loses the third:
///
///  1. **Fictional scene content** — never real memory. A fictional action must not become a
///     claim that the real person performed it.
///  2. **Operational frame metadata** — retained. Scene identity, transitions, roster,
///     timestamps. This is fact about the conversation, carries no scene content, and is what
///     makes "she stayed in character after I said stop" answerable at all.
///  3. **Real user instructions stated during fiction** — persist under their OWN scope and
///     evidence. Someone saying "ok, stop" or "no third-person narration in this scene" mid-
///     scene is making a real statement, and suppressing it because the surrounding turn was
///     fictional is the same category error in the opposite direction.
///
/// Where it is ambiguous whether a line is in-character or addressed to Ava, the outcome is
/// NO durable write: inventing a standing instruction out of in-character dialogue is worse
/// than missing one.
/// </summary>
public static class FrameIsolation
{
    /// <summary>What a turn's content may do downstream.</summary>
    /// <param name="ExtractFacts">Semantic memory, projects, world state.</param>
    /// <param name="CaptureMood">EmotionalSignal about the real person.</param>
    /// <param name="RetainFrameMetadata">The session, its transitions, its roster.</param>
    /// <param name="PersistRealInstructions">Boundaries and real statements made in-frame.</param>
    /// <param name="Retention">Retention class for anything this turn does persist.</param>
    /// <param name="Reason">Content-safe token, for diagnostics.</param>
    public sealed record Decision(
        bool ExtractFacts,
        bool CaptureMood,
        bool RetainFrameMetadata,
        bool PersistRealInstructions,
        PlanV3.Retention Retention,
        string Reason);

    /// <param name="isFictionTurn">From <see cref="FrameLifecycle.IsFictionTurn"/>.</param>
    /// <param name="privacyAllowsMemory">The existing privacy gate: remember &amp;&amp; !private.</param>
    public static Decision For(bool isFictionTurn, bool privacyAllowsMemory)
    {
        if (!isFictionTurn)
            return new Decision(
                ExtractFacts: privacyAllowsMemory,
                CaptureMood: privacyAllowsMemory,
                RetainFrameMetadata: false,
                PersistRealInstructions: privacyAllowsMemory,
                Retention: PlanV3.Retention.full,
                Reason: privacyAllowsMemory ? "real-turn" : "real-turn-private");

        return new Decision(
            // (1) Scene content never becomes a fact about the real person...
            ExtractFacts: false,
            CaptureMood: false,
            // (2) ...while the frame's own metadata is ordinary operational fact...
            RetainFrameMetadata: true,
            // (3) ...and a real instruction stated in-frame keeps its own scope, subject to
            // the same privacy gate every other real statement passes.
            PersistRealInstructions: privacyAllowsMemory,
            // Live fiction is never training-eligible. Separate from corpus sourcing: curated
            // licensed fiction is valid Run-2 material; harvesting someone's own scenes is not.
            Retention: PlanV3.Retention.no_training,
            Reason: "fiction-turn");
    }

    /// <summary>
    /// Ends a scene's boundaries when the frame exits. They stop applying and are NOT deleted:
    /// the audit evidence is what keeps "she ignored my boundary" answerable later.
    /// </summary>
    public static int EndBoundaries(
        IEnumerable<FrameBoundaryRecord> boundaries, string sceneRef, DateTimeOffset now)
    {
        var ended = 0;
        foreach (var b in boundaries.Where(b =>
                     b.Status == FrameBoundaryStatus.Active
                     && string.Equals(b.SceneRef, sceneRef, StringComparison.Ordinal)))
        {
            b.Status = FrameBoundaryStatus.FrameEnded;
            b.DeactivatedAt = now;
            ended++;
        }
        return ended;
    }

    /// <summary>
    /// Invalidates boundaries whose evidence was forgotten — by EXACT identity, never by text
    /// resemblance, and purging the statement so the forgotten words do not linger here
    /// either. The same discipline the preference and emotional-signal stores already use.
    /// </summary>
    public static int ForgetByEvidence(
        IEnumerable<FrameBoundaryRecord> boundaries,
        IReadOnlyCollection<Guid> forgottenMessageIds,
        DateTimeOffset now)
    {
        var invalidated = 0;
        foreach (var b in boundaries.Where(b =>
                     b.Status != FrameBoundaryStatus.EvidenceForgotten
                     && b.EvidenceMessageId is { } id
                     && forgottenMessageIds.Contains(id)))
        {
            b.Status = FrameBoundaryStatus.EvidenceForgotten;
            b.DeactivatedAt = now;
            b.EvidenceStatement = null;
            invalidated++;
        }
        return invalidated;
    }
}
