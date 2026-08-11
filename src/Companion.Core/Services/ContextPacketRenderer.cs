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

        if (packet.Identities is { } identities)
        {
            RenderIdentities(sb, identities);
            RenderRelationship(sb, identities);
        }

        if (!string.IsNullOrWhiteSpace(packet.Persona))
        {
            var who = packet.Identities?.CompanionRef ?? "COMPANION";
            sb.AppendLine($"# {who.ToUpperInvariant()} PERSONALITY / STYLE");
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
            {
                var label = m.Role switch
                {
                    MessageRole.User => packet.Identities?.UserLabel ?? "[USER]",
                    MessageRole.Assistant => packet.Identities?.CompanionLabel ?? "[COMPANION]",
                    _ => "[SYSTEM]",
                };
                sb.AppendLine(label);
                sb.AppendLine(m.Content);
            }
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

        BulletSection(sb, "renderer.shared.header", shared.Select(i => FormatMemory(i, packet.Identities)), "renderer.shared.rules");
        BulletSection(sb, "renderer.direct.header", direct.Select(i => FormatMemory(i, packet.Identities)));
        BulletSection(sb, "renderer.inferred.header", inferred.Select(i => FormatMemory(i, packet.Identities)));
        BulletSection(sb, "renderer.outdated.header", outdated.Select(i => FormatMemory(i, packet.Identities)));
        BulletSection(sb, "renderer.preferences.header", packet.PreferenceNotes, "renderer.preferences.rules");
        BulletSection(sb, "renderer.uncertainty.header", packet.UncertaintyNotes);

        return sb.ToString().TrimEnd();
    }

    private static void RenderIdentities(StringBuilder sb, PromptIdentityContext identities)
    {
        sb.AppendLine("# AUTHORITATIVE IDENTITIES");
        sb.AppendLine();
        sb.AppendLine("COMPANION");
        if (!string.IsNullOrWhiteSpace(identities.CompanionName))
            sb.AppendLine($"Name: {identities.CompanionRef}");
        if (!string.IsNullOrWhiteSpace(identities.CompanionGender))
            sb.AppendLine($"Gender: {identities.CompanionGender!.Trim()}");
        if (!string.IsNullOrWhiteSpace(identities.CompanionPronouns))
            sb.AppendLine($"Pronouns: {identities.CompanionPronouns!.Trim()}");
        sb.AppendLine();
        sb.AppendLine("USER");
        if (!string.IsNullOrWhiteSpace(identities.UserName))
            sb.AppendLine($"Name: {identities.UserRef}");
        sb.AppendLine();
        sb.AppendLine($"{identities.CompanionRef} is the companion generating the current response.");
        sb.AppendLine($"{identities.UserRef} is the user {identities.CompanionRef} is speaking to.");
        sb.AppendLine();
        sb.AppendLine($"When {identities.CompanionRef} speaks:");
        sb.AppendLine($"- I / me / my refers to {identities.CompanionRef}.");
        sb.AppendLine($"- you / your refers to {identities.UserRef}.");
        sb.AppendLine($"- user-owned information describes {identities.UserRef}.");
        sb.AppendLine($"- companion-owned information describes {identities.CompanionRef}.");
        sb.AppendLine("- shared information may involve both.");
        sb.AppendLine($"Never transfer identity, preferences, memories, experiences, or relationships between {identities.CompanionRef} and {identities.UserRef}.");
        sb.AppendLine();
    }

    private static void RenderRelationship(StringBuilder sb, PromptIdentityContext identities)
    {
        if (identities.RelationshipLines.Count == 0)
            return;

        sb.AppendLine("# AUTHORITATIVE RELATIONSHIP");
        foreach (var line in identities.RelationshipLines)
            sb.AppendLine($"- {line}");
        sb.AppendLine();
    }

    private static string FormatMemory(ContextItem item, PromptIdentityContext? identities)
    {
        var companion = identities?.CompanionRef ?? "the companion";
        var user = identities?.UserRef ?? "the user";
        return item.Owner switch
        {
            MemoryOwner.Shared => $"SHARED EXPERIENCE - {user} + {companion}: {ProjectMemoryText(item.Text, item.Owner, user, companion)}",
            MemoryOwner.Companion => $"COMPANION MEMORY - {companion}: {ProjectMemoryText(item.Text, item.Owner, user, companion)}",
            _ => $"USER MEMORY - {user}: {ProjectMemoryText(item.Text, item.Owner, user, companion)}",
        };
    }

    private static string ProjectMemoryText(string text, MemoryOwner owner, string user, string companion)
    {
        var trimmed = text.Trim();
        return owner switch
        {
            MemoryOwner.User => ReplaceLeadingUser(trimmed, user),
            MemoryOwner.Companion => ReplaceLeadingCompanion(trimmed, companion),
            MemoryOwner.Shared => EnsureSharedNames(trimmed, user, companion),
            _ => trimmed,
        };
    }

    private static string ReplaceLeadingUser(string text, string user)
    {
        if (text.StartsWith("The user's ", StringComparison.OrdinalIgnoreCase))
            return user + "'s " + text["The user's ".Length..];
        if (text.StartsWith("The user ", StringComparison.OrdinalIgnoreCase))
            return user + " " + text["The user ".Length..];
        if (text.StartsWith("User ", StringComparison.OrdinalIgnoreCase))
            return user + " " + text["User ".Length..];
        return text;
    }

    private static string ReplaceLeadingCompanion(string text, string companion)
    {
        if (text.StartsWith("You ", StringComparison.OrdinalIgnoreCase))
            return companion + " " + text["You ".Length..];
        if (text.StartsWith("The companion ", StringComparison.OrdinalIgnoreCase))
            return companion + " " + text["The companion ".Length..];
        return text;
    }

    private static string EnsureSharedNames(string text, string user, string companion)
    {
        if (text.Contains(user, StringComparison.OrdinalIgnoreCase)
            && text.Contains(companion, StringComparison.OrdinalIgnoreCase))
            return text;
        return $"{user} and {companion}: {text}";
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
