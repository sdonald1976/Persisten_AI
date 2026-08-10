using Companion.Core.Abstractions;
using Companion.Core.Domain;

namespace Companion.Core.Services;

/// <summary>
/// Builds a warm, low-pressure opener from what the companion remembers — open loops first (the
/// things you meant to get back to), then recent projects. Deterministic and offline: the openers
/// are real, never invented, and the message always makes clear you can ignore them and just talk.
/// </summary>
public sealed class Greeter : IGreeter
{
    private const int MaxOpeners = 3;

    // Below this, a "gap" isn't worth acknowledging — you were basically still here.
    private static readonly TimeSpan MinGapToAcknowledge = TimeSpan.FromMinutes(30);

    private readonly IProjectStore _projects;
    private readonly IMemoryStore _memories;
    private readonly IConversationStore _conversations;
    private readonly TimeProvider _clock;

    public Greeter(IProjectStore projects, IMemoryStore memories, IConversationStore conversations, TimeProvider clock)
    {
        _projects = projects;
        _memories = memories;
        _conversations = conversations;
        _clock = clock;
    }

    public async Task<Greeting> GreetAsync(string userId, CancellationToken ct = default)
    {
        var openLoops = await _projects.GetOpenLoopsAsync(userId, onlyOpen: true, ct);
        var projects = (await _projects.GetProjectsAsync(userId, ct))
            .OrderByDescending(p => p.LastActivityAt)
            .ToList();
        var memories = await _memories.GetRetrievableMemoriesAsync(userId, ct);

        // How long since we last talked? Only surfaced when it's a real gap.
        var lastSeen = await _conversations.GetLastMessageAtAsync(userId, ct);
        string? timeContext = null;
        if (lastSeen is { } seen)
        {
            var gap = _clock.GetUtcNow() - seen;
            if (gap >= MinGapToAcknowledge)
                timeContext = RelativeTime.Describe(gap);
        }

        var openers = new List<string>();

        // Unfinished business first — the most natural place to resume.
        foreach (var loop in openLoops.Take(2))
            openers.Add($"Pick up where we left off — {LowerFirst(loop.Description.TrimEnd('.'))}?");

        // Then recent projects that aren't already covered by an open loop above.
        var loopProjectIds = openLoops.Select(l => l.ProjectId).ToHashSet();
        foreach (var project in projects.Where(p => !loopProjectIds.Contains(p.Id)).Take(MaxOpeners - openers.Count))
            openers.Add($"How's {project.Name} going?");

        // A gentle catch-all when there's some history but nothing actionable surfaced.
        if (openers.Count == 0 && memories.Count > 0)
            openers.Add("Ask me what I remember about you.");

        // A prior message means we've talked before, even if nothing durable was remembered.
        var hasHistory = lastSeen is not null || openLoops.Count > 0 || projects.Count > 0 || memories.Count > 0;

        string message;
        if (hasHistory)
        {
            var lead = timeContext is null ? "Hey — good to see you." : $"It's been {timeContext}. Good to see you back.";
            message = lead + " Here's where we left things; " +
                      "pick whatever you feel like, or ignore them all and just say what's on your mind.";
        }
        else
        {
            message = "Hey — we haven't talked before, so there's nothing to catch up on yet. " +
                      "Tell me anything — what you're working on, something on your mind — or ask \"what can you do?\"";
        }

        return new Greeting { Message = message, TimeContext = timeContext, Openers = openers };
    }

    private static string LowerFirst(string text)
        => string.IsNullOrEmpty(text) ? text : char.ToLowerInvariant(text[0]) + text[1..];
}
