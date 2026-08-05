namespace Companion.Core.Services;

/// <summary>
/// Computes a memory's final confidence from the extractor's proposed value, how directly it
/// was sourced (a direct user statement is stronger than something inferred from the
/// assistant's own reply), and how much existing memory corroborates it. Pure and testable.
/// </summary>
public static class ConfidenceCalculator
{
    public static double Compute(double proposedConfidence, bool fromDirectUserStatement, int corroborations)
    {
        var confidence = Math.Clamp(proposedConfidence, 0.0, 1.0);

        // Direct user statements are more trustworthy than inferences from assistant text.
        confidence += fromDirectUserStatement ? 0.15 : -0.10;

        // Each independent corroboration nudges confidence up, with diminishing returns.
        confidence += Math.Min(0.20, 0.10 * Math.Max(0, corroborations));

        return Math.Clamp(confidence, 0.0, 1.0);
    }
}
