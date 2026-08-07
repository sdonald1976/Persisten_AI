using System.Globalization;
using System.Text;
using Companion.Core.Abstractions;
using Companion.Core.Domain;

namespace Companion.Core.Services;

/// <summary>
/// The companion brain (see <see cref="IAgent"/>): parse intent → run a turn or carry out an
/// action → return structured <see cref="AgentReply"/> data. It holds no per-conversation state;
/// anything it needs (e.g. the last exchange, for feedback) is read back from the stores, so the
/// same brain serves many concurrent faces and survives restarts.
/// </summary>
public sealed class Agent : IAgent
{
    private readonly IIntentParser _intents;
    private readonly ICompanion _companion;
    private readonly IConversationStore _conversations;
    private readonly IMemoryStore _memories;
    private readonly IMemoryCurator _curator;
    private readonly IProfileStore _profiles;
    private readonly IFeedbackStore _feedback;
    private readonly IProjectStore _projects;
    private readonly IMemoryConsolidator _consolidator;
    private readonly IGreeter _greeter;
    private readonly IPersonalityService _personality;
    private readonly TimeProvider _clock;

    public Agent(
        IIntentParser intents,
        ICompanion companion,
        IConversationStore conversations,
        IMemoryStore memories,
        IMemoryCurator curator,
        IProfileStore profiles,
        IFeedbackStore feedback,
        IProjectStore projects,
        IMemoryConsolidator consolidator,
        IGreeter greeter,
        IPersonalityService personality,
        TimeProvider clock)
    {
        _intents = intents;
        _companion = companion;
        _conversations = conversations;
        _memories = memories;
        _curator = curator;
        _profiles = profiles;
        _feedback = feedback;
        _projects = projects;
        _consolidator = consolidator;
        _greeter = greeter;
        _personality = personality;
        _clock = clock;
    }

    public async Task<AgentReply> HandleAsync(
        string userId, Guid conversationId, string input,
        IProgress<string>? tokenSink = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input))
            return AgentReply.Act(IntentKind.Chat, "");

        var intent = _intents.Parse(input);
        return intent.Kind switch
        {
            IntentKind.Chat => await ChatAsync(userId, conversationId, input, tokenSink, ct),
            IntentKind.Recall => await RecallAsync(userId, intent.Argument, ct),
            IntentKind.Forget => await ForgetAsync(userId, conversationId, intent.Argument, ct),
            IntentKind.Dispute => await DisputeAsync(userId, conversationId, ct),
            IntentKind.ListProjects => await ListProjectsAsync(userId, ct),
            IntentKind.ListOpenLoops => await ListOpenLoopsAsync(userId, ct),
            IntentKind.Consolidate => await ConsolidateAsync(userId, ct),
            IntentKind.SetPersona => await SetPersonaAsync(userId, intent.Argument, ct),
            IntentKind.SetPersonality => await SetPersonalityAsync(userId, intent.Argument, ct),
            IntentKind.AdjustStyle => await AdjustStyleAsync(userId, intent.Argument, ct),
            IntentKind.FeedbackPositive => await FeedbackAsync(userId, conversationId, FeedbackRating.Positive, intent.Argument, ct),
            IntentKind.FeedbackNegative => await FeedbackAsync(userId, conversationId, FeedbackRating.Negative, intent.Argument, ct),
            IntentKind.PrivacyDoNotRemember => await SetPrivacyAsync(userId, conversationId, ct),
            IntentKind.Greeting => await GreetAsync(userId, ct),
            _ => await ChatAsync(userId, conversationId, input, tokenSink, ct),
        };
    }

    public async Task<AgentReply> ConfirmAsync(
        string userId, Guid conversationId, string confirmationToken, bool confirmed, CancellationToken ct = default)
    {
        if (!Guid.TryParse(confirmationToken, out var memoryId))
            return AgentReply.Act(IntentKind.Forget, "That confirmation is no longer valid.");

        if (!confirmed)
            return AgentReply.Act(IntentKind.Forget, "Kept it.");

        await _curator.ForgetAsync(userId, memoryId, "user asked to forget", ct);
        return AgentReply.Act(IntentKind.Forget, "Forgotten. I won't bring that up again.");
    }

    // ---- conversational turn ----

    private async Task<AgentReply> ChatAsync(
        string userId, Guid conversationId, string input, IProgress<string>? tokenSink, CancellationToken ct)
    {
        var trace = await _companion.RespondAsync(userId, conversationId, input, tokenSink, ct);
        return new AgentReply
        {
            Kind = AgentReplyKind.Chat,
            Intent = IntentKind.Chat,
            Text = trace.Response,
            Trace = trace,
        };
    }

    // ---- intent handlers (return spoken text) ----

    private async Task<AgentReply> RecallAsync(string userId, string? topic, CancellationToken ct)
    {
        var memories = await _memories.GetRetrievableMemoriesAsync(userId, ct);
        if (memories.Count == 0)
            return AgentReply.Act(IntentKind.Recall, "I don't have any memories for you yet.");

        var sb = new StringBuilder();
        IEnumerable<IMemory> selected;
        if (!string.IsNullOrWhiteSpace(topic))
        {
            selected = memories
                .Select(m => (m, score: ScoreMath.KeywordOverlap(topic, m.Content)))
                .Where(x => x.score > 0)
                .OrderByDescending(x => x.score)
                .Select(x => x.m)
                .ToList();
            sb.AppendLine($"What I remember about \"{topic}\":");
        }
        else
        {
            selected = memories.OrderByDescending(m => m.EffectiveAt).ToList();
            sb.AppendLine($"I currently remember {memories.Count} things about you:");
        }

        var any = false;
        foreach (var m in selected)
        {
            any = true;
            var tag = m.Kind == MemoryKind.Semantic ? "fact " : "event";
            var status = m is SemanticMemory { Validity: not Validity.Current } sm ? $" [{sm.Validity}]" : "";
            sb.AppendLine($"  - [{m.Id.ToString()[..8]}] ({tag}) {m.Content}{status}");
        }
        if (!any)
            sb.AppendLine("  (nothing on that topic)");

        return AgentReply.Act(IntentKind.Recall, sb.ToString().TrimEnd());
    }

    private async Task<AgentReply> ForgetAsync(string userId, Guid conversationId, string? reference, CancellationToken ct)
    {
        var target = await ResolveMemoryAsync(userId, conversationId, reference, ct);
        if (target is null)
            return AgentReply.Act(IntentKind.Forget,
                "I'm not sure which memory you mean — try naming it, e.g. \"forget the low-carb thing\".");

        return new AgentReply
        {
            Kind = AgentReplyKind.Confirmation,
            Intent = IntentKind.Forget,
            Text = $"Forget this? \"{Truncate(target.Content)}\"",
            ConfirmationToken = target.Id.ToString(),
        };
    }

    private async Task<AgentReply> DisputeAsync(string userId, Guid conversationId, CancellationToken ct)
    {
        var target = await ResolveMemoryAsync(userId, conversationId, null, ct);
        if (target is null)
            return AgentReply.Act(IntentKind.Dispute, "I'm not sure which memory is wrong — mention it and I'll flag it.");

        await _curator.DisputeAsync(userId, target.Id, "user said it's wrong", ct);
        return AgentReply.Act(IntentKind.Dispute,
            $"Got it — I've flagged \"{Truncate(target.Content)}\" as disputed and won't rely on it.");
    }

    private async Task<AgentReply> ListProjectsAsync(string userId, CancellationToken ct)
    {
        var projects = await _projects.GetProjectsAsync(userId, ct);
        if (projects.Count == 0)
            return AgentReply.Act(IntentKind.ListProjects, "You don't have any projects yet.");

        var sb = new StringBuilder();
        sb.AppendLine($"Projects ({projects.Count}):");
        foreach (var p in projects.OrderByDescending(p => p.LastActivityAt))
            sb.AppendLine($"  - {p.Name}  [{p.Status}]");
        return AgentReply.Act(IntentKind.ListProjects, sb.ToString().TrimEnd());
    }

    private async Task<AgentReply> ListOpenLoopsAsync(string userId, CancellationToken ct)
    {
        var loops = await _projects.GetOpenLoopsAsync(userId, onlyOpen: true, ct);
        if (loops.Count == 0)
            return AgentReply.Act(IntentKind.ListOpenLoops, "No open loops — nothing unfinished on my radar.");

        var sb = new StringBuilder();
        sb.AppendLine($"Open loops ({loops.Count}):");
        foreach (var l in loops)
            sb.AppendLine($"  - {l.Description}");
        return AgentReply.Act(IntentKind.ListOpenLoops, sb.ToString().TrimEnd());
    }

    private async Task<AgentReply> ConsolidateAsync(string userId, CancellationToken ct)
    {
        var result = await _consolidator.ConsolidateAsync(userId, ct);
        if (result.Created.Count == 0)
            return AgentReply.Act(IntentKind.Consolidate, "Nothing to consolidate yet.");

        var sb = new StringBuilder();
        sb.AppendLine($"Consolidated {result.Created.Count} group(s):");
        foreach (var g in result.Created)
            sb.AppendLine($"  - {g.Content}  (from {g.SourceMemoryIds.Count} memories, {g.EvidenceCount} evidence)");
        return AgentReply.Act(IntentKind.Consolidate, sb.ToString().TrimEnd());
    }

    private async Task<AgentReply> GreetAsync(string userId, CancellationToken ct)
    {
        var greeting = await _greeter.GreetAsync(userId, ct);
        return AgentReply.Act(IntentKind.Greeting, greeting.ToDisplayText());
    }

    private async Task<AgentReply> SetPrivacyAsync(string userId, Guid conversationId, CancellationToken ct)
    {
        await _conversations.SetDoNotRememberAsync(conversationId, userId, true, ct);
        return AgentReply.Act(IntentKind.PrivacyDoNotRemember,
            "Got it — this conversation is private. I'll reply, but I won't save anything from it to long-term memory.");
    }

    private async Task<AgentReply> SetPersonaAsync(string userId, string? persona, CancellationToken ct)
    {
        await _profiles.SetPersonaAsync(userId, persona, ct);
        return AgentReply.Act(IntentKind.SetPersona, "Persona updated.");
    }

    private async Task<AgentReply> SetPersonalityAsync(string userId, string? name, CancellationToken ct)
    {
        // No specific choice → show the menu.
        if (string.IsNullOrWhiteSpace(name))
            return AgentReply.Act(IntentKind.SetPersonality, PersonalityMenu());

        var preset = _personality.Find(name);
        if (preset is null)
            return AgentReply.Act(IntentKind.SetPersonality,
                $"I don't have a \"{name}\" personality. {PersonalityMenu()}");

        await _profiles.SetPersonalityPresetAsync(userId, preset.Name, ct);
        return AgentReply.Act(IntentKind.SetPersonality,
            $"Done — I'll be {preset.Label.ToLowerInvariant()} from now on ({preset.Description}). " +
            "You can still fine-tune it any time (\"be more concise\").");
    }

    private string PersonalityMenu()
        => "I can be " +
           string.Join(", ", _personality.Presets.Select(p => $"{p.Name} ({p.Description})")) +
           ". Which would you like? (Say, e.g., \"switch to the witty personality\".)";

    private async Task<AgentReply> AdjustStyleAsync(string userId, string? directive, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(directive))
            return AgentReply.Act(IntentKind.AdjustStyle, "Tell me how you'd like me to sound.");

        var profile = await _profiles.GetOrCreateAsync(userId, ct);
        var line = "- " + char.ToUpper(directive[0], CultureInfo.InvariantCulture) + directive[1..].TrimEnd('.') + ".";
        var persona = string.IsNullOrWhiteSpace(profile.Persona) ? line : profile.Persona!.TrimEnd() + "\n" + line;
        await _profiles.SetPersonaAsync(userId, persona, ct);
        return AgentReply.Act(IntentKind.AdjustStyle, $"Got it — from now on I'll {directive.TrimEnd('.')}.");
    }

    private async Task<AgentReply> FeedbackAsync(
        string userId, Guid conversationId, FeedbackRating rating, string? note, CancellationToken ct)
    {
        var (userMessage, response) = await LastExchangeAsync(userId, conversationId, ct);
        if (response is null)
            return AgentReply.Act(
                rating == FeedbackRating.Positive ? IntentKind.FeedbackPositive : IntentKind.FeedbackNegative,
                "Nothing to rate yet — say something first.");

        await _feedback.AddAsync(new FeedbackRecord
        {
            UserId = userId,
            ConversationId = conversationId,
            UserMessage = userMessage ?? "",
            Response = response,
            Rating = rating,
            Note = note,
            CreatedAt = _clock.GetUtcNow(),
        }, ct);

        var total = await _feedback.CountAsync(userId, ct);
        var text = rating == FeedbackRating.Positive
            ? $"Thanks — glad that landed. ({total} ratings saved for tuning.)"
            : $"Noted — I'll aim to do better. ({total} ratings saved for tuning.)";
        return AgentReply.Act(
            rating == FeedbackRating.Positive ? IntentKind.FeedbackPositive : IntentKind.FeedbackNegative, text);
    }

    // ---- helpers ----

    /// <summary>Resolve which memory a plain-language reference points at, reading state from the stores.</summary>
    private async Task<IMemory?> ResolveMemoryAsync(
        string userId, Guid conversationId, string? reference, CancellationToken ct)
    {
        var memories = await _memories.GetRetrievableMemoriesAsync(userId, ct);
        if (memories.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(reference))
        {
            return memories
                .Select(m => (m, score: ScoreMath.KeywordOverlap(reference, m.Content)))
                .Where(x => x.score > 0)
                .OrderByDescending(x => x.score)
                .Select(x => x.m)
                .FirstOrDefault();
        }

        // "that" / "it" with no name → the most recent active memory (the thing most likely just discussed).
        return memories
            .Where(m => m.Status == MemoryStatus.Active)
            .OrderByDescending(m => m.CreatedAt)
            .FirstOrDefault();
    }

    /// <summary>The last (user message, assistant response) pair in a conversation, read from the store.</summary>
    private async Task<(string? UserMessage, string? Response)> LastExchangeAsync(
        string userId, Guid conversationId, CancellationToken ct)
    {
        var recent = await _conversations.GetRecentMessagesAsync(conversationId, userId, 10, ct);
        for (var i = recent.Count - 1; i >= 0; i--)
        {
            if (recent[i].Role != MessageRole.Assistant)
                continue;
            var response = recent[i].Content;
            string? userMessage = null;
            for (var j = i - 1; j >= 0; j--)
            {
                if (recent[j].Role == MessageRole.User)
                {
                    userMessage = recent[j].Content;
                    break;
                }
            }
            return (userMessage, response);
        }
        return (null, null);
    }

    private static string Truncate(string text, int max = 80)
        => text.Length <= max ? text : text[..max] + "…";
}
