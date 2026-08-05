using System.Text;
using Companion.Core.Domain;

namespace Companion.Cli;

/// <summary>
/// Renders a <see cref="TurnTrace"/> for the developer-facing `/why` view: what was
/// retrieved, the scores, why each matched, what was excluded, and the exact context sent
/// to the model. This is the diagnostic surface that makes memory behavior debuggable.
/// </summary>
public static class TraceRenderer
{
    public static string Render(TurnTrace trace)
    {
        var sb = new StringBuilder();
        sb.AppendLine("──────────── retrieval diagnostics ────────────");
        sb.AppendLine($"user message : {trace.UserMessage}");
        sb.AppendLine($"project      : {trace.DetectedProject ?? "(none detected)"}");
        sb.AppendLine();

        sb.AppendLine($"RETRIEVED ({trace.Retrieved.Count}) — included in context:");
        if (trace.Retrieved.Count == 0)
            sb.AppendLine("  (none)");
        foreach (var r in trace.Retrieved)
            AppendResult(sb, r);

        sb.AppendLine();
        var shownExcluded = trace.Excluded.Take(5).ToList();
        sb.AppendLine($"EXCLUDED (showing {shownExcluded.Count} of {trace.Excluded.Count}) — scored but not used:");
        foreach (var r in shownExcluded)
            AppendResult(sb, r);

        sb.AppendLine();
        sb.AppendLine($"CONTEXT PACKET (~{trace.Packet.EstimatedTokens} tokens):");
        foreach (var line in trace.Packet.Render().Split('\n'))
            sb.AppendLine($"  │ {line.TrimEnd('\r')}");

        sb.AppendLine("───────────────────────────────────────────────");
        return sb.ToString();
    }

    private static void AppendResult(StringBuilder sb, RetrievalResult r)
    {
        sb.AppendLine($"  [{r.Score:F3}] ({r.Memory.Kind}) {Truncate(r.Memory.Content)}");
        var signals = string.Join("  ", r.Signals
            .Where(s => s.Value > 0.0001)
            .OrderByDescending(s => s.Value)
            .Select(s => $"{s.Key}={s.Value:F2}"));
        if (signals.Length > 0)
            sb.AppendLine($"         signals: {signals}");
        sb.AppendLine($"         why    : {r.Reason}");
    }

    private static string Truncate(string text, int max = 90)
        => text.Length <= max ? text : text[..max] + "…";
}
