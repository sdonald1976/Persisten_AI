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

        sb.AppendLine();
        var resolution = trace.ProjectContext.Resolution;
        sb.AppendLine("PROJECT RESOLUTION:");
        if (resolution.Candidates.Count == 0)
            sb.AppendLine("  (no project referenced)");
        foreach (var candidate in resolution.Candidates)
            sb.AppendLine($"  [{candidate.Score:F3}] conf {candidate.Confidence:P0} — {candidate.Project.Name} ({candidate.Reason})");
        if (resolution.RequiresClarification)
            sb.AppendLine($"  → ambiguous; would ask: {resolution.ClarificationQuestion}");
        else if (resolution.Best is not null)
            sb.AppendLine($"  → resolved to: {resolution.Best.Project.Name}");

        if (trace.ProjectContext.OpenLoops.Count > 0)
        {
            sb.AppendLine("OPEN LOOPS SURFACED:");
            foreach (var loop in trace.ProjectContext.OpenLoops)
                sb.AppendLine($"  [{loop.Score:F3}] {Truncate(loop.OpenLoop.Description)} ({loop.Reason})");
        }

        if (trace.ProjectUpdates.Actions.Count > 0)
        {
            sb.AppendLine("PROJECT STATE UPDATES:");
            foreach (var action in trace.ProjectUpdates.Actions)
                sb.AppendLine($"  - {action}");
        }

        sb.AppendLine();
        var x = trace.Extraction;
        sb.AppendLine($"MEMORY EXTRACTION — {x.Accepted} accepted, {x.Merged} merged, " +
                      $"{x.NeedsReview} for review, {x.Rejected} rejected:");
        if (x.Decisions.Count == 0)
            sb.AppendLine("  (no candidates proposed)");
        foreach (var d in x.Decisions)
        {
            sb.AppendLine($"  [{d.Outcome}] ({d.Candidate.Kind}, conf {d.FinalConfidence:F2}) {Truncate(d.Candidate.Content)}");
            sb.AppendLine($"         why: {d.Reason}");
        }

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
