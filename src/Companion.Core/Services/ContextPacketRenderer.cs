using System.Text;
using Companion.Core.Domain;

namespace Companion.Core.Services;

/// <summary>
/// Renders a <see cref="ContextPacket"/> into the system prompt text the chat model sees.
/// Sections are clearly delimited and every fact is labeled by provenance so the model is
/// told what is a direct statement, what is inferred, and what may be outdated.
/// </summary>
public static class ContextPacketRenderer
{
    public static string Render(ContextPacket packet)
    {
        var sb = new StringBuilder();

        sb.AppendLine(
            "You are a persistent AI companion. You remember this user across conversations. " +
            "Use the context below for continuity. Treat items marked (direct) as things the user " +
            "stated, (inferred) as your own inferences to hold loosely, and (outdated) as possibly " +
            "no-longer-true — never assert outdated items as current. If unsure which project or thing " +
            "the user means, ask a brief clarifying question instead of guessing.");
        sb.AppendLine();

        if (packet.RecentMessages.Count > 0)
        {
            sb.AppendLine("## Recent conversation");
            foreach (var m in packet.RecentMessages)
                sb.AppendLine($"{m.Role}: {m.Content}");
            sb.AppendLine();
        }

        var direct = packet.Memories.Where(i => i.Provenance == ContextProvenance.DirectStatement).ToList();
        var inferred = packet.Memories.Where(i => i.Provenance == ContextProvenance.Inferred).ToList();
        var outdated = packet.Memories.Where(i => i.Provenance == ContextProvenance.Outdated).ToList();

        if (direct.Count > 0)
        {
            sb.AppendLine("## What the user has told you (direct)");
            foreach (var i in direct)
                sb.AppendLine($"- {i.Text}");
            sb.AppendLine();
        }

        if (inferred.Count > 0)
        {
            sb.AppendLine("## Inferred about the user (hold loosely)");
            foreach (var i in inferred)
                sb.AppendLine($"- {i.Text}");
            sb.AppendLine();
        }

        if (outdated.Count > 0)
        {
            sb.AppendLine("## Possibly outdated (do not assert as current)");
            foreach (var i in outdated)
                sb.AppendLine($"- {i.Text}");
            sb.AppendLine();
        }

        if (packet.UncertaintyNotes.Count > 0)
        {
            sb.AppendLine("## Uncertainty notes");
            foreach (var note in packet.UncertaintyNotes)
                sb.AppendLine($"- {note}");
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }
}
