using System.Text;
using Companion.Core.Domain;

namespace Companion.Core.Services;

/// <summary>
/// Renders a <see cref="ContextPacket"/> into the system prompt text the chat model sees.
/// Sections are clearly delimited and every fact is labeled by provenance so the model is
/// told what is a direct statement, what is inferred, and what may be outdated.
/// All wording comes from the <see cref="Prompts"/> catalog (editable at runtime); this class
/// owns only the structure — which sections exist, in what order, from which packet fields.
/// </summary>
public static class ContextPacketRenderer
{
    public static string Render(ContextPacket packet)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(packet.Persona))
        {
            sb.AppendLine(Prompts.Get("renderer.persona.header"));
            sb.AppendLine(packet.Persona!.Trim());
            sb.AppendLine();
        }

        sb.AppendLine(Prompts.Get("renderer.core"));
        sb.AppendLine();

        sb.AppendLine(Prompts.Get("renderer.memory-rules"));
        sb.AppendLine();

        sb.AppendLine(Prompts.Get("renderer.finish-task"));
        sb.AppendLine();

        // Her own state, not a fact about the user. Prose so it can't read as a memory item.
        ProseSection(sb, "renderer.mood.header", packet.MoodNote, "renderer.mood.rules");

        ProseSection(sb, "renderer.register.header", packet.RegisterNote);

        // Calibration, not content: it sets how casual/teasing/shorthand she may be.
        ProseSection(sb, "renderer.familiarity.header", packet.FamiliarityNote);

        // Tone guidance, not a fact — prose (no "- " bullet) so it never reads as a remembered item.
        ProseSection(sb, "renderer.relationship.header", packet.RelationshipNote);

        ProseSection(sb, "renderer.temporal.header", packet.TemporalNote, "renderer.temporal.rules");

        // The companion's own between-session thought: a musing to hold loosely, never a fact.
        ProseSection(sb, "renderer.musing.header", packet.Musing, "renderer.musing.rules");

        ProseSection(sb, "renderer.curiosity.header", packet.CuriosityQuestion, "renderer.curiosity.rules");

        if (packet.RecentMessages.Count > 0)
        {
            sb.AppendLine(Prompts.Get("renderer.recent.header"));
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
            sb.AppendLine(Prompts.Get("renderer.openloops.header"));
            foreach (var loop in packet.OpenLoops)
                sb.AppendLine($"- {loop.OpenLoop.Description}");
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(packet.ClarificationQuestion))
        {
            sb.AppendLine(Prompts.Get("renderer.ambiguous.header"));
            sb.AppendLine(Prompts.Format("renderer.ambiguous.line", ("question", packet.ClarificationQuestion)));
            sb.AppendLine();
        }

        // Shared moments are their own section regardless of provenance: they're history you were
        // both part of, told as "remember when we…", never as bare facts about the user.
        var shared = packet.Memories.Where(i => i.Owner == MemoryOwner.Shared).ToList();
        var rest = packet.Memories.Where(i => i.Owner != MemoryOwner.Shared).ToList();
        var direct = rest.Where(i => i.Provenance == ContextProvenance.DirectStatement).ToList();
        var inferred = rest.Where(i => i.Provenance == ContextProvenance.Inferred).ToList();
        var outdated = rest.Where(i => i.Provenance == ContextProvenance.Outdated).ToList();

        BulletSection(sb, "renderer.shared.header", shared.Select(i => i.Text), "renderer.shared.rules");
        BulletSection(sb, "renderer.direct.header", direct.Select(i => i.Text));
        BulletSection(sb, "renderer.inferred.header", inferred.Select(i => i.Text));
        BulletSection(sb, "renderer.outdated.header", outdated.Select(i => i.Text));
        BulletSection(sb, "renderer.preferences.header", packet.PreferenceNotes, "renderer.preferences.rules");
        BulletSection(sb, "renderer.uncertainty.header", packet.UncertaintyNotes);

        return sb.ToString().TrimEnd();
    }

    /// <summary>A header + prose body (+ optional trailing rules line), skipped when the body is empty.</summary>
    private static void ProseSection(StringBuilder sb, string headerKey, string? body, string? rulesKey = null)
    {
        if (string.IsNullOrWhiteSpace(body))
            return;
        sb.AppendLine(Prompts.Get(headerKey));
        sb.AppendLine(body!.Trim());
        if (rulesKey is not null)
            sb.AppendLine(Prompts.Get(rulesKey));
        sb.AppendLine();
    }

    /// <summary>A header + bullet list (+ optional trailing rules line), skipped when empty.</summary>
    private static void BulletSection(
        StringBuilder sb, string headerKey, IEnumerable<string> items, string? rulesKey = null)
    {
        var list = items.ToList();
        if (list.Count == 0)
            return;
        sb.AppendLine(Prompts.Get(headerKey));
        foreach (var item in list)
            sb.AppendLine($"- {item}");
        if (rulesKey is not null)
            sb.AppendLine(Prompts.Get(rulesKey));
        sb.AppendLine();
    }
}
