using System.Text;
using Companion.Core.Domain;

namespace Companion.Core.Services;

/// <summary>What one render produced: the prompt text, and what had to be left out to fit.</summary>
public sealed record PacketRender(string Text, IReadOnlyList<string> Dropped)
{
    public bool Trimmed => Dropped.Count > 0;
}

/// <summary>
/// Renders a <see cref="ContextPacket"/> into the system prompt text the chat model sees.
/// Sections are clearly delimited and every fact is labeled by provenance so the model is
/// told what is a direct statement, what is inferred, and what may be outdated.
/// All wording comes from the <see cref="Prompts"/> catalog (editable at runtime); this class
/// owns only the structure — which sections exist, in what order, from which packet fields.
///
/// It also owns the guarantee that the prompt FITS. Every model has a context window, and a
/// prompt that exceeds it is not rejected — llama.cpp and friends silently discard the oldest
/// tokens and answer from what is left. Measured against this project's own configuration: a
/// ~7,800-token prompt came back reporting 2,050 tokens consumed, with the entire system prompt
/// gone, and the model then denied — fluently and with complete confidence — that the discarded
/// content had ever existed. That failure has no error, no warning, and no trace in the reply;
/// it reads exactly like the companion having lost her memory and started a new conversation.
///
/// So the trimming decision is taken here, where it can be ordered by what actually matters and
/// reported afterwards, rather than left to a truncation that starts at the top of the prompt —
/// which is precisely where identity lives.
/// </summary>
public static class ContextPacketRenderer
{
    /// <summary>
    /// Per-message ceiling for the recent-conversation section (~130 words). Long enough that a
    /// normal turn is never touched; short enough that one pasted document can't own the prompt.
    /// </summary>
    private const int MaxRecentMessageChars = 800;

    /// <summary>
    /// Budget for the recent-conversation section as a whole (~700 tokens). Filled NEWEST-FIRST:
    /// when a transcript is heavy, the turns nearest to now are the ones that matter for
    /// continuity, and the oldest are dropped rather than letting the section grow without limit.
    /// </summary>
    private const int RecentSectionCharBudget = 2800;

    /// <summary>
    /// Ceiling for any single note/bullet (memory line, preference, attention item, procedure,
    /// mood, musing…). These are meant to be a sentence or two; one that arrives enormous is a
    /// generation bug upstream, and it must not be allowed to crowd out the rest of the prompt.
    /// </summary>
    private const int MaxNoteChars = 400;

    /// <summary>
    /// What each section is worth when the prompt will not fit, highest kept longest.
    ///
    /// The ordering is a claim about what the companion IS. Who she is and who she is talking to
    /// are not negotiable and never appear here — they are written before any budget is consulted.
    /// After that, the thread of the current conversation outranks everything, because losing it
    /// is the failure a person actually notices; then this turn's fresh lookups, which the reply
    /// may already be quoting; then what she is supposed to be attending to; then what she
    /// remembers; then the colour — mood, musings, curiosities — which shape a reply but whose
    /// absence costs nothing a person would catch. Debug output goes first, always.
    /// </summary>
    private static class Rank
    {
        public const int Recent = 900;
        public const int Tools = 800;
        public const int Clarification = 750;
        public const int Attention = 700;
        public const int OpenLoops = 690;
        public const int Project = 680;
        public const int SharedMemories = 650;
        public const int DirectMemories = 640;
        public const int Capabilities = 600;
        public const int Procedures = 590;
        public const int Preferences = 580;
        public const int Mood = 500;
        public const int Register = 495;
        public const int Familiarity = 490;
        public const int Relationship = 485;
        public const int Temporal = 480;
        public const int Musing = 400;
        public const int Curiosity = 395;
        public const int Perspectives = 350;
        public const int InferredMemories = 300;
        public const int Uncertainty = 250;
        public const int OutdatedMemories = 200;
        public const int Diagnostics = 100;
    }

    /// <summary>One optional block: what it is, what it is worth, and the text it contributes.</summary>
    private sealed record Section(string Name, int Rank, string Text);

    private static string Clip(string text, int max)
        => string.IsNullOrEmpty(text) || text.Length <= max ? text : text[..max] + " […]";

    /// <summary>Rough token estimate (~4 chars/token), the same measure used for every other budget.</summary>
    private static int Tokens(string text) => ContextAssembler.EstimateTokens(text);

    /// <summary>
    /// The recent turns that fit the section budget, still in chronological order. Always keeps
    /// at least the most recent message, however long it is (clipped) — a reply with no
    /// immediate context is worse than a big prompt.
    /// </summary>
    private static IReadOnlyList<Message> WithinBudget(IReadOnlyList<Message> messages, int charBudget)
    {
        var kept = new List<Message>(messages.Count);
        var used = 0;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var cost = Math.Min(messages[i].Content.Length, MaxRecentMessageChars);
            if (kept.Count > 0 && used + cost > charBudget)
                break;
            used += cost;
            kept.Add(messages[i]);
        }
        kept.Reverse();
        return kept;
    }

    public static string Render(ContextPacket packet) => Build(packet).Text;

    /// <summary>
    /// Renders the packet and reports what was left out. The report is the point: a prompt that
    /// silently loses half its content produces a reply that is confidently wrong with nothing
    /// anywhere to say why.
    /// </summary>
    public static PacketRender Build(ContextPacket packet)
    {
        // ---- 1. The part that is never negotiable ----
        // Who she is, who she is speaking to, and the standing rules. If these are missing she is
        // not a different companion, she is a bare model — which is the failure being prevented.
        var core = RenderCore(packet);

        // ---- 2. Everything else, in the order it is emitted ----
        var sections = new List<Section>();

        void Prose(string name, int rank, string headerKey, string? body, string? rulesKey = null)
        {
            var text = ProseSection(headerKey, body, rulesKey);
            if (text.Length > 0)
                sections.Add(new Section(name, rank, text));
        }

        void Bullets(string name, int rank, string headerKey, IEnumerable<string> items, string? rulesKey = null)
        {
            var text = BulletSection(headerKey, items, rulesKey);
            if (text.Length > 0)
                sections.Add(new Section(name, rank, text));
        }

        // Her own state, not a fact about the user. Prose so it can't read as a memory item.
        Prose("mood", Rank.Mood, "renderer.mood.header", packet.MoodNote, "renderer.mood.rules");
        Prose("register", Rank.Register, "renderer.register.header", packet.RegisterNote);
        // Calibration, not content: it sets how casual/teasing/shorthand she may be.
        Prose("familiarity", Rank.Familiarity, "renderer.familiarity.header", packet.FamiliarityNote);
        // Tone guidance, not a fact — prose (no "- " bullet) so it never reads as a remembered item.
        Prose("relationship", Rank.Relationship, "renderer.relationship.header", packet.RelationshipNote);
        Prose("temporal", Rank.Temporal, "renderer.temporal.header", packet.TemporalNote, "renderer.temporal.rules");
        Bullets("attention", Rank.Attention, "CURRENT ATTENTION", packet.AttentionNotes);
        Prose("capabilities", Rank.Capabilities, "CURRENT CAPABILITIES", packet.CapabilityNote);
        // The companion's own between-session thought: a musing to hold loosely, never a fact.
        Prose("musing", Rank.Musing, "renderer.musing.header", packet.Musing, "renderer.musing.rules");
        Prose("curiosity", Rank.Curiosity, "renderer.curiosity.header", packet.CuriosityQuestion, "renderer.curiosity.rules");

        var projectText = RenderProject(packet);
        if (projectText.Length > 0)
            sections.Add(new Section("project", Rank.Project, projectText));

        if (packet.OpenLoops.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine(Prompts.Get("renderer.openloops.header"));
            foreach (var loop in packet.OpenLoops)
                sb.AppendLine($"- {loop.OpenLoop.Description}");
            sb.AppendLine();
            sections.Add(new Section("open loops", Rank.OpenLoops, sb.ToString()));
        }

        if (!string.IsNullOrWhiteSpace(packet.ClarificationQuestion))
        {
            var sb = new StringBuilder();
            sb.AppendLine(Prompts.Get("renderer.ambiguous.header"));
            sb.AppendLine(Prompts.Format("renderer.ambiguous.line", ("question", packet.ClarificationQuestion)));
            sb.AppendLine();
            sections.Add(new Section("clarification", Rank.Clarification, sb.ToString()));
        }

        // Shared moments are their own section regardless of provenance: they're history you were
        // both part of, told as "remember when we…", never as bare facts about the user.
        var shared = packet.Memories.Where(i => i.Owner == MemoryOwner.Shared).ToList();
        var rest = packet.Memories.Where(i => i.Owner != MemoryOwner.Shared).ToList();
        var direct = rest.Where(i => i.Provenance == ContextProvenance.DirectStatement).ToList();
        var inferred = rest.Where(i => i.Provenance == ContextProvenance.Inferred).ToList();
        var outdated = rest.Where(i => i.Provenance == ContextProvenance.Outdated).ToList();

        Bullets("shared memories", Rank.SharedMemories, "renderer.shared.header",
            shared.Select(i => FormatMemory(i, packet.Identities)), "renderer.shared.rules");
        Bullets("shared perspectives", Rank.Perspectives,
            "SHARED EXPERIENCE PERSPECTIVES (interpretations, not objective facts)", packet.SharedPerspectiveNotes);
        Bullets("direct memories", Rank.DirectMemories, "renderer.direct.header",
            direct.Select(i => FormatMemory(i, packet.Identities)));
        Bullets("inferred memories", Rank.InferredMemories, "renderer.inferred.header",
            inferred.Select(i => FormatMemory(i, packet.Identities)));
        Bullets("outdated memories", Rank.OutdatedMemories, "renderer.outdated.header",
            outdated.Select(i => FormatMemory(i, packet.Identities)));
        Bullets("procedures", Rank.Procedures, "USER-TAUGHT PROCEDURES", packet.ProcedureNotes);
        Bullets("preferences", Rank.Preferences, "renderer.preferences.header",
            packet.PreferenceNotes, "renderer.preferences.rules");
        // Fresh lookups the companion made this turn — grounded data for THIS reply only.
        Prose("tool results", Rank.Tools, "renderer.tools.header", packet.ToolResults, "renderer.tools.rules");
        Bullets("uncertainty", Rank.Uncertainty, "renderer.uncertainty.header", packet.UncertaintyNotes);
        Bullets("diagnostics", Rank.Diagnostics, "DEBUG CONTEXT SELECTION", packet.Diagnostics);

        // ---- 3. Fit ----
        var budget = packet.MaxPromptTokens;

        if (budget <= 0)
        {
            var untrimmed = Insert(sections, RenderRecent(packet, RecentSectionCharBudget));
            return new PacketRender(Emit(core, untrimmed), Array.Empty<string>());
        }

        var used = Tokens(core);

        // The transcript is guaranteed, not merely ranked first.
        //
        // Ranking it highest was not enough, and the test that caught this is worth keeping in
        // mind: on a tight budget the standing rules alone can consume everything, so a purely
        // rank-ordered fit dropped the conversation while dutifully preserving the instructions
        // telling her to remember it. She would have been perfectly herself, and had no idea what
        // was just said. So it gets the same standing as identity — shortened as far as the
        // newest exchange, never removed.
        var recentText = RenderRecent(packet, RecentCharBudget(budget - used));
        used += Tokens(recentText);

        var keep = new HashSet<string>();
        var dropped = new List<string>();

        // Chosen by worth, emitted in canonical order. A section that does not fit is skipped
        // rather than ending the loop: a small low-ranked note costs almost nothing, and there is
        // no reason to discard it because one large section above it could not be afforded.
        foreach (var section in sections.OrderByDescending(s => s.Rank).ThenBy(s => s.Name, StringComparer.Ordinal))
        {
            var cost = Tokens(section.Text);
            if (used + cost <= budget)
            {
                used += cost;
                keep.Add(section.Name);
            }
            else
            {
                dropped.Add(section.Name);
            }
        }

        var fitted = Insert(sections.Where(s => keep.Contains(s.Name)).ToList(), recentText);
        return new PacketRender(Emit(core, fitted), dropped);
    }

    /// <summary>
    /// How many characters the transcript may use. It keeps its normal allowance while there is
    /// room, shrinks when there is not, and never reaches zero — <see cref="WithinBudget"/> always
    /// returns the newest message, so a squeezed prompt still knows what was just said.
    /// </summary>
    private static int RecentCharBudget(int tokensLeft)
        => Math.Clamp(tokensLeft * 4, 0, RecentSectionCharBudget);

    /// <summary>
    /// Puts the transcript back in its canonical position — after the companion's own state and
    /// before the project and memory sections — rather than wherever the budget decided it.
    /// </summary>
    private static List<Section> Insert(IReadOnlyList<Section> sections, string recentText)
    {
        var result = sections.ToList();
        if (recentText.Length == 0)
            return result;

        var recent = new Section("recent conversation", Rank.Recent, recentText);
        var at = result.FindIndex(s => s.Rank is Rank.Project or Rank.OpenLoops or Rank.Clarification
            or Rank.SharedMemories or Rank.DirectMemories or Rank.InferredMemories
            or Rank.OutdatedMemories or Rank.Perspectives or Rank.Procedures or Rank.Preferences
            or Rank.Tools or Rank.Uncertainty or Rank.Diagnostics);

        result.Insert(at < 0 ? result.Count : at, recent);
        return result;
    }

    private static string Emit(string core, IReadOnlyList<Section> sections)
    {
        var sb = new StringBuilder(core);
        foreach (var section in sections)
            sb.Append(section.Text);
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Identity, persona, and the standing rules — written before any budget is consulted.
    ///
    /// Deliberately not a <see cref="Section"/>: there is no configuration and no arithmetic that
    /// can drop this. A prompt without it is not a smaller prompt, it is a different entity, and
    /// the whole point of a persistent companion is that it stays the same one.
    /// </summary>
    private static string RenderCore(ContextPacket packet)
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

        sb.AppendLine(Prompts.Get("renderer.conversation-rules"));
        sb.AppendLine();

        sb.AppendLine(Prompts.Get("renderer.finish-task"));
        sb.AppendLine();

        return sb.ToString();
    }

    private static string RenderRecent(ContextPacket packet, int charBudget)
    {
        if (packet.RecentMessages.Count == 0)
            return "";

        var sb = new StringBuilder();
        sb.AppendLine(Prompts.Get("renderer.recent.header"));
        foreach (var m in WithinBudget(packet.RecentMessages, charBudget))
        {
            var label = m.Role switch
            {
                MessageRole.User => packet.Identities?.UserLabel ?? "[USER]",
                MessageRole.Assistant => packet.Identities?.CompanionLabel ?? "[COMPANION]",
                _ => "[SYSTEM]",
            };
            sb.AppendLine(label);
            // One pasted wall of text (a spec, a log, an article) would otherwise crowd out
            // every other section for the rest of the conversation. Clipping a single long
            // turn keeps continuity — the gist and the opening are what later turns need —
            // without letting transcript length become an unbounded prompt cost.
            sb.AppendLine(Clip(m.Content, MaxRecentMessageChars));
        }
        sb.AppendLine();
        return sb.ToString();
    }

    private static string RenderProject(ContextPacket packet)
    {
        if (packet.Project is not { } summary)
            return "";

        var sb = new StringBuilder();
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
        return sb.ToString();
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

    /// <summary>A header + prose body (+ optional trailing rules line), empty when the body is.</summary>
    private static string ProseSection(string headerKey, string? body, string? rulesKey = null)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "";
        var sb = new StringBuilder();
        sb.AppendLine(Prompts.Exists(headerKey) ? Prompts.Get(headerKey) : $"## {headerKey}");
        // Tool results carry their own (larger) budget from the loop; every other prose section
        // is a note that should be a sentence or two, so a runaway one is a bug, not content.
        sb.AppendLine(headerKey == "renderer.tools.header"
            ? body!.Trim()
            : Clip(body!.Trim(), MaxNoteChars));
        if (rulesKey is not null)
            sb.AppendLine(Prompts.Get(rulesKey));
        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>A header + bullet list (+ optional trailing rules line), empty when there are none.</summary>
    private static string BulletSection(string headerKey, IEnumerable<string> items, string? rulesKey = null)
    {
        var list = items.ToList();
        if (list.Count == 0)
            return "";
        var sb = new StringBuilder();
        sb.AppendLine(Prompts.Exists(headerKey) ? Prompts.Get(headerKey) : $"## {headerKey}");
        foreach (var item in list)
            sb.AppendLine($"- {Clip(item, MaxNoteChars)}");
        if (rulesKey is not null)
            sb.AppendLine(Prompts.Get(rulesKey));
        sb.AppendLine();
        return sb.ToString();
    }
}
