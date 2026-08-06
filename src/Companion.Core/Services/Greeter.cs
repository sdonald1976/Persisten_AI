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

    private readonly IProjectStore _projects;
    private readonly IMemoryStore _memories;

    public Greeter(IProjectStore projects, IMemoryStore memories)
    {
        _projects = projects;
        _memories = memories;
    }

    public async Task<Greeting> GreetAsync(string userId, CancellationToken ct = default)
    {
        var openLoops = await _projects.GetOpenLoopsAsync(userId, onlyOpen: true, ct);
        var projects = (await _projects.GetProjectsAsync(userId, ct))
            .OrderByDescending(p => p.LastActivityAt)
            .ToList();
        var memories = await _memories.GetRetrievableMemoriesAsync(userId, ct);

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

        var hasHistory = openLoops.Count > 0 || projects.Count > 0 || memories.Count > 0;
        var message = hasHistory
            ? "Hey — you don't have to figure out how to start. Here's where we left things; " +
              "pick whatever you feel like, or ignore them all and just say what's on your mind."
            : "Hey — and you don't need an opener ready. We haven't talked before, so there's " +
              "nothing to catch up on yet. Tell me anything — what you're working on, something " +
              "on your mind — or ask \"what can you do?\"";

        return new Greeting { Message = message, Openers = openers };
    }

    private static string LowerFirst(string text)
        => string.IsNullOrEmpty(text) ? text : char.ToLowerInvariant(text[0]) + text[1..];
}
