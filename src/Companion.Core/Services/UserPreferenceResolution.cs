using Companion.Core.Domain;

namespace Companion.Core.Services;

/// <summary>
/// The ONE place the within-user precedence rule lives (Source 3, amendment 9): a pure,
/// deterministic function from active records to per-slot winners. No I/O, no clock, no
/// randomness — and NO preference text: the report carries ids, dimensions, closed-set
/// value tokens, and reason tokens only, so it is safe to serialize into telemetry.
///
/// The rule, as amended: explicit supersession/revocation has already deactivated what it
/// replaced (the store enforces that on write), so actives normally hold one record per
/// (kind, scope, dimension). If several are nonetheless active for one slot, the newest
/// StatedAt wins, ties broken by ordinal id — deterministic, and REPORTED as
/// "newest-statement" so the anomaly is visible rather than silently absorbed.
/// Cross-authority precedence (user vs hosting) is NOT decided here — that belongs to
/// the assembler's contract-ordered register resolution.
/// </summary>
public static class UserPreferenceResolution
{
    /// <summary>
    /// One slot's outcome. `Reason` is "single-active" or "newest-statement". Deliberately
    /// carries NO Subject and no statement: a restriction's subject is user-stated text,
    /// and this report is what telemetry serializes. Consumers that need the subject
    /// (the note item's text) look the winning record up by id.
    /// </summary>
    public sealed record SlotDecision(
        UserPreferenceKind Kind,
        string Scope,
        string Dimension,
        Guid WinnerId,
        string Value,
        bool Restrictive,
        string Reason,
        IReadOnlyList<Guid> LoserIds);

    public sealed record Report(IReadOnlyList<SlotDecision> Decisions)
    {
        public IEnumerable<SlotDecision> Register
            => Decisions.Where(d => d.Kind == UserPreferenceKind.Register);

        public IEnumerable<SlotDecision> ExpressionRestrictions
            => Decisions.Where(d => d.Kind == UserPreferenceKind.ExpressionRestriction);
    }

    public static Report Resolve(IReadOnlyList<UserPreferenceRecord> records)
    {
        var decisions = new List<SlotDecision>();

        foreach (var slot in records
            .Where(r => r.Status == UserPreferenceStatus.Active)
            .GroupBy(r => (r.Kind, r.Scope, r.Dimension,
                // Restrictions are per-subject slots: "don't raise A" and "don't raise B"
                // are two standing restrictions, not competitors for one.
                Subject: r.Kind == UserPreferenceKind.ExpressionRestriction ? r.Subject : null))
            .OrderBy(g => g.Key.Kind).ThenBy(g => g.Key.Scope, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Dimension, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Subject, StringComparer.Ordinal))
        {
            var ordered = slot
                .OrderByDescending(r => r.StatedAt)
                .ThenByDescending(r => r.Id.ToString(), StringComparer.Ordinal)
                .ToList();
            var winner = ordered[0];

            decisions.Add(new SlotDecision(
                winner.Kind, winner.Scope, winner.Dimension,
                winner.Id, winner.Value, winner.Restrictive,
                ordered.Count == 1 ? "single-active" : "newest-statement",
                ordered.Skip(1).Select(r => r.Id).ToList()));
        }

        return new Report(decisions);
    }
}
