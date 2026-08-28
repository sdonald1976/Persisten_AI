namespace Companion.MouthFactory.Generation;

/// <summary>One way a role configuration violates independence, and why it matters.</summary>
public sealed record IndependenceViolation(string Code, string Detail);

/// <summary>
/// The writer may not grade its own work — enforced on MODEL IDENTITY, not just on role.
///
/// <see cref="RoleRouter"/> already refuses to let one invocation both generate and approve: the
/// writer role cannot be passed to CriticiseAsync and a critic role cannot be passed to
/// WriteTargetAsync. That is a real guarantee and it is not this one. The router's map is
/// Role → client, and nothing in it inspects which weights sit behind a slot, so pointing
/// MOUTH_WRITER_MODEL and MOUTH_FAITHFULNESS_MODEL at the same tag yields two proper role slots
/// over one model. The controlled comparison did exactly that, and the router had no opinion.
///
/// Separate invocations do remove the crudest failure — the model is not shown its own output
/// inside one context and asked to bless it. What they cannot remove is shared bias: a model
/// asked whether its own phrasing conveys a meaning has already decided that it does. For a
/// critic that GATES training data, that correlation is the whole risk.
///
/// So: a gating critic must not share a model identifier with the writer. Critics sharing with
/// each other is weaker — they are correlated but neither is marking its own homework — and is
/// reported rather than refused.
/// </summary>
public static class RoleIndependence
{
    /// <summary>Roles whose verdict can keep a row out of the corpus.</summary>
    public static readonly IReadOnlyList<Role> GatingCritics =
    [
        Role.FaithfulnessCritic, Role.NaturalnessCritic,
        Role.StyleCritic, Role.AdversarialCritic,
    ];

    /// <summary>
    /// Violations in a role→model configuration. Empty means the configuration may gate rows.
    /// </summary>
    public static IReadOnlyList<IndependenceViolation> Check(
        IReadOnlyDictionary<Role, string> models)
    {
        var violations = new List<IndependenceViolation>();

        if (!models.TryGetValue(Role.TargetWriter, out var writer)
            || string.IsNullOrWhiteSpace(writer))
            return violations;                       // nothing writes; nothing to collide with

        foreach (var critic in GatingCritics)
        {
            if (!models.TryGetValue(critic, out var judge) || string.IsNullOrWhiteSpace(judge))
                continue;
            if (Same(judge, writer))
                violations.Add(new IndependenceViolation(
                    "writer-is-judge",
                    $"{critic} uses '{judge}', the same model as the target writer. A gating "
                    + "critic must not grade its own writer's output: separate invocations remove "
                    + "the crudest failure but not the shared bias, and this critic can keep rows "
                    + "out of the corpus. Configure a different model."));
        }

        return violations;
    }

    /// <summary>
    /// Critics that share a model with EACH OTHER. Not a violation — neither is marking its own
    /// homework — but their agreement is not independent evidence, so it is worth saying.
    /// </summary>
    public static IReadOnlyList<string> CorrelatedCritics(IReadOnlyDictionary<Role, string> models)
        => GatingCritics
            .Where(models.ContainsKey)
            .GroupBy(r => models[r], StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => $"{string.Join(" and ", g)} both use '{g.Key}'; their agreement is "
                         + "correlated, not independent")
            .ToList();

    private static bool Same(string a, string b)
        => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// "qwen2.5:14b-instruct" and "qwen2.5:14b-instruct:latest" are the same weights. Comparing
    /// raw strings would let an implicit tag slip a collision past the check.
    /// </summary>
    private static string Normalize(string model)
    {
        var m = model.Trim();
        return m.EndsWith(":latest", StringComparison.OrdinalIgnoreCase)
            ? m[..^":latest".Length]
            : m;
    }
}
