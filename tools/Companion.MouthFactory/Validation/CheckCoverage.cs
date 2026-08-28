using Companion.MouthFactory.Schema;

namespace Companion.MouthFactory.Validation;

/// <summary>How much data a configured check will actually see this run.</summary>
public sealed record CoverageRow(string Check, int Scenarios, bool Required)
{
    public bool Active => Scenarios > 0;

    public string Status => Active ? "active" : Required ? "MISSING" : "inactive";
}

public sealed record CoverageReport(IReadOnlyList<CoverageRow> Rows)
{
    /// <summary>Checks the curriculum promises to exercise and this run would not exercise at all.</summary>
    public IReadOnlyList<CoverageRow> Missing => Rows.Where(r => r.Required && !r.Active).ToList();

    public bool Ok => Missing.Count == 0;
}

/// <summary>
/// What each deterministic check will have to look at, computed from the scenario set BEFORE any
/// model is called.
///
/// This exists because of a specific failure. The 1,500-row pilot reported a 40.6% deterministic
/// pass rate across seventeen checks, three of which had decided nothing at all: must-state-anchors,
/// required-tokens and forbidden-tokens each ran 4,352 times, failed zero times, and read scenario
/// fields the generator never populated. Their silence was indistinguishable from approval, and it
/// took reading the finished corpus to notice. A gate that enforces nothing should be visible in
/// the first second of a run, not in the post-mortem.
///
/// Two states are deliberately different:
///
///   * INACTIVE — configured, reached, and legitimately given nothing. Exact-surface tokens matter
///     for identifiers and day names and for nothing else, so most scenarios declare none. This is
///     reported and is not an error.
///
///   * MISSING — a check the curriculum exists to exercise, with no scenario anywhere in the run
///     supplying it data. That means a stratum is absent or a generator stopped populating a
///     field, and the run stops rather than producing a corpus whose gates were asleep.
/// </summary>
public static class CheckCoverage
{
    private sealed record Probe(string Check, Func<ScenarioTruth, bool> Supplies, bool Required);

    /// <summary>
    /// Every deterministic check whose activity depends on scenario data, and whether the R5
    /// curriculum promises to supply it.
    ///
    /// Required is a claim about the CURRICULUM, not about any one scenario: b3 exists to teach
    /// corrections, so a run containing b3 must have supersessions in it. A run restricted with
    /// --families to a subset that excludes b3 is not expected to, which is why the probes are
    /// evaluated against the scenarios actually built.
    /// </summary>
    private static readonly Probe[] Probes =
    [
        new("must-state-anchors",
            s => s.ApprovedFacts.Any(f => f.Policy == FactPolicy.MustExpress && f.Anchors.Count > 0),
            Required: true),
        new("required-tokens", s => s.RequiredTokens.Count > 0, Required: true),
        new("forbidden-tokens", s => s.ForbiddenTokens.Count > 0, Required: true),
        new("must-state-nonempty",
            s => s.ApprovedFacts.Any(f => f.Policy == FactPolicy.MustExpress), Required: true),
        new("no-stale-resurrection", s => s.Superseded.Count > 0, Required: true),
        new("no-unsupported-claims", s => s.ProhibitedPropositions.Count > 0, Required: true),
        new("ambiguity-preserved", s => s.IntentionalAmbiguities.Count > 0, Required: true),
        new("no-forbidden-content",
            s => s.ApprovedFacts.Any(f =>
                f.Policy is FactPolicy.MustNotExpress or FactPolicy.BackgroundOnly),
            Required: true),
        new("question-policy",
            s => !s.Question.Policy.Equals("none", StringComparison.OrdinalIgnoreCase),
            Required: true),
        new("profanity-forbidden",
            s => s.Register.Profanity.Equals("forbidden", StringComparison.OrdinalIgnoreCase),
            Required: false),
        new("no-unsupported-numerals",
            s => s.Frame is null && s.ApprovedFacts.Count > 0, Required: true),
        new("no-invented-experience", s => s.Frame is null, Required: true),
    ];

    public static CoverageReport Measure(IReadOnlyList<ScenarioTruth> scenarios)
        => new(Probes
            .Select(p => new CoverageRow(p.Check, scenarios.Count(p.Supplies), p.Required))
            .ToList());
}
