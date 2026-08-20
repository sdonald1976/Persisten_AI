using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Microsoft.Extensions.Logging;

namespace Companion.Core.Services;

/// <summary>
/// The system-owned decision that a gap deserves a question — deliberately SEPARATE from
/// the decision to record it: a gap important enough to record is not thereby important
/// enough to interrupt conversation. Runs in the reflection cadence, never on a request
/// path, and feeds the EXISTING Curiosity lifecycle (cooldown, ask-once, caps, fit rules
/// all unchanged). Every considered gap leaves a capture row — promoted or suppressed,
/// with the reason — so the floor is judged on data.
///
/// v1 promotes ONLY UnknownConcept gaps: unresolved references and evidence conflicts age
/// into worse conversations than letting them go, so they are scored and captured but
/// never asked (docs/KNOWLEDGE_GAPS.md §5).
/// </summary>
public sealed class GapPromoter
{
    /// <summary>At most one gap-sourced curiosity per reflection pass, so reflection-born
    /// curiosities are never crowded out of the existing per-pass cap.</summary>
    private const int MaxPerPass = 1;

    private readonly IGapStore _gaps;
    private readonly IReflectionStore _reflections;
    private readonly IShadowRecorder _shadow;
    private readonly TimeProvider _clock;
    private readonly ILogger<GapPromoter> _logger;

    public GapPromoter(
        IGapStore gaps, IReflectionStore reflections, IShadowRecorder shadow,
        TimeProvider clock, ILogger<GapPromoter> logger)
    {
        _gaps = gaps;
        _reflections = reflections;
        _shadow = shadow;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Considers every open gap once; promotes at most <see cref="MaxPerPass"/>.
    /// Returns how many curiosities were minted.</summary>
    public async Task<int> PromoteAsync(string userId, Guid reflectionId, CancellationToken ct = default)
    {
        var open = await _gaps.GetOpenAsync(userId, ct); // occurrences desc, oldest first
        if (open.Count == 0)
            return 0;

        var held = await _reflections.GetOpenCuriositiesAsync(userId, ct);
        var promoted = 0;
        foreach (var gap in open)
        {
            var suppression = Consider(gap, promoted, held);
            await RecordAsync(gap, suppression, ct);
            if (suppression is not null)
                continue;

            var curiosity = new Curiosity
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ReflectionId = reflectionId,
                GapId = gap.Id,
                Question = Prompts.Format("curiosity.gap.unknown-concept", ("subject", gap.Subject)),
                About = gap.Subject,
                Reason = $"knowledge gap ({gap.Kind.ToKebab()}, seen {gap.Occurrences}x, " +
                         $"source {gap.Source.ToKebab()}) — she has never learned this",
                CreatedAt = _clock.GetUtcNow(),
            };
            await _reflections.AddCuriosityAsync(curiosity, ct);
            await _gaps.PromoteAsync(userId, gap.Id, curiosity.Id, ct);
            promoted++;
            _logger.LogInformation(
                "Promoted knowledge gap \"{Subject}\" to a curiosity for {UserId}.", gap.Subject, userId);
        }
        return promoted;
    }

    /// <summary>Null = promote; otherwise the suppression reason, which is recorded. The
    /// reasons are the measurement: a floor judged on captured data, never intuition.</summary>
    private static string? Consider(KnowledgeGap gap, int promotedSoFar, IReadOnlyList<Curiosity> held)
    {
        // The gap's OWN merit is judged before the shared cap, so the recorded reason
        // describes the gap rather than its place in the queue — the measurement depends
        // on that distinction.
        if (gap.Kind != GapKind.UnknownConcept)
            return "kind-not-promotable";
        if (gap.Pursuit != GapPursuit.AskUser)
            return "pursuit-not-ask";
        if (held.Any(c => string.Equals(c.About, gap.Subject, StringComparison.OrdinalIgnoreCase)))
            return "duplicate-about";
        // The floor: an explicit knowledge question is near-certain worth one ask; anything
        // else needs to have recurred before it earns one.
        if (gap.Source != GapSource.KnowledgeLookup && gap.Occurrences < 2)
            return "below-floor";
        if (promotedSoFar >= MaxPerPass)
            return "cap-reached";
        return null;
    }

    private Task RecordAsync(KnowledgeGap gap, string? suppression, CancellationToken ct)
        => _shadow.IsRecording
            ? _shadow.RecordAsync(new ShadowComparison
            {
                Id = Guid.NewGuid(),
                Subject = "gap.promotion",
                Legacy = suppression ?? "promoted",
                Model = null,
                Applied = "legacy",
                Input = $"[{gap.Kind.ToKebab()}|{gap.Source.ToKebab()}|seen={gap.Occurrences}] {gap.Subject}",
            }, ct)
            : Task.CompletedTask;
}
