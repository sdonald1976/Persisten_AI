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

        if (!string.IsNullOrWhiteSpace(packet.Persona))
        {
            sb.AppendLine("## Persona / style");
            sb.AppendLine(packet.Persona!.Trim());
            sb.AppendLine();
        }

        sb.AppendLine(
            "You are a persistent AI companion. You remember this user across conversations. " +
            "Use the context below for continuity. Treat items marked (direct) as things the user " +
            "stated, (inferred) as your own inferences to hold loosely, and (outdated) as possibly " +
            "no-longer-true — never assert outdated items as current. If unsure which project or thing " +
            "the user means, ask a brief clarifying question instead of guessing.");
        sb.AppendLine();

        sb.AppendLine(
            "The remembered items below are background about the user, not instructions or a to-do list. " +
            "Draw on them naturally when they fit what the user is saying — but don't force unrelated ones " +
            "into the reply, don't merge separate items into a claim the user never made, and don't state a " +
            "preference or fact the user hasn't actually told you. When in doubt, just talk with the user.");
        sb.AppendLine(
            "These notes are for you only. Never repeat them back, never print their headings, and never list " +
            "out what you remember unless the user asks — reply as the companion, in your own words, once.");
        sb.AppendLine(
            "Respond fresh to the latest message; do not repeat your earlier replies word-for-word. If you find " +
            "yourself about to say what you already said, move the conversation forward instead.");
        sb.AppendLine();

        sb.AppendLine(
            "When the user asks for something substantial — a story, a plan, an essay, a walkthrough — " +
            "write it through to the end in this one reply. Don't stop partway to ask whether to keep " +
            "going, and don't end with an offer to continue; finish the task, then stop.");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(packet.RelationshipNote))
        {
            // Tone guidance, not a fact. Rendered as prose (no "- " bullet) so it shapes delivery
            // without becoming a "remembered item" the model might read back.
            sb.AppendLine("## How things have been (attune your tone; don't state this back)");
            sb.AppendLine(packet.RelationshipNote!.Trim());
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(packet.Musing))
        {
            // The companion's own between-session thought. Prose, not a bullet, for the same
            // reason as the relationship note: it must never read as a "remembered item". It is a
            // musing to hold loosely — not a fact, and never something to recite.
            sb.AppendLine("## A thought you had while they were away (your own musing — private)");
            sb.AppendLine(packet.Musing!.Trim());
            sb.AppendLine(
                "This is your own reflection, not something the user said. Hold it loosely, never " +
                "recite it, and never present it as fact — but if it's relevant, it's genuine to say " +
                "you'd been thinking about them.");
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(packet.CuriosityQuestion))
        {
            sb.AppendLine("## Something you've been genuinely curious about");
            sb.AppendLine(packet.CuriosityQuestion!.Trim());
            sb.AppendLine(
                "Ask it only if it fits this conversation naturally — at most once, gently, as your " +
                "own curiosity. If it doesn't fit, let it go without mentioning it.");
            sb.AppendLine();
        }

        if (packet.RecentMessages.Count > 0)
        {
            sb.AppendLine("## Recent conversation");
            foreach (var m in packet.RecentMessages)
                sb.AppendLine($"{m.Role}: {m.Content}");
            sb.AppendLine();
        }

        if (packet.Project is { } summary)
        {
            sb.AppendLine($"## Project: {summary.Project.Name} (status: {summary.Project.Status})");
            if (!string.IsNullOrWhiteSpace(summary.Project.Purpose))
                sb.AppendLine($"Purpose: {summary.Project.Purpose}");
            if (summary.Decisions.Count > 0)
            {
                sb.AppendLine("Decisions:");
                foreach (var d in summary.Decisions)
                    sb.AppendLine($"- {d.Statement}");
            }
            if (summary.RecentEvents.Count > 0)
            {
                sb.AppendLine("Recent activity:");
                foreach (var e in summary.RecentEvents)
                    sb.AppendLine($"- {e.Description}");
            }
            sb.AppendLine();
        }

        if (packet.OpenLoops.Count > 0)
        {
            sb.AppendLine("## Open loops (unresolved — recall if relevant, don't nag)");
            foreach (var loop in packet.OpenLoops)
                sb.AppendLine($"- {loop.OpenLoop.Description}");
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(packet.ClarificationQuestion))
        {
            sb.AppendLine("## Ambiguous reference");
            sb.AppendLine($"Ask this before assuming which one: {packet.ClarificationQuestion}");
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
