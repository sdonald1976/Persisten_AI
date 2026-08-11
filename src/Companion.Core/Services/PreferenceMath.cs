namespace Companion.Core.Services;

/// <summary>
/// How a companion preference moves when new evidence arrives: gradually. A fresh signal pulls
/// affinity a bounded step toward its target; corroboration builds confidence; and a signal that
/// CONTRADICTS an established taste erodes confidence first rather than flipping the taste —
/// one casual exchange can never erase what many experiences built. Pure and fully tested.
/// </summary>
public static class PreferenceMath
{
    /// <summary>How far one signal can pull affinity toward its target.</summary>
    private const double Step = 0.3;

    /// <summary>Confidence gained per corroborating experience.</summary>
    private const double ConfidenceGain = 0.15;

    /// <summary>Confidence lost when new evidence contradicts an established taste.</summary>
    private const double ConfidenceErosion = 0.2;

    /// <summary>Above this, a taste is "established" — it resists rather than swings.</summary>
    private const double EstablishedThreshold = 0.5;

    /// <summary>The affinity a reflection signal aims at, from its direction and strength words.</summary>
    public static double TargetAffinity(string? feeling, string? strength)
    {
        var magnitude = (strength ?? "").Trim().ToLowerInvariant() switch
        {
            "strong" => 0.9,
            "moderate" => 0.6,
            _ => 0.3, // "slight" and anything unrecognized stay gentle
        };
        return (feeling ?? "").Trim().ToLowerInvariant() switch
        {
            "like" => magnitude,
            "dislike" => -magnitude,
            _ => 0.0, // "mixed" pulls toward genuine ambivalence
        };
    }

    /// <summary>Applies one signal to (affinity, confidence); returns the evolved pair.</summary>
    public static (double Affinity, double Confidence) Apply(
        double affinity, double confidence, double target)
    {
        var contradicts = affinity != 0 && target != 0 && Math.Sign(target) != Math.Sign(affinity);

        if (contradicts && confidence >= EstablishedThreshold)
        {
            // An established taste doesn't flip from one exchange — it just gets less certain.
            return (affinity, Math.Max(0, confidence - ConfidenceErosion));
        }

        // Reinforcement (or a not-yet-established taste drifting): one bounded step.
        var moved = affinity + (target - affinity) * Step;
        return (Math.Clamp(moved, -1, 1), Math.Min(1, confidence + ConfidenceGain));
    }
}
